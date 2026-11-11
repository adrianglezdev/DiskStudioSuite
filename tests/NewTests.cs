using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using DiskStudio.Core;

/// <summary>Pruebas de las funciones de la versión 0.2.</summary>
static class NewTests
{
    static void Run(TransferJob job) => job.Work(new JobContext(job)).GetAwaiter().GetResult();

    public static void Run(Action<bool, string> check)
    {
        string R = Path.Combine(Path.GetTempPath(), "dss-tests-02");
        if (Directory.Exists(R)) Directory.Delete(R, true);
        Directory.CreateDirectory(R);

        Console.WriteLine("== Pie de VHD fijo");
        var footer = VirtualDisk.VhdFooter(1L << 30, Guid.NewGuid());
        check(footer.Length == 512 && footer.AsSpan(0, 8).SequenceEqual("conectix"u8), "cabecera «conectix»");
        check(BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(64)) == VirtualDisk.Checksum(footer), "suma de comprobación correcta");
        check(BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48)) == 1UL << 30, "tamaño actual en big-endian");
        var (c, h, s) = VirtualDisk.Geometry(1L << 30);
        check((long)c * h * s <= (1L << 30) / 512 && c > 0, $"geometría CHS {c}/{h}/{s} no supera el disco");
        string vhd = Path.Combine(R, "x.vhd");
        File.WriteAllBytes(vhd, [.. new byte[4096], .. VirtualDisk.VhdFooter(4096, Guid.NewGuid())]);
        check(VirtualDisk.IsFixedVhd(vhd, out long ds) && ds == 4096, "se reconoce un VHD fijo y su tamaño");

        Console.WriteLine("== Conversión .img → .vhd → .img");
        string img = Path.Combine(R, "disk.img");
        var data = new byte[3 * 1024 * 1024 + 512];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(img, data);
        Run(VirtualDisk.ConvertJob(img, Path.Combine(R, "disk.vhd")));
        check(VirtualDisk.IsFixedVhd(Path.Combine(R, "disk.vhd"), out long vs) && vs == data.Length, "VHD creado con el tamaño de la imagen");
        Run(VirtualDisk.ConvertJob(Path.Combine(R, "disk.vhd"), Path.Combine(R, "back.img")));
        check(File.ReadAllBytes(Path.Combine(R, "back.img")).AsSpan().SequenceEqual(data), "el viaje de ida y vuelta conserva los datos");
        Run(VirtualDisk.ConvertJob(img, Path.Combine(R, "disk.img.gz")));
        using (var gz = DiskImage.OpenImage(Path.Combine(R, "disk.img.gz"), out long gl, out var cl))
        {
            var ms = new MemoryStream(); gz.CopyTo(ms); cl();
            check(gl == data.Length && ms.ToArray().AsSpan().SequenceEqual(data), ".img.gz: tamaño leído del .sha256 y datos idénticos");
        }

        Console.WriteLine("== Imágenes incrementales");
        const int B = IncrementalImage.Block;
        long size = 6L * B + 4096;
        var disk0 = new byte[size]; new Random(1).NextBytes(disk0);
        var h0 = IncrementalImage.HashStream(new MemoryStream(disk0), size);
        var disk1 = (byte[])disk0.Clone();
        disk1[2 * B + 10] ^= 0xFF; disk1[size - 1] ^= 0xFF;            // cambia el bloque 2 y el último (parcial)
        var inc1 = new MemoryStream();
        var hdr1 = new IncrementalImage.Header { DiskSize = size, Created = DateTime.Now, Parent = "base.img" };
        var h1 = IncrementalImage.Write(new MemoryStream(disk1), size, h0, inc1, hdr1);
        check(hdr1.ChangedBlocks == 2, "solo se guardan los 2 bloques cambiados");
        inc1.Position = 0;
        check(IncrementalImage.ReadHeader(inc1).Parent == "base.img", "la cabecera recuerda la imagen anterior");
        var disk2 = (byte[])disk1.Clone();
        disk2[0] ^= 0x55; disk2[2 * B + 20] ^= 0x11;
        var inc2 = new MemoryStream();
        IncrementalImage.Write(new MemoryStream(disk2), size, h1, inc2, new IncrementalImage.Header { DiskSize = size, Parent = "inc1" });
        File.WriteAllBytes(Path.Combine(R, "base.img"), disk0);
        File.WriteAllBytes(Path.Combine(R, "inc1.dssinc"), inc1.ToArray());
        var inc2File = inc2.ToArray();
        // en el archivo la referencia al padre es por nombre: reescribir la cabecera con el nombre real
        var fixedHdr = new MemoryStream(); IncrementalImage.Write(new MemoryStream(disk2), size, h1, fixedHdr, new IncrementalImage.Header { DiskSize = size, Parent = "inc1.dssinc" });
        File.WriteAllBytes(Path.Combine(R, "inc2.dssinc"), fixedHdr.ToArray());
        check(IncrementalImage.Chain(Path.Combine(R, "inc2.dssinc")).Count == 3, "cadena: completa + 2 incrementales");
        using (var chain = DiskImage.OpenImage(Path.Combine(R, "inc2.dssinc"), out long cl2, out var cleanup))
        {
            var ms = new MemoryStream(); chain.CopyTo(ms); cleanup();
            check(cl2 == size && ms.ToArray().AsSpan().SequenceEqual(disk2), "restaurar la cadena reproduce el disco exacto de la última fecha");
        }
        using (var chain1 = DiskImage.OpenImage(Path.Combine(R, "inc1.dssinc"), out _, out var c1))
        {
            var ms = new MemoryStream(); chain1.CopyTo(ms); c1();
            check(ms.ToArray().AsSpan().SequenceEqual(disk1), "restaurar una fecha intermedia también es exacto");
        }

        Console.WriteLine("== Copias por versiones");
        string src = Path.Combine(R, "docs"), vroot = Path.Combine(R, "versiones");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "uno");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), new string('b', 100_000));
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);
        var job = new TransferJob { Work = _ => Task.CompletedTask };
        string v1 = VersionedBackup.Run(new JobContext(job), src, vroot, null, 3, true, t0);
        File.WriteAllText(Path.Combine(src, "a.txt"), "dos");
        File.SetLastWriteTime(Path.Combine(src, "a.txt"), DateTime.Now.AddMinutes(1));
        string v2 = VersionedBackup.Run(new JobContext(new TransferJob { Work = _ => Task.CompletedTask }), src, vroot, null, 3, false, t0.AddHours(1));
        check(File.ReadAllText(Path.Combine(v1, "a.txt")) == "uno" && File.ReadAllText(Path.Combine(v2, "a.txt")) == "dos", "cada versión conserva su contenido");
        check(File.ReadAllText(Path.Combine(v2, "sub", "b.txt")).Length == 100_000, "los archivos sin cambios están en la versión nueva");
        bool linked = HardLinks(Path.Combine(v2, "sub", "b.txt")) >= 2;
        check(linked, "los archivos sin cambios se enlazan en vez de copiarse (NTFS)");
        for (int i = 2; i <= 4; i++) VersionedBackup.Run(new JobContext(new TransferJob { Work = _ => Task.CompletedTask }), src, vroot, null, 3, false, t0.AddHours(i));
        var list = VersionedBackup.List(vroot);
        check(list.Count == 3 && list[0].Date == t0.AddHours(4), "se conservan solo las 3 más recientes");
        check(File.ReadAllText(Path.Combine(list[^1].Path, "sub", "b.txt")).Length == 100_000, "borrar versiones antiguas no afecta a las enlazadas");

        Console.WriteLine("== Programación de tareas");
        var mon = new DateTime(2026, 11, 9, 22, 0, 0);   // lunes 22:00
        check(Schedule.Next(ScheduleKind.Daily, 0, new TimeSpan(21, 0, 0), null, mon) == new DateTime(2026, 11, 10, 21, 0, 0), "diaria a las 21:00 → mañana");
        check(Schedule.Next(ScheduleKind.Daily, 0, new TimeSpan(23, 0, 0), null, mon) == new DateTime(2026, 11, 9, 23, 0, 0), "diaria a las 23:00 → hoy");
        check(Schedule.Next(ScheduleKind.Weekly, 0, new TimeSpan(8, 0, 0), [DayOfWeek.Friday], mon) == new DateTime(2026, 11, 13, 8, 0, 0), "semanal el viernes");
        check(Schedule.Next(ScheduleKind.EveryHours, 6, default, null, mon) == mon.AddHours(6), "cada 6 horas");
        check(Schedule.Next(ScheduleKind.Weekly, 0, default, [], mon) == null, "semanal sin días → sin próxima ejecución");

        Console.WriteLine("== Comprobación de capacidad (carpeta temporal)");
        var cr = CapacityCheck.Run(Path.Combine(R, "cap"), 8L << 20, true, new JobContext(new TransferJob { Work = _ => Task.CompletedTask }));
        check(cr.Passed && cr.Tested == 8L << 20, $"8 MB escritos y verificados sin caché ({Fmt.Speed(cr.ReadSpeed)} de lectura)");
        check(!Directory.Exists(Path.Combine(R, "cap")), "los archivos de prueba se borran al terminar");
        var blk = new byte[CapacityCheck.Block]; var blk2 = new byte[CapacityCheck.Block];
        CapacityCheck.Fill(blk, 5); CapacityCheck.Fill(blk2, 6);
        check(!blk.AsSpan().SequenceEqual(blk2) && BitConverter.ToInt64(blk, 0) == 5, "cada bloque tiene un patrón único con su posición");

        Console.WriteLine("== Actualizaciones");
        string json = """[{"tag_name":"v0.3.0","html_url":"u3","prerelease":true,"draft":false},{"tag_name":"v0.2.1","html_url":"u21","prerelease":false,"draft":false},{"tag_name":"v9.0.0","html_url":"d","draft":true,"prerelease":false}]""";
        check(Updates.Pick(json, new Version(0, 2, 0))?.Tag == "v0.3.0", "0.2.0 → ofrece la 0.3.0 (pre-release permitida en 0.x)");
        check(Updates.Pick(json, new Version(1, 0, 0)) == null, "1.0.0 no ofrece pre-releases ni borradores");
        check(Updates.Pick(json, new Version(0, 3, 0)) == null, "sin novedades si ya tienes la última");

        Console.WriteLine("== Traducción");
        L.Load("""{"Discos":"Disks","Se borrará {0} ({1})":"{0} will be erased ({1})","Disco {0}":"Disk {0}","libres":"free","Formateando {0} como {1}…":"Formatting {0} as {1}…"}""");
        check(L.T("Discos") == "Disks", "texto exacto");
        check(L.T("Se borrará Disco 2 (4 GB)") == "Disk 2 will be erased (4 GB)", "plantilla con valores (y traducción anidada)");
        check(L.T("Formateando E: como NTFS…") == "Formatting E: as NTFS…", "plantilla con puntos suspensivos");
        check(L.T("Disco 3 · 12 GB libres") == "Disk 3 · 12 GB libres", "segmentos separados por « · »");
        check(L.T("Texto sin traducir") == "Texto sin traducir", "lo desconocido se deja igual");
        check(L.T("Disco Libre") == "Disco Libre", "no traduce nombres puestos por el usuario («Disco Libre»)");
        L.Disable();
        check(L.T("Discos") == "Discos", "en español no se traduce");

        Console.WriteLine("== ¿Qué usa la unidad?");
        string lockFile = Path.Combine(R, "abierto.bin");
        using (var open = new FileStream(lockFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var sw = Stopwatch.StartNew();
            var found = LockFinder.Find(Path.GetPathRoot(R)!.TrimEnd('\\'));
            check(found.Any(l => l.IsSelf && l.Files.Any(f => f.EndsWith(@"dss-tests-02\abierto.bin", StringComparison.OrdinalIgnoreCase))), $"encuentra el archivo abierto por este proceso ({found.Count} procesos, {sw.Elapsed.TotalSeconds:0.0} s, {LockFinder.Stats})");
        }
        try { Directory.Delete(R, true); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION i);
    struct BY_HANDLE_FILE_INFORMATION { public uint Attr; public long C, A, W; public uint Vol, SizeHigh, SizeLow, Links, IdxHigh, IdxLow; }
    static uint HardLinks(string path)
    {
        using var h = File.OpenHandle(path);
        return GetFileInformationByHandle(h, out var i) ? i.Links : 0;
    }
}
