using System.ComponentModel;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskStudio.Core;

public class SmartCloneOptions
{
    public TableStyle? Style;          // null = igual que el origen
    public bool OptimizeSsd;           // alinear 1 MiB + copiar solo bloques usados + TRIM
    public bool UseSnapshot = true;    // leer NTFS desde una instantánea VSS
}

/// <summary>Clonado por particiones: recoloca, convierte MBR/GPT, copia solo lo usado y reconstruye el arranque.</summary>
public static class SmartClone
{
    const int Chunk = 4 * 1024 * 1024;
    const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref long inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);

    record PartInfo(string VolumePath, string Fs, string Letter, bool Windows, bool BootStore, uint Cluster, long Used, string Label);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForUser, out ulong total, out ulong totalFree);

    /// <summary>Vista previa sin administrador (a partir de los datos de Windows).</summary>
    public static ClonePlan Preview(PhysicalDisk src, PhysicalDisk dst, SmartCloneOptions o)
    {
        var l = PartitionTable.FromWmi(src);
        var parts = src.Partitions.OrderBy(p => p.Offset).ToList();
        bool win(int i) => !string.IsNullOrEmpty(parts[i].Letter) && HasWindows(parts[i].Letter + "\\");
        long used(int i) => !string.IsNullOrEmpty(parts[i].Letter) && parts[i].Size > 0 ? parts[i].Size - parts[i].Free : -1;
        string name(int i) => !string.IsNullOrEmpty(parts[i].Letter) ? $"{parts[i].Letter} «{parts[i].Label}»".Replace(" «»", "") : null;
        return PartitionTable.PlanClone(l, dst.Size, dst.SectorSize, o.Style, o.OptimizeSsd, i => parts[i].FileSystem, win, i => parts[i].IsActive && !win(i), used, name);
    }

    static bool HasWindows(string root) =>
        File.Exists(Path.Combine(root, @"Windows\System32\winload.efi")) || File.Exists(Path.Combine(root, @"Windows\System32\winload.exe"));
    static bool HasBootStore(string root) =>
        File.Exists(Path.Combine(root, "bootmgr")) || File.Exists(Path.Combine(root, @"Boot\BCD")) || Directory.Exists(Path.Combine(root, @"EFI\Microsoft\Boot"));

    public static TransferJob Job(PhysicalDisk src, PhysicalDisk dst, SmartCloneOptions o) => new()
    {
        Title = $"Clonar disco {src.Number} → disco {dst.Number}" + (o.Style is TableStyle s ? $" ({s.ToString().ToUpperInvariant()})" : "") + (o.OptimizeSsd ? " · SSD" : ""),
        Subtitle = $"{src.Title} → {dst.Title}",
        Glyph = Icons.Clone,
        Work = ctx => Task.Run(() => Run(ctx, src, dst, o))
    };

    static void Run(JobContext ctx, PhysicalDisk src, PhysicalDisk dst, SmartCloneOptions o)
    {
        if (dst.IsSystem || dst.IsBoot) throw new InvalidOperationException("No se puede escribir en el disco del sistema.");
        if (src.Number == dst.Number) throw new InvalidOperationException("Origen y destino son el mismo disco.");
        ctx.SetCurrent("Analizando el disco de origen…");
        using var sh = Native.OpenRaw(src.Device, false);
        var srcLayout = PartitionTable.Read(sh);
        var info = srcLayout.Parts.Select(p =>
        {
            string vp = PartitionTable.VolumePath(src.Number, p.Offset);
            string letter = PartitionTable.DriveLetter(src.Number, p.Offset);
            string root = letter != null ? letter + "\\" : vp;
            string fs = src.Partitions.FirstOrDefault(x => x.Offset == p.Offset)?.FileSystem ?? "";
            if (fs == "" && letter != null) try { fs = new DriveInfo(letter).DriveFormat; } catch { }
            uint cluster = 0;
            if (root != null && Native.GetDiskFreeSpace(root, out uint spc, out uint bps, out _, out _)) cluster = spc * bps;
            if (string.IsNullOrEmpty(fs) && vp != null) fs = VolumeFs(vp);
            bool win = root != null && SafeCheck(() => HasWindows(root));
            bool boot = root != null && SafeCheck(() => HasBootStore(root));
            long used = -1;
            if (root != null && GetDiskFreeSpaceEx(root, out _, out ulong tot, out ulong free)) used = (long)(tot - free);
            string label = src.Partitions.FirstOrDefault(x => x.Offset == p.Offset)?.Label ?? "";
            return new PartInfo(vp, fs, letter, win, boot, cluster, used, label);
        }).ToList();

        using var dh = Native.OpenRaw(dst.Device, true);
        var dstGeo = PartitionTable.Read(dh);
        var plan = PartitionTable.PlanClone(srcLayout, dstGeo.DiskSize, dstGeo.SectorSize, o.Style, o.OptimizeSsd,
            i => info[i].Fs, i => info[i].Windows, i => info[i].BootStore, i => info[i].Used, i => info[i].Letter);
        foreach (var n in plan.Notes) Log.Info("Clonado: " + n);
        if (!plan.Ok) throw new InvalidOperationException(string.Join(" ", plan.Errors.Distinct()));

        // Instantáneas VSS y rangos a copiar
        var shadows = new List<string>();
        var copies = new List<(PartEntry target, PartEntry source, string device, List<(long off, long len)> ranges)>();
        var fileCopies = new List<(PartEntry target, PartInfo info, string device)>();
        try
        {
            foreach (var t in plan.Target.Parts.Where(p => p.Source >= 0))
            {
                ctx.Check();
                var s = srcLayout.Parts[t.Source];
                var pi = info[t.Source];
                string device = null;
                List<(long, long)> ranges = null;
                bool ntfs = pi.Fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && pi.VolumePath != null;
                if (t.CopyFiles)
                {
                    // Partición reducida: se copia archivo a archivo después de crear la tabla
                    if (ntfs && o.UseSnapshot)
                    {
                        ctx.SetCurrent($"Creando instantánea de {(pi.Letter ?? "la partición " + (t.Source + 1))}…");
                        try { var (id, dev) = CreateShadow(pi.VolumePath); shadows.Add(id); device = dev; }
                        catch (Exception ex) { Log.Warn($"Sin instantánea para {pi.Letter ?? pi.VolumePath}: {ex.Message}. Se leerá directamente."); }
                    }
                    fileCopies.Add((t, pi, device));
                    continue;
                }
                if (ntfs && o.UseSnapshot)
                {
                    ctx.SetCurrent($"Creando instantánea de {(pi.Letter ?? "la partición " + (t.Source + 1))}…");
                    try { var (id, dev) = CreateShadow(pi.VolumePath); shadows.Add(id); device = dev; }
                    catch (Exception ex) { Log.Warn($"Sin instantánea para {pi.Letter ?? pi.VolumePath}: {ex.Message}. Se leerá directamente."); }
                }
                if (ntfs && o.OptimizeSsd && pi.Cluster > 0)
                {
                    ctx.SetCurrent("Leyendo el mapa de bloques en uso…");
                    try { ranges = UsedRanges(device ?? pi.VolumePath.TrimEnd('\\'), pi.Cluster, s.Length); }
                    catch (Exception ex) { Log.Warn($"No se pudo leer el mapa de bloques ({ex.Message}); se copia la partición completa."); }
                }
                ranges ??= [(0, s.Length)];
                copies.Add((t, s, device, ranges));
            }
            long total = copies.Sum(c => c.ranges.Sum(r => r.len)) + fileCopies.Sum(f => Math.Max(0, f.info.Used));
            long full = copies.Sum(c => c.source.Length);
            ctx.SetTotal(total);
            if (total < full) Log.Info($"Se copiarán {Fmt.Size(total)} en uso de {Fmt.Size(full)} (el espacio vacío no se escribe).");

            // Preparar el destino: tabla vacía (ningún volumen montado mientras se escriben los datos)
            ctx.SetCurrent("Preparando el disco destino…");
            var locks = PartitionTable.LockVolumes(dst);
            try { PartitionTable.CreateEmpty(dh, plan.Target); }
            finally { PartitionTable.Unlock(locks); }

            // Copiar datos
            var buf = new byte[Chunk];
            foreach (var (t, s, device, ranges) in copies)
            {
                SafeFileHandle vh = device != null ? Native.OpenRaw(device, false) : null;
                long vlen = vh != null ? Native.GetDeviceLength(vh) : 0;
                try
                {
                    foreach (var (off, len) in ranges)
                        for (long pos = off; pos < off + len;)
                        {
                            ctx.Check();
                            int n = (int)Math.Min(Chunk, off + len - pos);
                            bool fromSnap = vh != null && pos + n <= vlen;
                            int got = fromSnap ? RandomAccess.Read(vh, buf.AsSpan(0, n), pos) : RandomAccess.Read(sh, buf.AsSpan(0, n), s.Offset + pos);
                            if (got < n) Array.Clear(buf, got, n - got);
                            RandomAccess.Write(dh, buf.AsSpan(0, n), t.Offset + pos);
                            pos += n;
                            ctx.AddBytes(n);
                            ctx.SetCurrent($"Partición {t.Source + 1}: {Fmt.Size(pos)} de {Fmt.Size(s.Length)}" + (fromSnap ? " (instantánea)" : ""));
                        }
                }
                finally { vh?.Dispose(); }
            }

            ctx.SetCurrent("Escribiendo la tabla de particiones…");
            PartitionTable.WriteLayout(dh, plan.Target);

            // Particiones reducidas: formatear y copiar los archivos (desde la instantánea si la hay)
            if (fileCopies.Count > 0)
            {
                PartitionTable.RefreshStorageCache();
                Thread.Sleep(3000);
                foreach (var (t, pi, device) in fileCopies) CopyFilesInto(ctx, dst, t, pi, device);
            }
        }
        finally
        {
            foreach (var id in shadows) DeleteShadow(id);
        }

        Thread.Sleep(3000); // que Windows monte los volúmenes nuevos
        if (plan.RebuildBoot) RebuildBoot(ctx, dst, plan, srcLayout, info);
        if (o.OptimizeSsd) Trim(ctx, dst, plan);
        Log.Ok($"Disco {src.Number} clonado en el disco {dst.Number} ({plan.Target.Style}, {plan.Target.Parts.Count} particiones).");
        foreach (var n in plan.Notes.Where(n => n.Contains("BIOS"))) Toast.Show("Clonado terminado", n, Icons.Info);
    }

    static bool SafeCheck(Func<bool> f) { try { return f(); } catch { return false; } }

    /// <summary>Formatea la partición reducida igual que la de origen y copia sus archivos con Robocopy.</summary>
    static void CopyFilesInto(JobContext ctx, PhysicalDisk dst, PartEntry t, PartInfo pi, string shadowDevice)
    {
        string what = pi.Letter ?? $"la partición {t.Source + 1}";
        ctx.SetCurrent($"Preparando la partición reducida para {what}…");
        string letter = PartitionTable.AssignLetter(dst.Number, t.Offset);
        long done = ctx.BytesDone;
        long total = Interlocked.Read(ref ctx.Job.bytesTotal);
        Formatter.FormatPartition(ctx, letter, t.Length, new FormatOptions { FileSystem = pi.Fs, Label = Formatter.CleanLabel(pi.Label, pi.Fs), ClusterSize = (int)pi.Cluster, Quick = true });
        ctx.ResetProgress(total);
        ctx.AddBytes(done);

        // La instantánea (o un volumen sin letra) se abre con un enlace simbólico temporal a su raíz
        string link = null;
        string device = shadowDevice ?? (pi.Letter == null ? pi.VolumePath : null);
        if (device != null)
        {
            link = Path.Combine(Path.GetTempPath(), $"dss-origen-{Guid.NewGuid():N}");
            var (lc, lo) = PartitionTable.RunTool("cmd.exe", $"/c mklink /d \"{link}\" \"{device.TrimEnd('\\')}\\\"");
            if (lc != 0 || !Directory.Exists(link))
            {
                link = null;
                if (pi.Letter == null) throw new IOException($"No se pudo abrir {what} para copiar sus archivos: {lo}");
                Log.Warn("No se pudo abrir la instantánea: " + lo + ". Se copia directamente.");
            }
        }
        string from = link ?? pi.Letter + "\\";
        string to = letter + "\\";
        // Robocopy lee mal «"X:\"» (la barra final escapa la comilla): se pasa «X:\.»
        static string Arg(string p) => "\"" + p.TrimEnd('\\') + "\\.\"";
        try
        {
            ctx.SetCurrent($"Copiando los archivos de {what}…");
            long startUsed = UsedBytes(to);
            using var stop = new CancellationTokenSource();
            long counted = 0;   // bytes de esta partición ya sumados al progreso
            var poll = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                    long now = Math.Max(0, UsedBytes(to) - startUsed);
                    long target = Math.Min(now, Math.Max(0, pi.Used));
                    long prev = Interlocked.Read(ref counted);
                    if (target > prev) { ctx.AddBytes(target - prev); Interlocked.Exchange(ref counted, target); }
                    ctx.SetCurrent($"Copiando los archivos de {what}: {Fmt.Size(now)} de {Fmt.Size(Math.Max(0, pi.Used))}");
                }
            });
            // /COPYALL + /B: datos, atributos, fechas, permisos, propietario y auditoría · /SL /SJ: enlaces y uniones tal cual.
            // El origen es un enlace a la instantánea: con /SL Robocopy copiaría ese enlace en vez de su contenido, así que
            // la raíz se copia sin /SL (solo sus archivos) y cada carpeta de primer nivel por separado, ya con /SL /SJ.
            const string common = "/COPYALL /DCOPY:DAT /B /R:1 /W:1 /NP /NFL /NDL /NJH";
            int code = 0;
            var outAll = new System.Text.StringBuilder();
            void robo(string args)
            {
                var (c, o) = VolumeRepair.RunLive("robocopy.exe", args, ctx, _ => { });
                code = Math.Max(code, c);
                outAll.AppendLine(o);
            }
            robo($"{Arg(from)} {Arg(to)} /LEV:1 {common} /XF pagefile.sys hiberfil.sys swapfile.sys DumpStack.log.tmp");
            foreach (var dir in new DirectoryInfo(from).EnumerateDirectories("*", new System.IO.EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                ctx.Check();
                if (dir.Name is "System Volume Information" or "$RECYCLE.BIN" or "$Recycle.Bin") continue;
                string target = Path.Combine(to, dir.Name);
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Carpeta de la raíz que es una unión o un enlace (p. ej. «Documents and Settings»): se recrea igual
                    string dest = dir.LinkTarget;
                    if (string.IsNullOrEmpty(dest)) { Log.Warn($"{what}: se omite {dir.Name} (enlace sin destino legible)"); continue; }
                    bool junction = !Path.IsPathRooted(dest) || dest.Length > 1 && dest[1] == ':';   // las uniones apuntan a una ruta local
                    var (lc, lo) = PartitionTable.RunTool("cmd.exe", $"/c mklink {(junction ? "/j" : "/d")} \"{target}\" \"{dest}\"");
                    if (lc != 0) ctx.Fail(dir.FullName, "No se pudo recrear el enlace: " + lo);
                    continue;
                }
                robo($"{Arg(dir.FullName)} {Arg(target)} /E /SL /SJ /MT:8 {common}");
            }
            string outp = outAll.ToString();
            stop.Cancel();
            try { poll.Wait(3000); } catch { }
            // Completar el progreso de esta partición (Robocopy puede terminar antes de la primera medición)
            long rest = Math.Max(0, pi.Used) - Interlocked.Read(ref counted);
            if (rest > 0) ctx.AddBytes(rest);
            // Robocopy: 0-7 correcto (1 = archivos copiados, 2/4 = extras o diferencias); 8 o más = errores
            // Resumen de Robocopy en el registro (tabla final: directorios, archivos, bytes)
            var summary = outp.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && (l.Contains(':') && System.Text.RegularExpressions.Regex.IsMatch(l, @"^\S+\s*:\s+\d") || l.Contains("ERROR"))).Take(8);
            foreach (var l in summary) Log.Info($"Robocopy ({what}): {l}");
            if (code >= 8) ctx.Fail(what, $"Robocopy terminó con el código {code}: algunos archivos no se pudieron copiar. " + string.Join(" ", outp.Split('\n').Where(l => l.Contains("ERROR")).Take(3).Select(l => l.Trim())));
            long copied = Math.Max(0, UsedBytes(to) - startUsed);
            Log.Ok($"{what}: archivos copiados en la partición reducida ({Fmt.Size(copied)}, código de Robocopy {code})");
        }
        finally
        {
            if (link != null) try { Directory.Delete(link); } catch { }
            if (pi.Letter == null) PartitionTable.RemoveLetter(dst.Number, t.Offset, letter);
        }
    }

    static long UsedBytes(string root) => GetDiskFreeSpaceEx(root, out _, out ulong tot, out ulong free) ? (long)(tot - free) : 0;

    static string VolumeFs(string vp)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FileSystem, Path FROM MSFT_Volume"));
            foreach (ManagementObject v in s.Get())
                if (string.Equals(v["Path"]?.ToString(), vp, StringComparison.OrdinalIgnoreCase)) return v["FileSystem"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }

    // ───────────── Arranque ─────────────
    static void RebuildBoot(JobContext ctx, PhysicalDisk dst, ClonePlan plan, DiskLayout src, List<PartInfo> info)
    {
        var win = plan.Target.Parts.FirstOrDefault(p => p.Source >= 0 && info[p.Source].Windows);
        if (win == null) return;
        var sys = plan.Uefi ? plan.Target.Parts.FirstOrDefault(p => p.Role == "EFI") : plan.Target.Parts.FirstOrDefault(p => p.Active) ?? win;
        if (sys == null) { Log.Warn("No hay partición de sistema en el destino: el disco no arrancará hasta crearla."); return; }
        ctx.SetCurrent("Reconstruyendo el arranque de Windows…");
        string w = null, s = null;
        bool wTemp = PartitionTable.DriveLetter(dst.Number, win.Offset) == null;
        bool sTemp = sys != win && PartitionTable.DriveLetter(dst.Number, sys.Offset) == null;
        try
        {
            w = PartitionTable.AssignLetter(dst.Number, win.Offset);
            s = sys == win ? w : PartitionTable.AssignLetter(dst.Number, sys.Offset);
            if (sys.FormatAs != null && sys != win)
            {
                ctx.SetCurrent($"Formateando la partición de sistema ({sys.FormatAs})…");
                var (fc, fo) = PartitionTable.RunTool("format.com", $"{s} /FS:{sys.FormatAs} /V:SYSTEM /Q /Y");
                if (fc != 0) throw new IOException("No se pudo formatear la partición de sistema: " + fo);
            }
            var (code, outp) = PartitionTable.RunTool("bcdboot.exe", $"{w}\\Windows /s {s} /f {(plan.Uefi ? "UEFI" : "BIOS")}");
            if (code != 0) throw new IOException("bcdboot falló: " + outp);
            Log.Ok($"Arranque de Windows creado en el disco {dst.Number} ({(plan.Uefi ? "UEFI" : "BIOS")}).");
        }
        catch (Exception ex) { ctx.Fail("Arranque", ex.Message); }
        finally
        {
            if (sTemp && s != null && s != w) PartitionTable.RemoveLetter(dst.Number, sys.Offset, s);
            if (wTemp && w != null) PartitionTable.RemoveLetter(dst.Number, win.Offset, w);
        }
    }

    static void Trim(JobContext ctx, PhysicalDisk dst, ClonePlan plan)
    {
        foreach (var p in plan.Target.Parts.Where(p => p.Source >= 0 && p.Role == "Datos"))
        {
            string l = PartitionTable.DriveLetter(dst.Number, p.Offset);
            bool temp = false;
            try
            {
                if (l == null) { l = PartitionTable.AssignLetter(dst.Number, p.Offset); temp = true; }
                ctx.SetCurrent($"TRIM en {l}…");
                var (code, outp) = PartitionTable.RunTool("defrag.exe", $"{l} /L");
                if (code == 0) Log.Ok($"TRIM aplicado en {l} (disco {dst.Number}).");
                else Log.Info($"TRIM no aplicado en {l}: {outp.Split('\n').LastOrDefault()?.Trim()}");
            }
            catch (Exception ex) { Log.Warn($"TRIM: {ex.Message}"); }
            finally { if (temp && l != null) PartitionTable.RemoveLetter(dst.Number, p.Offset, l); }
        }
    }

    // ───────────── VSS ─────────────
    static (string id, string device) CreateShadow(string volume)
    {
        using var cls = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
        var inp = cls.GetMethodParameters("Create");
        inp["Volume"] = volume.EndsWith('\\') ? volume : volume + "\\";
        inp["Context"] = "ClientAccessible";
        var outp = cls.InvokeMethod("Create", inp, null);
        uint code = System.Convert.ToUInt32(outp["ReturnValue"]);
        if (code != 0) throw new IOException($"VSS devolvió el código {code}");
        string id = outp["ShadowID"].ToString();
        using var s = new ManagementObjectSearcher($"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{id}'");
        foreach (ManagementObject sc in s.Get()) return (id, sc["DeviceObject"].ToString());
        throw new IOException("No se encontró la instantánea creada");
    }

    static void DeleteShadow(string id)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID='{id}'");
            foreach (ManagementObject sc in s.Get()) sc.Delete();
        }
        catch (Exception ex) { Log.Warn("No se pudo borrar la instantánea VSS: " + ex.Message); }
    }

    // ───────────── Mapa de clústeres en uso (NTFS) ─────────────
    public static List<(long off, long len)> UsedRanges(string volumeDevice, uint cluster, long partLen)
    {
        using var h = Native.OpenRaw(volumeDevice, false);
        var ranges = new List<(long, long)>();
        long lcn = 0;
        var buf = new byte[16 * 1024 * 1024];
        while (true)
        {
            long start = lcn;
            bool ok = DeviceIoControl(h, FSCTL_GET_VOLUME_BITMAP, ref start, 8, buf, buf.Length, out int ret, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();
            if (!ok && err != 234) throw new Win32Exception(err);
            long first = BitConverter.ToInt64(buf, 0), count = BitConverter.ToInt64(buf, 8);
            long bits = Math.Min(count, (ret - 16) * 8L);
            long runStart = -1;
            for (long i = 0; i < bits; i++)
            {
                bool used = (buf[16 + (i >> 3)] & (1 << (int)(i & 7))) != 0;
                if (used && runStart < 0) runStart = i;
                if (!used && runStart >= 0) { ranges.Add(((first + runStart) * cluster, (i - runStart) * cluster)); runStart = -1; }
            }
            if (runStart >= 0) ranges.Add(((first + runStart) * cluster, (bits - runStart) * cluster));
            lcn = first + bits;
            if (ok || bits <= 0) break;
        }
        // Siempre: primer y último MiB (sector de arranque, copia del sector de arranque NTFS)
        ranges.Add((0, Math.Min(PartitionTable.MiB, partLen)));
        ranges.Add((Math.Max(0, partLen - PartitionTable.MiB), Math.Min(PartitionTable.MiB, partLen)));
        // Unir rangos contiguos o cercanos (≤ 1 MiB) para escribir en bloques grandes
        var merged = new List<(long off, long len)>();
        foreach (var (off, len) in ranges.Where(r => r.Item2 > 0).Select(r => (off: r.Item1, len: Math.Min(r.Item2, partLen - r.Item1))).OrderBy(r => r.off))
        {
            if (merged.Count > 0 && off <= merged[^1].off + merged[^1].len + PartitionTable.MiB)
            {
                var last = merged[^1];
                merged[^1] = (last.off, Math.Max(last.off + last.len, off + len) - last.off);
            }
            else merged.Add((off, len));
        }
        return merged;
    }
}
