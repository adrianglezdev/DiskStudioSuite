using System.IO;
using System.IO.Compression;
using System.Management;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DiskStudio.Core;

/// <summary>Operaciones a nivel de sector: imágenes, clonado de disco y análisis de superficie. Requieren administrador.</summary>
public static class DiskImage
{
    const int Chunk = 1024 * 1024;   // múltiplo de cualquier tamaño de sector

    public class SurfaceBlock { public long Offset; public int State; } // 0 ok, 1 lento, 2 error

    // ───────────── Crear imagen ─────────────
    public static TransferJob CreateImageJob(PhysicalDisk disk, string file, bool compress) => new()
    {
        Title = $"Imagen del disco {disk.Number}",
        Subtitle = $"{disk.Title} → {file}",
        Glyph = Icons.Image,
        Work = ctx => Task.Run(() =>
        {
            using var src = OpenStream(disk.Device, false, out long len);
            ctx.SetTotal(len);
            using var sha = SHA256.Create();
            using var fout = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, Chunk);
            Stream outS = compress ? new GZipStream(fout, CompressionLevel.Fastest, true) : fout;
            var buf = new byte[Chunk];
            long bad = 0;
            var blocks = new ulong[(len + Chunk - 1) / Chunk];
            for (long off = 0; off < len; off += Chunk)
            {
                ctx.Check();
                int n = (int)Math.Min(Chunk, len - off);
                if (!ReadAt(src, off, buf, n)) { bad++; Array.Clear(buf, 0, n); ctx.Fail($"Sector {off / 512}", "Error de lectura (rellenado con ceros)"); }
                sha.TransformBlock(buf, 0, n, null, 0);
                blocks[off / Chunk] = IncrementalImage.HashBlock(buf.AsSpan(0, n));
                outS.Write(buf, 0, n);
                ctx.AddBytes(n);
                ctx.SetCurrent($"Leyendo {Fmt.Size(off)} de {Fmt.Size(len)}");
            }
            sha.TransformFinalBlock([], 0, 0);
            if (compress) outS.Dispose();
            string hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            File.WriteAllText(file + ".sha256", $"{hash} *{Path.GetFileName(file)}  (datos sin comprimir, {len} bytes, {disk.Title})\n");
            IncrementalImage.SaveBlocksForFull(file, len, blocks);
            Log.Ok($"Imagen creada: {file} · SHA-256 {hash}" + (bad > 0 ? $" · {bad} bloques ilegibles" : ""));
        })
    };

    // ───────────── Restaurar imagen / clonar disco ─────────────
    public static TransferJob RestoreJob(string file, PhysicalDisk target) => new()
    {
        Title = $"Restaurar imagen en disco {target.Number}",
        Subtitle = $"{Path.GetFileName(file)} → {target.Title}",
        Glyph = Icons.Download,
        Work = ctx => Task.Run(() =>
        {
            ctx.SetCurrent("Abriendo la imagen…");
            using var src = OpenImage(file, out long total, out var cleanup);
            try { WriteToDisk(ctx, target, src, total); }
            finally { cleanup(); }
        })
    };

    /// <summary>Abre cualquier imagen admitida (.img, .img.gz, .vhd, .vhdx, .iso o incremental) como datos en bruto.</summary>
    public static Stream OpenImage(string file, out long length, out Action cleanup) =>
        IncrementalImage.IsIncremental(file) ? IncrementalImage.OpenChain(file, out length, out cleanup) : VirtualDisk.OpenSource(file, out length, out cleanup);

    public static TransferJob CloneDiskJob(PhysicalDisk source, PhysicalDisk target) => new()
    {
        Title = $"Clonar disco {source.Number} → disco {target.Number}",
        Subtitle = $"{source.Title} → {target.Title}",
        Glyph = Icons.Clone,
        Work = ctx => Task.Run(() =>
        {
            using var src = OpenStream(source.Device, false, out long len);
            WriteToDisk(ctx, target, src, len);
        })
    };

    static void WriteToDisk(JobContext ctx, PhysicalDisk target, Stream src, long total)
    {
        if (target.IsSystem || target.IsBoot) throw new InvalidOperationException("No se puede escribir en el disco del sistema.");
        var locks = new List<SafeFileHandle>();
        bool offline = false;
        try
        {
            ctx.SetCurrent("Desmontando volúmenes del disco destino…");
            foreach (var v in target.Volumes)
            {
                try
                {
                    var h = Native.OpenRaw(@"\\.\" + v.Letter, true);
                    Native.Ioctl(h, Native.FSCTL_LOCK_VOLUME);
                    Native.Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME);
                    locks.Add(h);
                }
                catch (Exception ex) { Log.Warn($"No se pudo bloquear {v.Letter}: {ex.Message}"); }
            }
            offline = SetDiskOnline(target.Number, false);

            using var dst = OpenStream(target.Device, true, out long dstLen);
            if (total > dstLen) throw new IOException($"El disco destino ({Fmt.Size(dstLen)}) es más pequeño que el origen ({Fmt.Size(total)}).");
            ctx.SetTotal(total > 0 ? total : dstLen);
            var buf = new byte[Chunk];
            long off = 0;
            while (true)
            {
                ctx.Check();
                int n = ReadFull(src, buf, Chunk);
                if (n <= 0) break;
                int aligned = (n + 511) / 512 * 512;
                if (aligned > n) Array.Clear(buf, n, aligned - n);
                dst.Position = off;
                dst.Write(buf, 0, aligned);
                off += n;
                ctx.AddBytes(n);
                ctx.SetCurrent($"Escribiendo {Fmt.Size(off)}");
                if (off >= dstLen) break;
            }
            dst.Flush();
            Log.Ok($"Escritos {Fmt.Size(off)} en el disco {target.Number} ({target.Title})");
        }
        finally
        {
            if (offline) SetDiskOnline(target.Number, true);
            foreach (var h in locks) { Native.Ioctl(h, Native.FSCTL_UNLOCK_VOLUME); h.Dispose(); }
            try { using var h = Native.OpenRaw(target.Device, false); Native.Ioctl(h, Native.IOCTL_DISK_UPDATE_PROPERTIES); } catch { }
        }
    }

    // ───────────── Verificar imagen ─────────────
    public static TransferJob VerifyImageJob(string file, PhysicalDisk disk) => new()
    {
        Title = $"Verificar imagen contra disco {disk.Number}",
        Subtitle = Path.GetFileName(file),
        Glyph = Icons.Check,
        Work = ctx => Task.Run(() =>
        {
            using var img = OpenImage(file, out long imgLen, out var cleanup);
            using var dev = OpenStream(disk.Device, false, out long len);
            ctx.SetTotal(Math.Min(len, imgLen));
            var a = new byte[Chunk]; var b = new byte[Chunk];
            long off = 0, diffs = 0;
            while (true)
            {
                ctx.Check();
                int n = ReadFull(img, a, Chunk);
                if (n <= 0 || off >= len) break;
                int m = (int)Math.Min(n, len - off);
                ReadAt(dev, off, b, (m + 511) / 512 * 512);
                if (!a.AsSpan(0, m).SequenceEqual(b.AsSpan(0, m))) { diffs++; if (diffs <= 50) ctx.Fail($"Offset {off}", "Los datos no coinciden"); }
                off += m; ctx.AddBytes(m);
                ctx.SetCurrent($"Comparando {Fmt.Size(off)}");
            }
            cleanup();
            if (diffs == 0) Log.Ok("La imagen coincide exactamente con el disco.");
            else Log.Warn($"{diffs} bloques distintos entre imagen y disco.");
        })
    };

    // ───────────── Análisis de superficie (solo lectura) ─────────────
    public static TransferJob SurfaceScanJob(PhysicalDisk disk, List<SurfaceBlock> blocks, Action changed) => new()
    {
        Title = $"Análisis de superficie del disco {disk.Number}",
        Subtitle = disk.Title + " · solo lectura",
        Glyph = Icons.Grid,
        Work = ctx => Task.Run(() =>
        {
            using var src = OpenStream(disk.Device, false, out long len);
            ctx.SetTotal(len);
            const int block = 8 * 1024 * 1024;
            var buf = new byte[block];
            var sw = new System.Diagnostics.Stopwatch();
            long slow = 0, bad = 0;
            for (long off = 0; off < len; off += block)
            {
                ctx.Check();
                int n = (int)Math.Min(block, len - off);
                sw.Restart();
                bool ok = ReadAt(src, off, buf, n);
                double ms = sw.Elapsed.TotalMilliseconds;
                int state = !ok ? 2 : ms > Math.Max(400, n / 1048576.0 * 40) ? 1 : 0;
                if (state == 1) slow++;
                if (state == 2) { bad++; ctx.Fail($"Bloque en {Fmt.Size(off)}", "Error de lectura"); }
                lock (blocks) blocks.Add(new SurfaceBlock { Offset = off, State = state });
                ctx.AddBytes(n);
                ctx.SetCurrent($"{Fmt.Size(off)} · {slow} lentos · {bad} con error");
                changed?.Invoke();
            }
            if (bad == 0) Log.Ok($"Superficie del disco {disk.Number} sin errores de lectura ({slow} bloques lentos).");
            else Log.Error($"Disco {disk.Number}: {bad} bloques con error de lectura, {slow} lentos.");
        })
    };

    // ───────────── Utilidades ─────────────
    static FileStream OpenStream(string device, bool write, out long length)
    {
        var h = Native.OpenRaw(device, write);
        length = Native.GetDeviceLength(h);
        return new FileStream(h, write ? FileAccess.ReadWrite : FileAccess.Read, 0, false);
    }

    static bool ReadAt(FileStream s, long off, byte[] buf, int n)
    {
        try
        {
            s.Position = off;
            int aligned = (n + 511) / 512 * 512;
            int got = 0;
            while (got < aligned)
            {
                int r = s.Read(buf, got, aligned - got);
                if (r <= 0) break;
                got += r;
            }
            return true;
        }
        catch (IOException) { return false; }
    }

    static int ReadFull(Stream s, byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = s.Read(buf, got, n - got);
            if (r <= 0) break;
            got += r;
        }
        return got;
    }

    static bool SetDiskOnline(int number, bool online)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number={number}"));
            foreach (ManagementObject d in s.Get())
            {
                d.InvokeMethod(online ? "Online" : "Offline", null);
                return true;
            }
        }
        catch { }
        return false;
    }

    // ───────────── Formatear y etiquetar ─────────────
    public static void Format(string letter, string fs, string label, bool quick)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Volume WHERE DriveLetter='{letter.TrimEnd(':')}'"));
        foreach (ManagementObject v in s.Get())
        {
            var p = v.GetMethodParameters("Format");
            p["FileSystem"] = fs;
            p["FileSystemLabel"] = label;
            p["Full"] = !quick;
            p["Force"] = true;
            var r = v.InvokeMethod("Format", p, null);
            uint code = Convert.ToUInt32(r?["ReturnValue"] ?? 0u);
            if (code != 0) throw new IOException($"Windows devolvió el código {code} al formatear.");
            return;
        }
        throw new IOException("No se encontró el volumen " + letter);
    }
}
