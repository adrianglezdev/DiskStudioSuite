using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DiskStudio.Core;

static class PlanTests
{
    const long MiB = 1048576, GiB = 1024 * MiB;

    public static int Run(Action<bool, string> check)
    {
        Console.WriteLine("== Planificador de clonado (lógica)");
        // Disco MBR típico de Windows con BIOS: Reservada para el sistema + Windows + Recuperación
        var mbr = new DiskLayout { Style = TableStyle.Mbr, DiskSize = 240 * GiB, SectorSize = 512 };
        mbr.Parts.Add(new PartEntry { Offset = MiB, Length = 350 * MiB, MbrType = 0x07, Active = true, Source = 0 });
        mbr.Parts.Add(new PartEntry { Offset = 351 * MiB, Length = 200 * GiB, MbrType = 0x07, Source = 1 });
        mbr.Parts.Add(new PartEntry { Offset = 351 * MiB + 200 * GiB, Length = 600 * MiB, MbrType = 0x27, Source = 2 });
        var plan = PartitionTable.PlanClone(mbr, 480 * GiB, 512, TableStyle.Gpt, true, _ => "NTFS", i => i == 1, i => i == 0);
        check(plan.Ok, "MBR→GPT con Windows: plan válido");
        check(plan.Target.Parts[0].Role == "EFI" && plan.Target.Parts[0].FormatAs == "FAT32", "crea partición EFI al principio");
        check(plan.Target.Parts[1].Role == "MSR", "crea partición MSR");
        check(!plan.Target.Parts.Any(p => p.Source == 0), "quita la «Reservada para el sistema»");
        check(plan.Target.Parts.Any(p => p.Role == "Recuperación" && p.GptType == PartitionTable.GptRecovery), "recuperación con tipo GPT correcto");
        check(plan.Target.Parts.All(p => p.Offset % MiB == 0), "todas alineadas a 1 MiB");
        check(plan.RebuildBoot && plan.Uefi, "marca reconstruir arranque UEFI");

        var gpt = new DiskLayout { Style = TableStyle.Gpt, DiskSize = 500 * GiB, SectorSize = 512 };
        gpt.Parts.Add(new PartEntry { Offset = MiB, Length = 100 * MiB, GptType = PartitionTable.GptEsp, Source = 0 });
        gpt.Parts.Add(new PartEntry { Offset = 101 * MiB, Length = 16 * MiB, GptType = PartitionTable.GptMsr, Source = 1 });
        gpt.Parts.Add(new PartEntry { Offset = 117 * MiB, Length = 300 * GiB, GptType = PartitionTable.GptBasic, Source = 2 });
        gpt.Parts.Add(new PartEntry { Offset = 117 * MiB + 300 * GiB, Length = 700 * MiB, GptType = PartitionTable.GptRecovery, Source = 3 });
        plan = PartitionTable.PlanClone(gpt, 500 * GiB, 512, TableStyle.Mbr, false, i => i == 0 ? "FAT32" : "NTFS", i => i == 2, i => i == 0);
        check(plan.Ok, "GPT→MBR con Windows: plan válido");
        check(!plan.Target.Parts.Any(p => p.Role == "MSR") && plan.Target.Parts.Count == 3, "omite MSR (quedan 3 particiones)");
        var esp = plan.Target.Parts[0];
        check(esp.Active && esp.MbrType == 0x0C, "EFI → partición activa FAT32 (0x0C)");
        check(plan.Target.Parts[2].MbrType == 0x27, "recuperación → tipo 0x27");
        check(plan.RebuildBoot && !plan.Uefi, "marca reconstruir arranque BIOS");

        var xp = new DiskLayout { Style = TableStyle.Mbr, DiskSize = 80 * GiB, SectorSize = 512 };
        xp.Parts.Add(new PartEntry { Offset = 63 * 512, Length = 40 * GiB, MbrType = 0x07, Active = true, Source = 0 });
        plan = PartitionTable.PlanClone(xp, 120 * GiB, 512, null, true, _ => "NTFS", _ => false, _ => false);
        check(plan.Ok && plan.Target.Parts[0].Offset == MiB && plan.Target.Style == TableStyle.Mbr, "SSD: partición de XP (sector 63) realineada a 1 MiB");
        plan = PartitionTable.PlanClone(xp, 120 * GiB, 512, null, false, _ => "NTFS", _ => false, _ => false);
        check(plan.Target.Parts[0].Offset == 63 * 512, "sin optimizar: conserva la posición exacta");

        var five = new DiskLayout { Style = TableStyle.Gpt, DiskSize = 100 * GiB, SectorSize = 512 };
        for (int i = 0; i < 5; i++) five.Parts.Add(new PartEntry { Offset = MiB + i * 10 * GiB, Length = 10 * GiB, GptType = PartitionTable.GptBasic, Source = i });
        plan = PartitionTable.PlanClone(five, 100 * GiB, 512, TableStyle.Mbr, false, _ => "NTFS", _ => false, _ => false);
        check(!plan.Ok && plan.Errors.Any(e => e.Contains("4 particiones")), "GPT→MBR con 5 particiones: rechazado");
        plan = PartitionTable.PlanClone(mbr, 100 * GiB, 512, null, true, _ => "NTFS", _ => false, _ => false);
        check(!plan.Ok && plan.Errors.Any(e => e.Contains("no caben")), "destino demasiado pequeño: rechazado");
        plan = PartitionTable.PlanClone(mbr, 4096 * GiB, 4096, null, true, _ => "NTFS", _ => false, _ => false);
        check(!plan.Ok, "distinto tamaño de sector: rechazado");

        // Destino algo más pequeño (466 GB → 447 GB): se reduce la partición de datos si sus datos caben
        var big = new DiskLayout { Style = TableStyle.Mbr, DiskSize = 466 * GiB, SectorSize = 512 };
        big.Parts.Add(new PartEntry { Offset = MiB, Length = 350 * MiB, MbrType = 0x07, Active = true, Source = 0 });
        big.Parts.Add(new PartEntry { Offset = 351 * MiB, Length = 466 * GiB - 352 * MiB, MbrType = 0x07, Source = 1 });
        plan = PartitionTable.PlanClone(big, 447 * GiB, 512, null, true, _ => "NTFS", _ => false, i => i == 0, i => i == 0 ? 40 * MiB : 214 * GiB, i => i == 1 ? "E:" : null);
        var shr = plan.Target.Parts.FirstOrDefault(p => p.CopyFiles);
        check(plan.Ok && shr != null && shr.Source == 1 && plan.Target.Parts.All(p => p.End <= 447 * GiB), "466 GB → 447 GB: la partición de datos se reduce y todo cabe");
        check(shr != null && shr.ShrunkFrom > shr.Length && shr.Length % MiB == 0 && shr.Length >= 214 * GiB, $"reducida de {Fmt.Size(shr?.ShrunkFrom ?? 0)} a {Fmt.Size(shr?.Length ?? 0)}, alineada a 1 MiB");
        check(!plan.Target.Parts[0].CopyFiles && plan.Target.Parts[0].Length == 350 * MiB, "la partición pequeña se copia igual, sector a sector");
        check(plan.Notes.Any(n => n.Contains("E:") && n.Contains("archivo a archivo")), "la vista previa explica qué se reduce y cómo");
        plan = PartitionTable.PlanClone(big, 447 * GiB, 512, null, false, _ => "NTFS", _ => false, _ => false, i => i == 0 ? 40 * MiB : 214 * GiB);
        check(plan.Ok && plan.Target.Parts.Any(p => p.CopyFiles), "sin «Optimizar para SSD» también se recoloca y se reduce");
        plan = PartitionTable.PlanClone(big, 447 * GiB, 512, null, true, _ => "NTFS", _ => false, _ => false, i => i == 0 ? 40 * MiB : 460 * GiB);
        check(!plan.Ok && plan.Errors.Any(e => e.Contains("ocupan")), "si los datos no caben, se rechaza explicando por qué");
        plan = PartitionTable.PlanClone(big, 447 * GiB, 512, null, true, _ => "NTFS", i => i == 1, _ => false, i => i == 0 ? 40 * MiB : 214 * GiB);
        check(plan.Ok && plan.Notes.Any(n => n.Contains("contiene Windows")), "si es la partición de Windows, avisa de que es más seguro reducirla en el origen");
        plan = PartitionTable.PlanClone(big, 447 * GiB, 512, null, true, _ => "NTFS", _ => false, _ => false);
        check(!plan.Ok, "sin saber cuánto ocupan los datos, no se reduce nada");

        var full = new DiskLayout { Style = TableStyle.Mbr, DiskSize = 100 * GiB, SectorSize = 512 };
        full.Parts.Add(new PartEntry { Offset = MiB, Length = 100 * GiB - MiB, MbrType = 0x07, Source = 0 });
        var errs = new List<string>();
        var conv = PartitionTable.Convert(full, TableStyle.Gpt, _ => "NTFS", errs);
        check(errs.Any(e => e.Contains("final del disco")), "conversión in situ: detecta falta de sitio para la copia GPT");
        full.Parts[0].Length -= MiB; errs.Clear();
        conv = PartitionTable.Convert(full, TableStyle.Gpt, _ => "NTFS", errs);
        check(errs.Count == 0 && conv.Parts[0].Offset == MiB && conv.Parts[0].GptType == PartitionTable.GptBasic, "conversión in situ MBR→GPT conserva posiciones");
        var back = PartitionTable.Convert(conv, TableStyle.Mbr, _ => "NTFS", errs);
        check(errs.Count == 0 && back.Parts[0].MbrType == 0x07 && back.Parts[0].Length == full.Parts[0].Length, "y vuelta a MBR idéntica");
        return 0;
    }

