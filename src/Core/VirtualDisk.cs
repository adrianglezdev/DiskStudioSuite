using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskStudio.Core;

public class MountedImage
{
    public string Path { get; init; } = "";
    public int DiskNumber { get; init; } = -1;
    public string Letters { get; init; } = "";
    public bool ReadOnly { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Montar imágenes (ISO, VHD, VHDX e .img) y convertir entre formatos.</summary>
public static class VirtualDisk
{
    const int Chunk = 1 << 20;
    public static ObservableCollection<MountedImage> Mounted { get; } = [];
    public static readonly string[] Formats = [".img", ".img.gz", ".vhd", ".vhdx"];

    public static bool IsMountable(string p) => Ext(p) is ".iso" or ".vhd" or ".vhdx";
    public static string Ext(string p) => p.EndsWith(".img.gz", StringComparison.OrdinalIgnoreCase) ? ".img.gz" : System.IO.Path.GetExtension(p).ToLowerInvariant();

    // ───────────── PowerShell ─────────────
    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    public static (int code, string output) PowerShell(string script)
    {
        var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        p.StandardInput.WriteLine("[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ErrorActionPreference='Stop'; try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (o + err.Result).Trim());
    }

    // ───────────── Montar / desmontar ─────────────
    /// <summary>Monta una ISO, VHD o VHDX. Devuelve el disco (o -1 para ISO) y las letras asignadas.</summary>
    public static MountedImage Mount(string path, bool readOnly, bool assignLetters = true)
    {
        // En ISO las letras se leen de sus volúmenes; en VHD/VHDX, de las particiones del disco
        // («Get-DiskImage | Get-Volume» solo funciona con ISO).
        string script = $"$i = Mount-DiskImage -ImagePath {Q(path)} -PassThru" + (readOnly ? " -Access ReadOnly" : "") + (assignLetters ? "" : " -NoDriveLetter") + "; $p = " + Q(path) + "; " +
            "$n = -1; $l = ''; " +
            "for ($k = 0; $k -lt 30; $k++) { Start-Sleep -Milliseconds 500; $i = Get-DiskImage -ImagePath $p; " +
            "  if ($i.StorageType -ne 1) { $d = $i | Get-Disk; $n = $d.Number; $parts = Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue; " +
            "    $l = ($parts | Where-Object { [int][char]$_.DriveLetter -ne 0 } | ForEach-Object { \"$($_.DriveLetter):\" }) -join ' ' } " +
            "  else { $n = -1; $l = ($i | Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object { \"$($_.DriveLetter):\" }) -join ' ' }; " +
            "  if ($l -or -not " + (assignLetters ? "$true" : "$false") + ") { break } " +
            "  if ($k -eq 8 -and $n -ge 0) { if ($d.IsOffline) { Set-Disk -Number $n -IsOffline $false -ErrorAction SilentlyContinue }; " +
            "    $parts | Where-Object { [int][char]$_.DriveLetter -eq 0 -and $_.Type -in 'Basic','IFS','FAT32','FAT32 XINT13','Unknown' -and $_.Size -gt 32MB } | ForEach-Object { try { $_ | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction Stop } catch {} } } }; " +
            "\"DISK=$n\"; \"LETTERS=$l\"";
        var (code, o) = PowerShell(script);
        if (code != 0) throw new IOException("No se pudo montar la imagen: " + LastLine(o));
        var m = new MountedImage
        {
            Path = path, ReadOnly = readOnly,
            DiskNumber = int.TryParse(Regex.Match(o, @"DISK=(-?\d+)").Groups[1].Value, out int n) ? n : -1,
            Letters = Regex.Match(o, @"LETTERS=(.*)").Groups[1].Value.Trim()
        };
        void add() { foreach (var x in Mounted.Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList()) Mounted.Remove(x); Mounted.Add(m); }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) add(); else d.Invoke(add);
        return m;
    }

    public static void Dismount(string path)
    {
        var (code, o) = PowerShell($"Dismount-DiskImage -ImagePath {Q(path)} | Out-Null");
        void remove() { foreach (var x in Mounted.Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList()) Mounted.Remove(x); }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) remove(); else d.Invoke(remove);
        if (code != 0) throw new IOException("No se pudo desmontar: " + LastLine(o));
    }

    /// <summary>Desmonta un disco virtual por su número (también los montados fuera de la app).</summary>
    public static void DismountDisk(int number)
    {
        var (code, o) = PowerShell($"Dismount-DiskImage -DevicePath '\\\\.\\PHYSICALDRIVE{number}' | Out-Null");
        if (code != 0) throw new IOException("No se pudo desmontar: " + LastLine(o));
        foreach (var x in Mounted.Where(x => x.DiskNumber == number).ToList()) Mounted.Remove(x);
    }

    static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? s;

    // ───────────── Pie de VHD fijo ─────────────
    /// <summary>Un VHD fijo es la imagen en bruto seguida de este pie de 512 bytes.</summary>
    public static byte[] VhdFooter(long size, Guid id, DateTime? when = null)
    {
        var f = new byte[512];
        "conectix"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(16), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), (uint)((when ?? DateTime.UtcNow) - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        "dss "u8.CopyTo(f.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(32), 0x00020000);
        "Wi2k"u8.CopyTo(f.AsSpan(36));
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(40), (ulong)size);
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(48), (ulong)size);
        var (c, h, s) = Geometry(size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(56), c);
        f[58] = h; f[59] = s;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), 2);   // disco fijo
        id.ToByteArray().CopyTo(f, 68);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), Checksum(f));
        return f;
    }

    public static uint Checksum(byte[] footer)
    {
        uint sum = 0;
        for (int i = 0; i < 512; i++) if (i < 64 || i > 67) sum += footer[i];
        return ~sum;
    }

    public static bool IsFixedVhd(string path, out long dataSize)
    {
        dataSize = 0;
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 1024) return false;
            var f = new byte[512];
            fs.Position = fs.Length - 512;
            fs.ReadExactly(f);
            if (!f.AsSpan(0, 8).SequenceEqual("conectix"u8) || BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(60)) != 2) return false;
            if (BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(64)) != Checksum(f)) return false;
            dataSize = fs.Length - 512;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Geometría CHS según la especificación de VHD.</summary>
    public static (ushort cyl, byte heads, byte spt) Geometry(long size)
    {
        long total = Math.Min(size / 512, 65535L * 16 * 255);
        long spt, heads, cth;
        if (total >= 65535L * 16 * 63) { spt = 255; heads = 16; cth = total / spt; }
        else
        {
            spt = 17; cth = total / spt;
            heads = Math.Max(4, (cth + 1023) / 1024);
            if (cth >= heads * 1024 || heads > 16) { spt = 31; heads = 16; cth = total / spt; }
            if (cth >= heads * 1024) { spt = 63; heads = 16; cth = total / spt; }
        }
        return ((ushort)(cth / heads), (byte)heads, (byte)spt);
    }

    // ───────────── Lectura de cualquier formato ─────────────
    /// <summary>Abre una imagen como flujo de datos en bruto. Para VHD/VHDX la monta en solo lectura.</summary>
    public static Stream OpenSource(string path, out long length, out Action cleanup)
    {
        cleanup = () => { };
        switch (Ext(path))
        {
            case ".img" or ".bin" or ".iso" or ".raw":
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk);
                length = fs.Length;
                return fs;
            case ".img.gz":
                length = GzLength(path);
                return new GZipStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk), CompressionMode.Decompress);
            case ".vhd" when IsFixedVhd(path, out long size):
                length = size;
                return new SubStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk), size);
            case ".vhd" or ".vhdx":
                var m = Mount(path, true, false);
                if (m.DiskNumber < 0) throw new IOException("Windows no asignó un disco a la imagen.");
                var h = Native.OpenRaw(@"\\.\PhysicalDrive" + m.DiskNumber, false);
                length = Native.GetDeviceLength(h);
                cleanup = () => { try { Dismount(path); } catch (Exception ex) { Log.Warn(ex.Message); } };
                return new FileStream(h, FileAccess.Read, 0, false);
            default: throw new NotSupportedException("Formato de imagen no admitido: " + Ext(path));
        }
    }

    /// <summary>Tamaño descomprimido: del archivo .sha256 que guarda la app o descomprimiendo una vez.</summary>
    public static long GzLength(string path)
    {
        try
        {
            if (File.Exists(path + ".sha256") && Regex.Match(File.ReadAllText(path + ".sha256"), @"(\d+) bytes") is { Success: true } m) return long.Parse(m.Groups[1].Value);
        }
        catch { }
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        var buf = new byte[Chunk];
        long n = 0; int r;
        while ((r = gz.Read(buf)) > 0) n += r;
        return n;
    }

    // ───────────── Convertir ─────────────
    public static TransferJob ConvertJob(string src, string dst) => new()
    {
        Title = $"Convertir {System.IO.Path.GetFileName(src)} a {Ext(dst)}",
        Subtitle = $"{src} → {dst}",
        Glyph = Icons.Image,
        Work = ctx => Task.Run(() => Convert(ctx, src, dst))
    };

    static void Convert(JobContext ctx, string src, string dst)
    {
        if (string.Equals(System.IO.Path.GetFullPath(src), System.IO.Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) throw new IOException("El origen y el destino son el mismo archivo.");
        ctx.SetCurrent("Abriendo la imagen de origen…");
        using var input = DiskImage.OpenImage(src, out long length, out var cleanup);
        try
        {
            ctx.SetTotal(length);
            string ext = Ext(dst);
            if (ext == ".vhdx") { WriteVhdx(ctx, input, length, dst); return; }
            using var fout = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, Chunk);
            Stream outS = ext == ".img.gz" ? new GZipStream(fout, CompressionLevel.Fastest, true) : fout;
            Copy(ctx, input, length, (buf, n, _) => outS.Write(buf, 0, n));
            if (ext == ".img.gz") { outS.Dispose(); File.WriteAllText(dst + ".sha256", $"(datos sin comprimir, {length} bytes)\n"); }
            if (ext == ".vhd")
            {
                long padded = (length + 511) / 512 * 512;
                if (padded > length) fout.Write(new byte[padded - length]);
                fout.Write(VhdFooter(padded, Guid.NewGuid()));
            }
            Log.Ok($"Imagen convertida: {dst} ({Fmt.Size(length)})");
        }
        finally { cleanup(); }
    }

    static void Copy(JobContext ctx, Stream input, long length, Action<byte[], int, long> write)
    {
        var buf = new byte[Chunk];
        long off = 0;
        while (off < length)
        {
            ctx.Check();
            int want = (int)Math.Min(Chunk, length - off);
            int n = 0, r;
            while (n < want && (r = input.Read(buf, n, want - n)) > 0) n += r;
            if (n == 0) break;
            write(buf, n, off);
            off += n;
            ctx.AddBytes(n);
            ctx.SetCurrent($"Convirtiendo: {Fmt.Size(off)} de {Fmt.Size(length)}");
        }
    }

    /// <summary>Crea un VHDX dinámico con diskpart y copia solo los bloques con datos.</summary>
    static void WriteVhdx(JobContext ctx, Stream input, long length, string dst)
    {
        if (File.Exists(dst)) File.Delete(dst);
        long mb = (length + (1 << 20) - 1) >> 20;
        string script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dss-vhdx-{Guid.NewGuid():N}.txt");
        File.WriteAllText(script, $"create vdisk file=\"{dst}\" maximum={Math.Max(3, mb)} type=expandable\n");
        ctx.SetCurrent("Creando el disco virtual…");
        var (code, o) = PartitionTable.RunTool("diskpart.exe", $"/s \"{script}\"");
        File.Delete(script);
        if (code != 0 || !File.Exists(dst)) throw new IOException("diskpart no pudo crear el VHDX: " + LastLine(o));
        var m = Mount(dst, false, false);
        try
        {
            if (m.DiskNumber < 0) throw new IOException("Windows no asignó un disco al VHDX.");
            DiskImageOffline(m.DiskNumber, false);
            using var h = Native.OpenRaw(@"\\.\PhysicalDrive" + m.DiskNumber, true);
            using var outS = new FileStream(h, FileAccess.ReadWrite, 0, false);
            long skipped = 0;
            Copy(ctx, input, length, (buf, n, off) =>
            {
                if (buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) < 0) { skipped += n; return; }   // bloque vacío: no ocupa espacio
                int aligned = (n + 511) / 512 * 512;
                if (aligned > n) Array.Clear(buf, n, aligned - n);
                outS.Position = off;
                outS.Write(buf, 0, aligned);
            });
            outS.Flush();
            Log.Ok($"VHDX creado: {dst} ({Fmt.Size(length)}, {Fmt.Size(skipped)} vacíos sin ocupar espacio)");
        }
        finally { try { Dismount(dst); } catch (Exception ex) { Log.Warn(ex.Message); } }
    }

    static void DiskImageOffline(int number, bool online)
    {
        try
        {
            var scope = new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var s = new System.Management.ManagementObjectSearcher(scope, new System.Management.ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number={number}"));
            foreach (System.Management.ManagementObject d in s.Get()) d.InvokeMethod(online ? "Online" : "Offline", null);
        }
        catch { }
    }

    /// <summary>Prepara un .img para montarlo: crea una copia .vhd (imagen + pie) y la monta.</summary>
    public static TransferJob MountImgJob(string img, string vhd, bool readOnly, Action<MountedImage> done) => new()
    {
        Title = $"Montar {System.IO.Path.GetFileName(img)}",
        Subtitle = $"Se crea {System.IO.Path.GetFileName(vhd)} para que Windows pueda abrirla",
        Glyph = Icons.Image,
        Work = ctx => Task.Run(() =>
        {
            if (!File.Exists(vhd) || !IsFixedVhd(vhd, out _)) Convert(ctx, img, vhd);
            ctx.SetCurrent("Montando…");
            var m = Mount(vhd, readOnly);
            Log.Ok($"Imagen montada: {img}" + (m.Letters.Length > 0 ? $" en {m.Letters}" : ""));
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => done?.Invoke(m));
        })
    };
}

/// <summary>Vista de solo lectura de los primeros <c>length</c> bytes de otro flujo.</summary>
public class SubStream(Stream inner, long length) : Stream
{
    long pos;
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => pos; set { pos = value; inner.Position = value; } }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, (int)Math.Min(count, length - pos));
        pos += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => pos + offset, _ => length + offset };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
