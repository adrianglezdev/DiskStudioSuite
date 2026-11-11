using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskStudio.Core;

public class FormatOptions
{
    public string FileSystem = "exFAT";     // NTFS | exFAT | FAT32
    public string Label = "";
    public int ClusterSize;                 // 0 = predeterminado
    public bool Quick = true;
    public bool Compress;                   // solo NTFS
    public TableStyle Table = TableStyle.Mbr; // solo al reiniciar el disco completo
}

public static class Formatter
{
    public const long FatLimitWindows = 32L * 1024 * 1024 * 1024;

    // ───────────── Validación ─────────────
    public static string CleanLabel(string label, string fs)
    {
        label = (label ?? "").Trim();
        if (fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || fs == "exFAT")
        {
            label = new string(label.Where(c => !"*?/\\|,;:+=<>[]\"".Contains(c)).ToArray());
            int max = fs == "exFAT" ? 15 : 11;
            if (fs == "FAT32") label = label.ToUpperInvariant();
            if (label.Length > max) label = label[..max];
        }
        else if (label.Length > 32) label = label[..32];
        return label;
    }

    public static List<string> Problems(long size, FormatOptions o, int sector = 512)
    {
        var p = new List<string>();
        if (o.FileSystem == "FAT32")
        {
            if (size > 0xFFFFFFFFL * sector) p.Add($"FAT32 no admite volúmenes de más de {Fmt.Size(0xFFFFFFFFL * sector)}.");
            int spc = (o.ClusterSize > 0 ? o.ClusterSize : Fat32.DefaultCluster(size)) / sector;
            if (spc > 0 && (size / sector) / spc < 65525) p.Add("El volumen es demasiado pequeño para FAT32 con ese tamaño de clúster: elige un clúster más pequeño o FAT/exFAT.");
            if (o.ClusterSize > 32768) p.Add("FAT32 admite clústeres de hasta 32 KB.");
        }
        if (o.Compress && o.FileSystem != "NTFS") p.Add("La compresión solo existe en NTFS.");
        if (o.Compress && o.ClusterSize > 4096) p.Add("La compresión NTFS necesita clústeres de 4 KB o menos.");
        return p;
    }