    // ═════════════ Integración en discos virtuales (requiere administrador) ═════════════
    public static int Vhd(int a, int b, string hashFile, Action<bool, string> check, int small = -1)
    {
        Console.WriteLine($"== Integración en VHD: origen disco {a}, destino disco {b}");
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(hashFile))!;
        (List<PhysicalDisk> disks, List<VolumeInfo> vols) scan() { var r = DiskScanner.Scan(false); return r; }
        PhysicalDisk D(int n) => scan().disks.First(d => d.Number == n);
        void run(TransferJob job)
        {
            var ctx = new JobContext(job);
            job.Work(ctx).GetAwaiter().GetResult();
            foreach (var e in job.Errors) Console.WriteLine($"       error: {e.Path}: {e.Error}");
            Console.WriteLine($"       {job.Title}: {Fmt.Size(job.bytesDone)} copiados");
            PartitionTable.RefreshStorageCache();
            Thread.Sleep(3000);
        }
        bool verify(int disk, string tag)
        {
            Thread.Sleep(2500);
            var d = D(disk);
            var data = d.Partitions.OrderByDescending(p => p.Size).First();
            string letter = PartitionTable.AssignLetter(disk, data.Offset);
            int bad = 0;
            foreach (var (rel, hash) in expected)
            {
                string f = Path.Combine(letter + "\\", rel);
                bool exists = File.Exists(f);
                if (!exists || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) != hash)
                {
                    if (bad < 3) Console.WriteLine($"       {(exists ? "distinto" : "falta")}: {rel}");
                    bad++;
                }
            }
            if (bad > 0) Console.WriteLine($"       {bad} de {expected.Count} con problemas; en {letter} hay {Directory.GetFiles(letter + Path.DirectorySeparatorChar, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Length} archivos");
            check(bad == 0, $"{tag}: {expected.Count} archivos idénticos (SHA-256) en {letter}");
            return bad == 0;
        }

        var src = D(a); var dst = D(b);
        check(src.PartitionStyle == "MBR" && src.Partitions.Any(p => p.Offset % MiB != 0), "origen MBR con una partición desalineada");

        run(SmartClone.Job(src, dst, new SmartCloneOptions { Style = TableStyle.Gpt, OptimizeSsd = true, UseSnapshot = true }));
        dst = D(b);
        check(dst.PartitionStyle == "GPT", "clonado: destino convertido a GPT");
        check(dst.Partitions.All(p => p.Offset % MiB == 0), "clonado: particiones alineadas a 1 MiB");
        check(dst.Partitions.Count == src.Partitions.Count, $"clonado: {dst.Partitions.Count} particiones");
        verify(b, "clonado inteligente MBR→GPT (instantánea + solo bloques usados)");

        run(PartitionTable.ConvertJob(D(b), TableStyle.Mbr));
        check(D(b).PartitionStyle == "MBR", "conversión sin pérdida GPT→MBR");
        verify(b, "tras GPT→MBR");

        run(PartitionTable.ConvertJob(D(b), TableStyle.Gpt));
        check(D(b).PartitionStyle == "GPT", "conversión sin pérdida MBR→GPT");
        verify(b, "tras MBR→GPT");

        var backup = PartitionTable.ListBackups(D(b).Serial).First(x => x.Disk == b);
        check(backup.Style == TableStyle.Mbr, "existe copia de la tabla anterior (MBR)");
        run(PartitionTable.RestoreJob(D(b), backup.File));
        check(D(b).PartitionStyle == "MBR", "restaurar copia de la tabla");
        verify(b, "tras restaurar la tabla");

        run(SmartClone.Job(D(a), D(b), new SmartCloneOptions { Style = null, OptimizeSsd = false, UseSnapshot = false }));
        verify(b, "clonado completo sin optimizar ni instantánea");

        if (small >= 0)
        {
            Console.WriteLine($"== Clonar a un disco más pequeño (disco {small}, {Fmt.Size(D(small).Size)})");
            var preview = SmartClone.Preview(D(a), D(small), new SmartCloneOptions { OptimizeSsd = true });
            var red = preview.Target.Parts.FirstOrDefault(p => p.CopyFiles);
            check(preview.Ok && red != null, $"vista previa: la partición de datos se reduce de {Fmt.Size(red?.ShrunkFrom ?? 0)} a {Fmt.Size(red?.Length ?? 0)}");
            foreach (var e in preview.Errors) Console.WriteLine("       " + e);
            run(SmartClone.Job(D(a), D(small), new SmartCloneOptions { OptimizeSsd = true, UseSnapshot = true }));
            var sd = D(small);
            check(sd.Partitions.Count == 2 && sd.Partitions.All(p => p.Offset + p.Size <= sd.Size), "clonado: dos particiones dentro del disco más pequeño");
            check(sd.Partitions.Any(p => p.FileSystem == "NTFS" && p.Label == "DATOS"), "la partición reducida conserva sistema de archivos y nombre");
            verify(small, "clonado a disco más pequeño (copia archivo a archivo desde la instantánea)");
        }
        return 0;
    }
}
