using System.IO;
using System.Security.Cryptography;
using DiskStudio.Core;

/// <summary>Integración de las funciones de la 0.2 sobre discos virtuales (requiere administrador).</summary>
static class IntegrationNew
{
    const long MiB = 1 << 20;

    static PhysicalDisk D(int n) { PartitionTable.RefreshStorageCache(); Thread.Sleep(2500); return DiskScanner.Scan(false).disks.First(d => d.Number == n); }

    static void Run(TransferJob j, Action<bool, string> check, string what)
    {
        try { j.Work(new JobContext(j)).GetAwaiter().GetResult(); }
        catch (Exception ex) { check(false, $"{what}: {ex.GetType().Name}: {ex.Message}"); throw; }
        foreach (var e in j.Errors) Console.WriteLine($"       aviso: {e.Path}: {e.Error}");
    }

    static string Hash(string f) { using var s = File.OpenRead(f); return Convert.ToHexString(SHA256.HashData(s)); }

    /// <summary>Crea una ISO pequeña con arranque UEFI simulado (efi\boot\bootx64.efi) usando IMAPI2 de Windows.</summary>
    public static string MakeIso(string work)
    {
        string src = Path.Combine(work, "iso-src");
        Directory.CreateDirectory(Path.Combine(src, "efi", "boot"));
        File.WriteAllBytes(Path.Combine(src, "efi", "boot", "bootx64.efi"), RandomNumberGenerator.GetBytes(300_000));
        File.WriteAllText(Path.Combine(src, "leeme.txt"), "ISO de prueba de Disk Studio Suite");
        Directory.CreateDirectory(Path.Combine(src, "datos"));
        File.WriteAllBytes(Path.Combine(src, "datos", "grande.bin"), RandomNumberGenerator.GetBytes(12 << 20));
        string iso = Path.Combine(work, "prueba.iso");
        Exception err = null;
        var t = new Thread(() =>
        {
            try
            {
                dynamic fsi = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")!)!;
                fsi.FileSystemsToCreate = 3;   // ISO 9660 + Joliet
                fsi.VolumeName = "DSSPRUEBA";
                fsi.Root.AddTree(src, false);
                dynamic res = fsi.CreateResultImage();
                var stream = (System.Runtime.InteropServices.ComTypes.IStream)res.ImageStream;
                using var fs = File.Create(iso);
                var buf = new byte[1 << 20];
                IntPtr pRead = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                try
                {
                    while (true)
                    {
                        stream.Read(buf, buf.Length, pRead);
                        int n = System.Runtime.InteropServices.Marshal.ReadInt32(pRead);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pRead); }
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (err != null) throw err;
        return iso;
    }

    /// <param name="ed">disco del editor de particiones (vacío, 2 GB)</param>
    /// <param name="rs">disco donde se restaura la imagen (vacío, 2 GB)</param>
    /// <param name="iso">ISO de prueba con efi\boot\bootx64.efi</param>
    public static void Vhd(int ed, int rs, string iso, string work, Action<bool, string> check)
    {
        if (iso == "auto") iso = MakeIso(work);
        void step(string name, Action a)
        {
            Console.WriteLine("== " + name);
            try { a(); } catch (Exception ex) { check(false, $"{name}: excepción {ex.GetType().Name}: {ex.Message}"); Console.WriteLine(ex.StackTrace); }
        }

        string letter = null;
        step("Editor de particiones", () =>
        {
            var d = D(ed);
            if (d.PartitionStyle == "Sin inicializar") { Run(PartitionEditor.InitializeJob(d, TableStyle.Gpt), check, "inicializar"); d = D(ed); }
            check(d.PartitionStyle == "GPT", "disco inicializado como GPT");
            var free = PartitionEditor.Segments(d).First(s => s.IsFree && s.Size > 1000 * MiB);
            string want = PartitionEditor.FreeLetters().Last();
            Run(PartitionEditor.CreateJob(d, free, 800 * MiB, new FormatOptions { FileSystem = "NTFS", Label = "EDITOR", Quick = true }, want), check, "crear");
            d = D(ed);
            var p = d.Partitions.FirstOrDefault(x => x.FileSystem == "NTFS");
            check(p != null && Math.Abs(p.Size - 800 * MiB) < 2 * MiB, $"partición NTFS de 800 MB creada ({Fmt.Size(p?.Size ?? 0)})");
            check(p?.Letter?.TrimEnd('\\') == want, $"con la letra pedida ({want})");
            letter = p.Letter.TrimEnd('\\');
            File.WriteAllBytes(letter + @"\datos.bin", RandomNumberGenerator.GetBytes(20 << 20));
            string h0 = Hash(letter + @"\datos.bin");

            var (min, max) = PartitionEditor.SupportedSize(ed, p.Offset);
            check(min > 0 && min < p.Size && max >= p.Size, $"tamaños admitidos: {Fmt.Size(min)} – {Fmt.Size(max)}");
            Run(PartitionEditor.ResizeJob(d, p, 500 * MiB), check, "reducir");
            d = D(ed); p = d.Partitions.First(x => x.FileSystem == "NTFS");
            check(Math.Abs(p.Size - 500 * MiB) < 2 * MiB, "reducida a 500 MB");
            Run(PartitionEditor.ResizeJob(d, p, 900 * MiB), check, "ampliar");
            d = D(ed); p = d.Partitions.First(x => x.FileSystem == "NTFS");
            check(Math.Abs(p.Size - 900 * MiB) < 2 * MiB, "ampliada a 900 MB");
            check(Hash(letter + @"\datos.bin") == h0, "los datos siguen intactos tras reducir y ampliar");

            Run(PartitionEditor.SplitJob(d, p, 600 * MiB, new FormatOptions { FileSystem = "exFAT", Label = "NUEVA", Quick = true }, null), check, "dividir");
            d = D(ed);
            var parts = d.Partitions.Where(x => x.Offset >= p.Offset).OrderBy(x => x.Offset).ToList();
            Console.WriteLine("       particiones: " + string.Join(", ", parts.Select(x => $"{Fmt.Size(x.Size)} {x.FileSystem} «{x.Label}»")));
            check(parts.Count == 2 && parts[1].FileSystem == "exFAT" && parts[1].Label == "NUEVA" && Math.Abs(parts[0].Size - 600 * MiB) < 2 * MiB, "dividida: 600 MB NTFS + partición exFAT nueva sin letra");
            check(Hash(letter + @"\datos.bin") == h0, "los datos siguen intactos tras dividir");

            var nueva = parts[1];
            string l2 = PartitionEditor.FreeLetters().First();
            PartitionEditor.SetLetter(d, nueva, l2);
            d = D(ed);
            check(d.Partitions.Any(x => x.Letter?.TrimEnd('\\') == l2), $"letra asignada a la partición nueva ({l2})");
            nueva = d.Partitions.First(x => x.Letter?.TrimEnd('\\') == l2);
            check(PartitionEditor.CannotDelete(d, nueva) == null, "una partición de datos se puede eliminar");
            int backupsBefore = Directory.GetFiles(Path.Combine(Settings.DataDir, "TablasParticiones"), "*.bin").Length;
            Run(PartitionEditor.DeleteJob(d, nueva), check, "eliminar");
            d = D(ed);
            check(d.Partitions.Count(x => x.Offset >= p.Offset) == 1, "partición eliminada");
            check(Directory.GetFiles(Path.Combine(Settings.DataDir, "TablasParticiones"), "*.bin").Length > backupsBefore, "se guardó copia de la tabla antes de tocarla");
            var sys = DiskScanner.Scan(false).disks.First(x => x.IsSystem);
            var winPart = sys.Partitions.FirstOrDefault(x => x.IsBoot) ?? sys.Partitions.First();
            check(PartitionEditor.CannotDelete(sys, winPart) != null, "la partición de Windows no se puede eliminar");
        });
        if (letter == null) return;
        var vol = DiskScanner.Scan(false).volumes.First(v => v.Letter.TrimEnd('\\').Equals(letter, StringComparison.OrdinalIgnoreCase));

        step("Comprobar, reparar y optimizar", () =>
        {
            Run(VolumeRepair.Job(vol, RepairMode.Scan), check, "comprobar");
            Run(VolumeRepair.Job(vol, RepairMode.Fix), check, "reparar");
            Run(VolumeRepair.Job(vol, RepairMode.Optimize), check, "optimizar");
            check(true, "chkdsk /scan, chkdsk /f /x y defrag /O terminan sin error");
        });

        step("BitLocker y bloqueos", () =>
        {
            var b = BitLocker.Get(letter);
            check(b.Detailed && !b.Encrypted && !b.Locked, $"BitLocker leído como administrador: {b.StatusText}");
            using var open = new FileStream(letter + @"\abierto.txt", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var found = LockFinder.Find(letter);
            check(found.Any(l => l.IsSelf && l.Files.Any(f => f.EndsWith("abierto.txt", StringComparison.OrdinalIgnoreCase))), "detecta el archivo abierto en la unidad");
        });

        step("Capacidad real", () =>
        {
            var r = CapacityCheck.Run(letter + @"\" + CapacityCheck.FolderName, 96 * MiB, true, new JobContext(new TransferJob { Work = _ => Task.CompletedTask }));
            check(r.Passed && r.Tested == 96 * MiB, $"96 MB comprobados sin errores (escritura {Fmt.Speed(r.WriteSpeed)}, lectura {Fmt.Speed(r.ReadSpeed)})");
        });

        string img = Path.Combine(work, "editor.img"), inc = Path.Combine(work, "editor_inc1.dssinc");
        string h1 = null;
        step("Imagen completa + incremental y restauración", () =>
        {
            Run(DiskImage.CreateImageJob(D(ed), img, false), check, "imagen completa");
            check(File.Exists(IncrementalImage.BlocksFile(img)), "la imagen completa guarda su índice de bloques");
            File.WriteAllBytes(letter + @"\cambio.bin", RandomNumberGenerator.GetBytes(7 << 20));
            h1 = Hash(letter + @"\cambio.bin");
            Run(IncrementalImage.CreateJob(D(ed), img, inc), check, "imagen incremental");
            var hdr = IncrementalImage.ReadHeader(inc);
            check(hdr.ChangedBlocks > 0 && hdr.ChangedBlocks < 200, $"incremental con {hdr.ChangedBlocks} bloques cambiados (de {D(ed).Size / MiB})");
            check(new FileInfo(inc).Length < new FileInfo(img).Length / 4, $"la incremental ocupa poco ({Fmt.Size(new FileInfo(inc).Length)} frente a {Fmt.Size(new FileInfo(img).Length)})");
            long edSize = D(ed).Size;
            // El disco restaurado tendrá el mismo identificador GPT que el original: desconectar el original antes
            VirtualDisk.DismountDisk(ed);
            var target = D(rs);
            check(!target.IsSystem && target.Size >= edSize, "disco de restauración válido");
            Run(DiskImage.RestoreJob(inc, target), check, "restaurar cadena");
            VirtualDisk.PowerShell($"Set-Disk -Number {rs} -IsOffline $false -ErrorAction SilentlyContinue; Update-HostStorageCache");
            Thread.Sleep(3000);
            var rd = D(rs);
            var rp = rd.Partitions.FirstOrDefault(x => x.FileSystem == "NTFS");
            check(rp != null, "el disco restaurado tiene la partición NTFS");
            if (rp != null)
            {
                string rl = string.IsNullOrEmpty(rp.Letter) ? PartitionTable.AssignLetter(rs, rp.Offset) : rp.Letter.TrimEnd('\\');
                bool ok = File.Exists(rl + @"\cambio.bin") && Hash(rl + @"\cambio.bin") == h1;
                if (!ok) Console.WriteLine($"       cambio.bin en {rl}: existe {File.Exists(rl + @"\cambio.bin")}");
                check(ok, "el archivo creado después de la imagen completa está en la restauración");
                check(File.Exists(rl + @"\datos.bin"), "y también los anteriores");
            }
            Run(DiskImage.VerifyImageJob(inc, D(rs)), check, "verificar");
        });

        step("USB de arranque (modo archivos)", () =>
        {
            var info = BootUsb.Inspect(iso);
            check(info.Efi && !info.Windows, $"ISO analizada: {info.Kind}, UEFI {info.Efi}");
            var target = D(rs);
            var probs = BootUsb.Problems(info, target, BootMode.Uefi);
            check(probs.Count == 0, "sin problemas para el modo UEFI" + (probs.Count > 0 ? ": " + string.Join(" ", probs) : ""));
            Run(BootUsb.Job(info, target, BootMode.Uefi, "ARRANQUE"), check, "crear USB");
            var d = D(rs);
            var v = d.Volumes.FirstOrDefault();
            check(d.PartitionStyle == "GPT" && v?.FileSystem == "FAT32", "USB: GPT + FAT32");
            check(v != null && File.Exists(v.Root + @"efi\boot\bootx64.efi") && File.Exists(v.Root + @"leeme.txt"), "archivos de la ISO copiados");
            check(!VirtualDisk.Mounted.Any(x => x.Path == iso), "la ISO se desmonta al terminar");
        });
        step("Convertir y montar", () =>
        {
            if (!File.Exists(img)) return;
            string vhdx = Path.Combine(work, "conv.vhdx"), vhd = Path.Combine(work, "conv.vhd");
            Run(VirtualDisk.ConvertJob(inc, vhdx), check, "convertir a VHDX");
            check(File.Exists(vhdx) && new FileInfo(vhdx).Length < new FileInfo(img).Length, $"VHDX dinámico creado ({Fmt.Size(new FileInfo(vhdx).Length)})");
            var m = VirtualDisk.Mount(vhdx, true);
            try
            {
                string ml = m.Letters.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                Console.WriteLine($"       VHDX montado: disco {m.DiskNumber}, letras «{m.Letters}»");
                check(ml != null && Hash(ml + @"\cambio.bin") == h1, $"el VHDX montado en solo lectura ({ml}) contiene los datos");
            }
            finally { VirtualDisk.Dismount(vhdx); }
            Run(VirtualDisk.ConvertJob(img, vhd), check, "convertir a VHD");
            check(VirtualDisk.IsFixedVhd(vhd, out long vs) && vs == new FileInfo(img).Length, "VHD fijo válido");
            var m2 = VirtualDisk.Mount(vhd, true);
            try { check(m2.Letters.Length > 0 && File.Exists(m2.Letters.Split(' ')[0] + @"\datos.bin"), "el VHD fijo se monta y se lee"); }
            finally { VirtualDisk.Dismount(vhd); }
            Run(VirtualDisk.ConvertJob(vhdx, Path.Combine(work, "back.img")), check, "VHDX → IMG");
            check(new FileInfo(Path.Combine(work, "back.img")).Length == new FileInfo(img).Length, "VHDX → IMG conserva el tamaño del disco");
        });

    }
}