    // ───────────── Formatear un volumen ─────────────
    public static TransferJob FormatVolumeJob(VolumeInfo v, FormatOptions o) => new()
    {
        Title = $"Formatear {v.Letter} como {o.FileSystem}",
        Subtitle = $"{v.Name} · {Fmt.Size(v.Total)}" + (o.Quick ? " · rápido" : " · completo"),
        Glyph = Icons.Drive,
        Work = ctx => Task.Run(() =>
        {
            if (v.IsSystem) throw new InvalidOperationException("No se puede formatear la unidad del sistema.");
            if (string.Equals(Path.GetPathRoot(AppContext.BaseDirectory), v.Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Disk Studio Suite se está ejecutando desde esta unidad. Cópialo a otro disco para formatearla.");
            FormatLetter(ctx, v.Letter, v.Total, o);
            Log.Ok($"{v.Letter} formateado como {o.FileSystem}" + (string.IsNullOrEmpty(o.Label) ? "" : $" («{o.Label}»)"));
        })
    };

    static void FormatLetter(JobContext ctx, string letter, long size, FormatOptions o)
    {
        string label = CleanLabel(o.Label, o.FileSystem);
        if (o.FileSystem == "FAT32" && size > FatLimitWindows)
        {
            ctx.SetCurrent("Windows no permite FAT32 de más de 32 GB: usando el formateador FAT32 de Disk Studio Suite…");
            Fat32.FormatVolume(@"\\.\" + letter.TrimEnd('\\'), label, o.ClusterSize, ctx);
            return;
        }
        if (o.Quick) { ctx.SetTotal(0); ctx.SetCurrent("Formateando…"); WmiFormat(letter, o.FileSystem, label, o.ClusterSize, false, o.Compress); ctx.SetTotal(1); ctx.AddBytes(1); }
        else FormatCom(ctx, letter, o.FileSystem, label, o.ClusterSize, o.Compress);
    }

    static void WmiFormat(string letter, string fs, string label, int cluster, bool full, bool compress)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Volume WHERE DriveLetter='{letter.TrimEnd(':', '\\')}'"));
        foreach (ManagementObject v in s.Get())
        {
            var p = v.GetMethodParameters("Format");
            p["FileSystem"] = fs;
            p["FileSystemLabel"] = label;
            if (cluster > 0) p["AllocationUnitSize"] = (uint)cluster;
            p["Full"] = full;
            p["Force"] = true;
            if (fs == "NTFS") p["Compress"] = compress;
            var r = v.InvokeMethod("Format", p, null);
            uint code = Convert.ToUInt32(r?["ReturnValue"] ?? 0u);
            if (code != 0) throw new IOException(StorageError(code, "formatear"));
            return;
        }
        throw new IOException("No se encontró el volumen " + letter);
    }

    /// <summary>Formato completo con format.com para poder mostrar el porcentaje.</summary>
    static void FormatCom(JobContext ctx, string letter, string fs, string label, int cluster, bool compress)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        string args = $"{letter.TrimEnd('\\')} /FS:{fs} /V:\"{label}\" /X /Y" + (cluster > 0 ? $" /A:{cluster}" : "") + (compress ? " /C" : "");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "format.com"), args)
        { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = enc };
        using var p = Process.Start(psi)!;
        string current = "";
        try { current = new DriveInfo(letter).VolumeLabel; } catch { }
        p.StandardInput.WriteLine(current);   // por si pide la etiqueta actual
        p.StandardInput.Close();
        ctx.SetTotal(100);
        var sb = new StringBuilder();
        var line = new StringBuilder();
        long reported = 0;
        int ch;
        while ((ch = p.StandardOutput.Read()) >= 0)
        {
            if (ctx.Token.IsCancellationRequested) { try { p.Kill(); } catch { } ctx.Check(); }
            sb.Append((char)ch);
            if (ch is '\r' or '\n')
            {
                var m = Regex.Match(line.ToString(), @"(\d{1,3})\s*(%|por ciento|percent)", RegexOptions.IgnoreCase);
                if (m.Success && long.TryParse(m.Groups[1].Value, out long pct) && pct > reported) { ctx.AddBytes(pct - reported); reported = pct; ctx.SetCurrent($"Formato completo: {pct}%"); }
                line.Clear();
            }
            else line.Append((char)ch);
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            string msg = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Length > 3) ?? "";
            throw new IOException($"format.com terminó con el código {p.ExitCode}. {msg}");
        }
        if (reported < 100) ctx.AddBytes(100 - reported);
    }

    // ───────────── Reiniciar el disco completo ─────────────
    public static TransferJob ResetDiskJob(PhysicalDisk d, FormatOptions o) => new()
    {
        Title = $"Reiniciar disco {d.Number} ({o.Table.ToString().ToUpperInvariant()} + {o.FileSystem})",
        Subtitle = $"{d.Title} · {Fmt.Size(d.Size)} · una sola partición",
        Glyph = Icons.Drive,
        Work = ctx => Task.Run(() =>
        {
            var (letter, size) = Repartition(ctx, d, o, false);
            ctx.SetCurrent($"Formateando {letter} como {o.FileSystem}…");
            FormatLetter(ctx, letter, size, o);
            Log.Ok($"Disco {d.Number} reiniciado: {o.Table} con una partición {o.FileSystem} de {Fmt.Size(size)} en {letter}");
        })
    };

    /// <summary>Borra el disco y crea una única partición con letra (sin formatear). Devuelve la letra y el tamaño.</summary>
    internal static (string letter, long size) Repartition(JobContext ctx, PhysicalDisk d, FormatOptions o, bool active)
    {
        if (d.IsSystem || d.IsBoot) throw new InvalidOperationException("No se puede reiniciar el disco del sistema.");
        var probs = Problems(d.Size, o, d.SectorSize);
        if (probs.Count > 0) throw new InvalidOperationException(string.Join(" ", probs));
        var disk = FindDisk(d.Number) ?? throw new IOException("No se encontró el disco " + d.Number);

        if (disk["IsReadOnly"] is true)
        {
            ctx.SetCurrent("Quitando el atributo de solo lectura…");
            Invoke(disk, "SetAttributes", new() { ["IsReadOnly"] = false }, "quitar la protección contra escritura");
            disk = FindDisk(d.Number);
        }
        if (disk["IsOffline"] is true)
        {
            ctx.SetCurrent("Poniendo el disco en línea…");
            Invoke(disk, "Online", [], "poner el disco en línea");
            disk = FindDisk(d.Number);
        }
        if (Convert.ToInt32(disk["PartitionStyle"] ?? 0) != 0)
        {
            ctx.SetCurrent("Borrando todas las particiones…");
            Invoke(disk, "Clear", new() { ["RemoveData"] = true, ["RemoveOEM"] = true, ["ZeroOutEntireDisk"] = false }, "borrar el disco");
            disk = FindDisk(d.Number);
        }
        ctx.SetCurrent($"Creando tabla {o.Table}…");
        Invoke(disk, "Initialize", new() { ["PartitionStyle"] = (ushort)(o.Table == TableStyle.Gpt ? 2 : 1) }, "inicializar el disco");
        disk = FindDisk(d.Number);

        ctx.SetCurrent("Creando la partición…");
        var args = new Dictionary<string, object> { ["UseMaximumSize"] = true, ["AssignDriveLetter"] = true };
        if (o.Table == TableStyle.Mbr) { args["MbrType"] = (ushort)(o.FileSystem == "FAT32" ? 12 : 7); if (active) args["IsActive"] = true; }
        Invoke(disk, "CreatePartition", args, "crear la partición");
        PartitionTable.RefreshStorageCache();

        string letter = null; long size = 0;
        for (int i = 0; i < 40 && letter == null; i++)
        {
            Thread.Sleep(250);
            var part = FindDataPartition(d.Number);
            if (part == null) continue;
            size = Convert.ToInt64(part["Size"]);
            char c = Convert.ToChar(part["DriveLetter"] ?? '\0');
            if (c != '\0') letter = c + ":";
            else if (i == 20) letter = PartitionTable.AssignLetter(d.Number, Convert.ToInt64(part["Offset"]));
        }
        if (letter == null) throw new IOException("La partición se creó pero Windows no le asignó letra.");
        for (int i = 0; i < 20 && !Directory.Exists(letter + "\\") && !DriveInfo.GetDrives().Any(x => x.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase)); i++) Thread.Sleep(250);
        return (letter, size);
    }

    internal static void FormatPartition(JobContext ctx, string letter, long size, FormatOptions o) => FormatLetter(ctx, letter, size, o);

    static ManagementObject FindDisk(int n)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number={n}"));
        return s.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    static ManagementObject FindDataPartition(int disk)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk}"));
        return s.Get().Cast<ManagementObject>().OrderByDescending(p => Convert.ToInt64(p["Size"])).FirstOrDefault();
    }

    static void Invoke(ManagementObject o, string method, Dictionary<string, object> args, string what)
    {
        var p = o.GetMethodParameters(method);
        foreach (var (k, v) in args) p[k] = v;
        var r = o.InvokeMethod(method, p, null);
        uint code = Convert.ToUInt32(r?["ReturnValue"] ?? 0u);
        if (code != 0) throw new IOException(StorageError(code, what));
    }

    static string StorageError(uint code, string what) => code switch
    {
        1 => $"No se pudo {what}: operación no admitida por este dispositivo.",
        40001 => $"No se pudo {what}: acceso denegado (¿protección física contra escritura?).",
        40002 => $"No se pudo {what}: el disco está en uso.",
        41006 => $"No se pudo {what}: el disco está en uso por otro proceso.",
        42002 => $"No se pudo {what}: el sistema de archivos no admite este tamaño o clúster.",
        43001 => $"No se pudo {what}: el disco es de solo lectura (protección física contra escritura).",
        _ => $"No se pudo {what} (código {code})."
    };
}

