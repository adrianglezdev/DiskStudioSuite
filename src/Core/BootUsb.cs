using System.IO;

namespace DiskStudio.Core;

public enum BootMode { Uefi, UefiBios, Dd }

public class IsoInfo
{
    public string Path = "";
    public long Size;
    public string Label = "";
    public bool Windows, Efi, Bios, Hybrid;
    public long LargestFile;
    public string LargestName = "";
    public string Kind => Windows ? "Instalación de Windows" : Hybrid ? "Linux u otro sistema (ISO híbrida)" : Efi ? "Sistema con arranque UEFI" : "Disco de datos o sistema sin arranque UEFI";
    public bool NeedsSplit => Windows && LargestFile >= BootUsb.Fat32Max && LargestName.EndsWith(".wim", StringComparison.OrdinalIgnoreCase);
    public BootMode Recommended => !Efi && Hybrid ? BootMode.Dd : Windows && Bios ? BootMode.UefiBios : !Windows && Hybrid ? BootMode.Dd : BootMode.Uefi;
}

/// <summary>Crear pendrives de arranque a partir de una ISO (copia de archivos en FAT32 o imagen DD).</summary>
public static class BootUsb
{
    public const long Fat32Max = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>Una ISO híbrida lleva una tabla de particiones MBR: se puede escribir tal cual en un USB.</summary>
    public static bool IsHybrid(string iso)
    {
        try
        {
            using var fs = File.OpenRead(iso);
            var b = new byte[512];
            if (fs.Read(b, 0, 512) < 512 || b[510] != 0x55 || b[511] != 0xAA) return false;
            for (int i = 0; i < 4; i++) if (b[446 + i * 16 + 4] != 0) return true;   // alguna partición con tipo
            return false;
        }
        catch { return false; }
    }

    public static IsoInfo Inspect(string iso)
    {
        var info = new IsoInfo { Path = iso, Size = new FileInfo(iso).Length, Hybrid = IsHybrid(iso) };
        var m = VirtualDisk.Mount(iso, true);
        try
        {
            string root = m.Letters.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is string l ? l + "\\" : throw new IOException("Windows no asignó letra a la ISO.");
            try { info.Label = new DriveInfo(root).VolumeLabel; } catch { }
            info.Windows = File.Exists(Path.Combine(root, "sources", "install.wim")) || File.Exists(Path.Combine(root, "sources", "install.esd")) || File.Exists(Path.Combine(root, "sources", "boot.wim"));
            info.Efi = File.Exists(Path.Combine(root, "efi", "boot", "bootx64.efi")) || File.Exists(Path.Combine(root, "EFI", "BOOT", "BOOTAA64.EFI"));
            info.Bios = File.Exists(Path.Combine(root, "bootmgr")) || Directory.Exists(Path.Combine(root, "isolinux")) || Directory.Exists(Path.Combine(root, "syslinux"));
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                if (f.Length > info.LargestFile) { info.LargestFile = f.Length; info.LargestName = Path.GetRelativePath(root, f.FullName); }
        }
        finally { try { VirtualDisk.Dismount(iso); } catch { } }
        return info;
    }

    public static List<string> Problems(IsoInfo iso, PhysicalDisk d, BootMode mode)
    {
        var p = new List<string>();
        if (d.IsSystem || d.IsBoot) p.Add("No se puede usar el disco del sistema.");
        if (d.Size < iso.Size + (64L << 20)) p.Add($"El disco ({Fmt.Size(d.Size)}) es demasiado pequeño para esta ISO ({Fmt.Size(iso.Size)}).");
        if (mode != BootMode.Dd)
        {
            if (!iso.Efi) p.Add("La ISO no tiene arranque UEFI: usa el modo «Imagen DD».");
            if (iso.LargestFile >= Fat32Max && !iso.NeedsSplit) p.Add($"«{iso.LargestName}» ocupa más de 4 GB y no cabe en FAT32: usa el modo «Imagen DD».");
            if (mode == BootMode.UefiBios && d.Size > 0xFFFFFFFFL * 512) p.Add("MBR solo puede usar 2 TB: elige «UEFI (GPT)».");
        }
        else if (!iso.Hybrid) p.Add("Esta ISO no es híbrida: escrita tal cual no arrancará desde un USB. Usa un modo UEFI.");
        return p;
    }

