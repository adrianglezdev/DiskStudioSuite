using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using DiskStudio.Core;
using Microsoft.Win32.SafeHandles;

static class FormatTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr i, int si, IntPtr o, int so, out int r, IntPtr ov);

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== Formateador FAT32 (imagen dispersa de 40 GB)");
        string img = Path.Combine(Path.GetTempPath(), "dss-fat32.img");
        long size = 40L << 30;
        try
        {
            using (var fs = new FileStream(img, FileMode.Create, FileAccess.ReadWrite))
            {
                DeviceIoControl(fs.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // FSCTL_SET_SPARSE
                fs.SetLength(size);
                var L = Fat32.Write(fs, size, 512, 0, "Mi Pendrive", 2048);
                check(L.SectorsPerCluster * 512 == 32768, "40 GB → clúster de 32 KB");
                check(L.Clusters >= 65525 && L.Clusters < 0x0FFFFFF5, $"{L.Clusters:N0} clústeres (rango válido de FAT32)");
                check((long)L.FatSectors * 512 / 4 >= L.Clusters + 2, "la FAT tiene entradas para todos los clústeres");
                check((2048 + L.DataStart / 512) % 2048 == 0, "zona de datos alineada a 1 MiB");

                byte[] s(long sector) { var b = new byte[512]; fs.Position = sector * 512; fs.ReadExactly(b); return b; }
                var boot = s(0); var bak = s(6); var info = s(1);
                check(boot[510] == 0x55 && boot[511] == 0xAA && System.Text.Encoding.ASCII.GetString(boot, 82, 8) == "FAT32   ", "sector de arranque FAT32 válido");
                check(boot.AsSpan().SequenceEqual(bak), "copia del sector de arranque idéntica (sector 6)");
                check(BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)) == 512 && boot[16] == 2 && BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44)) == 2, "BPB: 512 B/sector, 2 FAT, raíz en el clúster 2");
                check(BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32)) == size / 512, "número total de sectores correcto");
                check(BinaryPrimitives.ReadUInt32LittleEndian(info) == 0x41615252 && BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(484)) == 0x61417272, "FSInfo con firmas correctas");
                check(BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(488)) == L.Clusters - 1, "FSInfo: clústeres libres = total − raíz");
                var fat1 = s(L.Reserved); var fat2 = s(L.Reserved + L.FatSectors);
                check(BinaryPrimitives.ReadUInt32LittleEndian(fat1) == 0x0FFFFFF8 && BinaryPrimitives.ReadUInt32LittleEndian(fat1.AsSpan(8)) == 0x0FFFFFFF, "FAT: medio y fin de cadena de la raíz");
                check(fat1.AsSpan().SequenceEqual(fat2), "las dos copias de la FAT coinciden");
                var root = s(L.DataStart / 512);
                check(System.Text.Encoding.ASCII.GetString(root, 0, 11) == "MI PENDRIVE" && root[11] == 0x08, "etiqueta en la carpeta raíz");
                check(new FileInfo(img).Length == size, "imagen de 40 GB generada");
            }
            bool small = false;
            try { Fat32.Compute(16L << 20, 512, 0); } catch (InvalidOperationException) { small = true; }
            check(small, "rechaza FAT32 en un volumen demasiado pequeño (16 MB)");
            check(Fat32.Compute(8L << 30, 512, 0).SectorsPerCluster == 16, "8 GB → clúster de 8 KB");
            check(Formatter.CleanLabel("mi:usb*grande_2026", "FAT32") == "MIUSBGRANDE", "etiqueta FAT32 saneada (11 caracteres, sin símbolos)");
            check(Formatter.Problems(64L << 30, new FormatOptions { FileSystem = "exFAT", Compress = true }).Count == 1, "compresión solo permitida en NTFS");
        }
        finally { try { File.Delete(img); } catch { } }
    }

    // Integración (administrador): disco virtual de 40 GB
    public static void Vhd(int disk, Action<bool, string> check)
    {
        Console.WriteLine($"== Formatear en VHD de 40 GB (disco {disk})");
        PhysicalDisk D() { PartitionTable.RefreshStorageCache(); Thread.Sleep(2000); return DiskScanner.Scan(false).disks.First(d => d.Number == disk); }
        void run(TransferJob j)
        {
            j.Work(new JobContext(j)).GetAwaiter().GetResult();
            foreach (var e in j.Errors) Console.WriteLine($"       error: {e.Path}: {e.Error}");
        }
        run(Formatter.ResetDiskJob(D(), new FormatOptions { FileSystem = "FAT32", Label = "PRUEBA32", Table = TableStyle.Mbr }));
        var d = D();
        var v = d.Volumes.FirstOrDefault();
        check(d.PartitionStyle == "MBR" && d.Partitions.Count == 1, "reiniciar disco: MBR con una sola partición");
        check(v != null && v.FileSystem == "FAT32" && v.Total > (32L << 30), $"FAT32 de {Fmt.Size(v?.Total ?? 0)} reconocido por Windows en {v?.Letter}");
        if (v != null)
        {
            File.WriteAllBytes(v.Root + "prueba.bin", new byte[5 << 20]);
            check(new FileInfo(v.Root + "prueba.bin").Length == 5 << 20, "se puede escribir y leer en el FAT32 creado");
            var (code, outp) = PartitionTable.RunTool("chkdsk.exe", v.Letter);
            check(code == 0, "chkdsk de Windows no encuentra errores en el FAT32");
            if (code != 0) Console.WriteLine(outp);
            run(Formatter.FormatVolumeJob(D().Volumes.First(), new FormatOptions { FileSystem = "exFAT", Label = "EXFAT", Quick = true }));
            check(D().Volumes.First().FileSystem == "exFAT", "formatear volumen como exFAT");
            run(Formatter.FormatVolumeJob(D().Volumes.First(), new FormatOptions { FileSystem = "NTFS", Label = "NTFS", Quick = true, Compress = true }));
            check(D().Volumes.First().FileSystem == "NTFS", "formatear volumen como NTFS con compresión");
        }
        run(Formatter.ResetDiskJob(D(), new FormatOptions { FileSystem = "exFAT", Label = "GPTDISK", Table = TableStyle.Gpt }));
        d = D();
        check(d.PartitionStyle == "GPT" && d.Volumes.FirstOrDefault()?.FileSystem == "exFAT", "reiniciar disco como GPT + exFAT");
    }
}