/// <summary>Formateador FAT32 propio (Windows no permite FAT32 de más de 32 GB).</summary>
public static class Fat32
{
    public static int DefaultCluster(long size) => size switch
    {
        < 260L << 20 => 512,
        < 8L << 30 => 4096,
        < 16L << 30 => 8192,
        < 32L << 30 => 16384,
        _ => 32768
    };

    public record Layout(int BytesPerSector, int SectorsPerCluster, int Reserved, uint FatSectors, uint TotalSectors, uint Clusters, long DataStart);

    public static Layout Compute(long size, int bps, int clusterBytes, long hiddenSectors = 0)
    {
        int spc = Math.Max(1, (clusterBytes > 0 ? clusterBytes : DefaultCluster(size)) / bps);
        long total = size / bps;
        if (total > uint.MaxValue) throw new InvalidOperationException("Volumen demasiado grande para FAT32.");
        int reserved = 32;
        uint fat = 0;
        for (int i = 0; i < 10; i++)
        {
            long clusters = (total - reserved - 2L * fat) / spc;
            uint need = (uint)(((clusters + 2) * 4 + bps - 1) / bps);
            if (need == fat) break;
            fat = need;
        }
        // Alinear el inicio de los datos a 1 MiB (mejor rendimiento en memorias flash)
        long dataStart = reserved + 2L * fat;
        long align = Math.Max(1, (1L << 20) / bps);
        long abs = hiddenSectors + dataStart;
        long pad = (align - abs % align) % align;
        reserved += (int)pad;
        uint finalClusters = (uint)((total - reserved - 2L * fat) / spc);
        if (finalClusters < 65525) throw new InvalidOperationException("Demasiados pocos clústeres para FAT32: usa un clúster más pequeño.");
        if (finalClusters > 0x0FFFFFF5) throw new InvalidOperationException("Demasiados clústeres para FAT32: usa un clúster más grande.");
        return new Layout(bps, spc, reserved, fat, (uint)total, finalClusters, (long)(reserved + 2L * fat) * bps);
    }