    public static TransferJob Job(IsoInfo iso, PhysicalDisk d, BootMode mode, string label)
    {
        if (mode == BootMode.Dd)
        {
            var j = DiskImage.RestoreJob(iso.Path, d);
            return new TransferJob { Title = $"USB de arranque en disco {d.Number} (imagen DD)", Subtitle = $"{Path.GetFileName(iso.Path)} → {d.Title}", Glyph = Icons.Usb, Work = j.Work };
        }
        return new TransferJob
        {
            Title = $"USB de arranque en disco {d.Number}",
            Subtitle = $"{Path.GetFileName(iso.Path)} → {d.Title} · " + (mode == BootMode.Uefi ? "UEFI (GPT)" : "UEFI + BIOS (MBR)"),
            Glyph = Icons.Usb,
            Work = ctx => Task.Run(() => Create(ctx, iso, d, mode, label))
        };
    }

    static void Create(JobContext ctx, IsoInfo iso, PhysicalDisk d, BootMode mode, string label)
    {
        var probs = Problems(iso, d, mode);
        if (probs.Count > 0) throw new InvalidOperationException(string.Join(" ", probs));
        ctx.SetCurrent("Montando la ISO…");
        var m = VirtualDisk.Mount(iso.Path, true);
        try
        {
            string src = (m.Letters.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? throw new IOException("Windows no asignó letra a la ISO.")) + "\\";
            var o = new FormatOptions { FileSystem = "FAT32", Label = Formatter.CleanLabel(string.IsNullOrWhiteSpace(label) ? iso.Label : label, "FAT32"), Quick = true, Table = mode == BootMode.Uefi ? TableStyle.Gpt : TableStyle.Mbr };
            var (letter, size) = Formatter.Repartition(ctx, d, o, mode == BootMode.UefiBios);
            ctx.SetCurrent($"Formateando {letter} en FAT32…");
            Formatter.FormatPartition(ctx, letter, size, o);
            string dst = letter + "\\";

            // Copiar los archivos (salvo install.wim si hay que dividirlo)
            var files = new DirectoryInfo(src).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
            var split = iso.NeedsSplit ? files.First(f => f.Length >= Fat32Max && f.Name.EndsWith(".wim", StringComparison.OrdinalIgnoreCase)) : null;
            ctx.ResetProgress(files.Sum(f => f.Length));
            var buf = new byte[4 << 20];
            foreach (var f in files)
            {
                ctx.Check();
                string rel = Path.GetRelativePath(src, f.FullName);
                if (f == split) continue;
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ctx.SetCurrent("Copiando " + rel);
                using (var i = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                using (var w = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1))
                {
                    int n;
                    while ((n = i.Read(buf)) > 0) { ctx.Check(); w.Write(buf, 0, n); ctx.AddBytes(n); }
                }
                File.SetAttributes(target, FileAttributes.Normal);
                ctx.FileDone();
            }
            if (split != null)
            {
                string rel = Path.GetRelativePath(src, split.FullName);
                string swm = Path.Combine(dst, Path.ChangeExtension(rel, ".swm"));
                Directory.CreateDirectory(Path.GetDirectoryName(swm)!);
                ctx.SetCurrent($"Dividiendo {split.Name} ({Fmt.Size(split.Length)}) en partes de menos de 4 GB…");
                long before = ctx.BytesDone;
                var (code, outp) = VolumeRepair.RunLive("dism.exe", $"/Split-Image /ImageFile:\"{split.FullName}\" /SWMFile:\"{swm}\" /FileSize:3800", ctx, line =>
                {
                    var mm = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})(?:[.,]\d+)?\s*%");
                    if (mm.Success && int.TryParse(mm.Groups[1].Value, out int pct))
                    {
                        long want = before + split.Length * pct / 100;
                        long now = ctx.BytesDone;
                        if (want > now) ctx.AddBytes(want - now);
                        ctx.SetCurrent($"Dividiendo {split.Name}: {pct}%");
                    }
                });
                if (code != 0) throw new IOException("DISM no pudo dividir la imagen de Windows: " + outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
            }

            // Arranque BIOS para Windows en MBR
            if (mode == BootMode.UefiBios && iso.Windows)
            {
                string bs = Path.Combine(src, "boot", "bootsect.exe");
                if (File.Exists(bs))
                {
                    ctx.SetCurrent("Escribiendo el sector de arranque BIOS…");
                    var (code, outp) = PartitionTable.RunTool(bs, $"/nt60 {letter} /mbr /force");
                    if (code != 0) Log.Warn("bootsect: " + outp);
                }
                else Log.Warn("La ISO no incluye bootsect.exe: el USB arrancará solo en modo UEFI.");
            }
            else if (mode == BootMode.UefiBios && !iso.Windows)
                Log.Warn("En ISOs que no son de Windows, el arranque BIOS desde FAT32 no está garantizado: el USB arrancará en UEFI.");
            Log.Ok($"USB de arranque creado en {letter} ({d.Title}) desde {Path.GetFileName(iso.Path)}");
        }
        finally { try { VirtualDisk.Dismount(iso.Path); } catch (Exception ex) { Log.Warn(ex.Message); } }
    }
}