    /// <summary>Escribe un sistema de archivos FAT32 vacío. <paramref name="s"/> es el volumen (o una imagen) desde el byte 0.</summary>
    public static Layout Write(Stream s, long size, int bps, int clusterBytes, string label, long hiddenSectors, JobContext ctx = null)
    {
        var L = Compute(size, bps, clusterBytes, hiddenSectors);
        label = string.IsNullOrWhiteSpace(label) ? "NO NAME" : label;
        var lab = Encoding.ASCII.GetBytes(label.ToUpperInvariant().PadRight(11)[..11]);
        uint volId = (uint)Random.Shared.Next() ^ (uint)DateTime.Now.Ticks;

        var boot = new byte[bps];
        boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(boot, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11), (ushort)bps);
        boot[13] = (byte)L.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14), (ushort)L.Reserved);
        boot[16] = 2;                         // número de FAT
        boot[21] = 0xF8;                      // medio: disco fijo
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(24), 63);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(26), 255);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(28), (uint)hiddenSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), L.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36), L.FatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44), 2);   // clúster raíz
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(48), 1);   // FSInfo
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(50), 6);   // copia del arranque
        boot[64] = 0x80; boot[66] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(67), volId);
        lab.CopyTo(boot, 71);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82);
        boot[90] = 0xCD; boot[91] = 0x18;     // no arrancable: INT 18h
        boot[510] = 0x55; boot[511] = 0xAA;

        var info = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0), 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(484), 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488), L.Clusters - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(508), 0xAA550000);
        var sig = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(sig.AsSpan(508), 0xAA550000);

        long fatBytes = (long)L.FatSectors * bps;
        long clusterSize = (long)L.SectorsPerCluster * bps;
        long totalWrite = (long)L.Reserved * bps + 2 * fatBytes + clusterSize;
        ctx?.SetTotal(totalWrite);

        var zero = new byte[1 << 20];
        void zeroRange(long off, long len)
        {
            s.Position = off;
            while (len > 0) { ctx?.Check(); int n = (int)Math.Min(zero.Length, len); s.Write(zero, 0, n); len -= n; ctx?.AddBytes(n); }
        }
        // Sectores reservados, FAT y carpeta raíz a cero
        zeroRange(0, (long)L.Reserved * bps);
        zeroRange((long)L.Reserved * bps, 2 * fatBytes);
        zeroRange(L.DataStart, clusterSize);

        void at(long sector, byte[] data) { s.Position = sector * bps; s.Write(data, 0, data.Length); }
        at(0, boot); at(1, info); at(2, sig);
        at(6, boot); at(7, info); at(8, sig);

        var fat0 = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(fat0.AsSpan(0), 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat0.AsSpan(4), 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat0.AsSpan(8), 0x0FFFFFFF);   // raíz: fin de cadena
        at(L.Reserved, fat0);
        at(L.Reserved + L.FatSectors, fat0);

        // Etiqueta como primera entrada de la carpeta raíz
        if (label != "NO NAME")
        {
            var entry = new byte[bps];
            lab.CopyTo(entry, 0);
            entry[11] = 0x08;
            s.Position = L.DataStart; s.Write(entry, 0, entry.Length);
        }
        // Borrar posibles restos de otro sistema de archivos al final del volumen (copia del arranque NTFS)
        if (size >= bps) { s.Position = (size / bps - 1) * bps; s.Write(new byte[bps], 0, bps); }
        s.Flush();
        return L;
    }

    public static void FormatVolume(string volumeDevice, string label, int clusterBytes, JobContext ctx)
    {
        // Datos del volumen antes de desmontarlo
        int bps = 512;
        try { if (Native.GetDiskFreeSpace(volumeDevice.Replace(@"\\.\", "") + "\\", out _, out uint b, out _, out _)) bps = (int)b; } catch { }
        long hidden = 0;
        try { hidden = PartitionStart(volumeDevice) / bps; } catch { }

        using var h = Native.OpenRaw(volumeDevice, true);
        if (!Native.Ioctl(h, Native.FSCTL_LOCK_VOLUME)) throw new IOException("El volumen está en uso: cierra los programas o ventanas que lo estén usando.");
        Native.Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME);
        long size = Native.GetDeviceLength(h);
        using (var fs = new FileStream(h, FileAccess.ReadWrite, 0, false))
            Write(new SectorStream(fs, bps), size, bps, clusterBytes, label, hidden, ctx);
        Native.Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME);
        Native.Ioctl(h, Native.FSCTL_UNLOCK_VOLUME);
        // Marcar la partición como FAT32 (0x0C) si es MBR
        try
        {
            string letter = volumeDevice.Replace(@"\\.\", "").TrimEnd(':');
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DriveLetter='{letter}'"));
            foreach (ManagementObject p in s.Get())
                if (p["MbrType"] != null && Convert.ToInt32(p["MbrType"]) != 12)
                {
                    var a = p.GetMethodParameters("SetAttributes"); a["MbrType"] = (ushort)12; p.InvokeMethod("SetAttributes", a, null);
                }
        }
        catch { }
    }

    static long PartitionStart(string volumeDevice)
    {
        string letter = volumeDevice.Replace(@"\\.\", "").TrimEnd(':');
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT Offset FROM MSFT_Partition WHERE DriveLetter='{letter}'"));
        foreach (ManagementObject p in s.Get()) return Convert.ToInt64(p["Offset"]);
        return 0;
    }
}

/// <summary>Adapta escrituras de cualquier tamaño a sectores completos (necesario en volúmenes en bruto).</summary>
public class SectorStream(Stream inner, int sector) : Stream
{
    long pos;
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => pos; set => pos = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin origin) => pos = origin == SeekOrigin.Begin ? o : origin == SeekOrigin.Current ? pos + o : Length + o;
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c)
    {
        if (pos % sector == 0 && c % sector == 0) { inner.Position = pos; inner.Write(b, o, c); pos += c; return; }
        long start = pos / sector * sector, end = (pos + c + sector - 1) / sector * sector;
        var buf = new byte[end - start];
        inner.Position = start; int got = 0; while (got < buf.Length) { int r = inner.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
        Array.Copy(b, o, buf, pos - start, c);
        inner.Position = start; inner.Write(buf, 0, buf.Length);
        pos += c;
    }
}
