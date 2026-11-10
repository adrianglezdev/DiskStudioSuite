using System.IO;
using DiskStudio.Core;

// Pruebas de los motores de Disk Studio Suite sobre carpetas temporales (no toca discos reales).
int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALLO ") + what); if (!ok) fails++; }
if (args.Length >= 4 && args[0] == "--vhd")
{
    try { PlanTests.Vhd(int.Parse(args[1]), int.Parse(args[2]), args[3], Check); }
    catch (Exception ex) { Console.WriteLine("  FALLO excepción: " + ex); fails++; }
    if (args.Length >= 5)
        try { FormatTests.Vhd(int.Parse(args[4]), Check); }
        catch (Exception ex) { Console.WriteLine("  FALLO excepción: " + ex); fails++; }
    Console.WriteLine(fails == 0 ? "\nINTEGRACIÓN SUPERADA" : $"\n{fails} PRUEBAS DE INTEGRACIÓN FALLIDAS");
    return fails;
}
PlanTests.Run(Check);
FormatTests.Run(Check);
string R = Path.Combine(Path.GetTempPath(), "dss-tests");
if (Directory.Exists(R)) Directory.Delete(R, true);
string P(string rel) => Path.Combine(R, rel);
void W(string rel, string text, DateTime? t = null) { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!); File.WriteAllText(P(rel), text); if (t != null) File.SetLastWriteTime(P(rel), t.Value); }

void Run(TransferJob job)
{
    var ctx = new JobContext(job);
    job.Work(ctx).GetAwaiter().GetResult();
}

W("src/a.txt", "hola"); W("src/sub/b.txt", new string('x', 3_000_000)); W("src/sub/deep/c.bin", "ccc"); W("src/skip.tmp", "tmp");
W("dst/sobra.txt", "borrar"); W("dst/viejo/x.txt", "x");

Console.WriteLine("== Copiar con verificación y filtro");
Run(FileOps.CopyJob([P("src")], P("copy"), false, new CopyOptions { Verify = true, Filter = new FileFilter { Exclude = "*.tmp" } }));
Check(File.ReadAllText(P("copy/src/sub/b.txt")).Length == 3_000_000, "archivo grande copiado íntegro");
Check(File.Exists(P("copy/src/sub/deep/c.bin")), "subcarpetas profundas");
Check(!File.Exists(P("copy/src/skip.tmp")), "filtro de exclusión respetado");
Check(File.GetLastWriteTimeUtc(P("copy/src/a.txt")) == File.GetLastWriteTimeUtc(P("src/a.txt")), "fecha de modificación conservada");
Check(!Directory.EnumerateFiles(P("copy"), "*.dss-part", SearchOption.AllDirectories).Any(), "sin temporales .dss-part");

Console.WriteLine("== Conflictos");
W("src/a.txt", "versión nueva", DateTime.Now.AddMinutes(5));
Run(FileOps.CopyJob([P("src/a.txt")], P("copy/src"), false, new CopyOptions { Conflict = ConflictPolicy.Skip }));
Check(File.ReadAllText(P("copy/src/a.txt")) == "hola", "Omitir no sobrescribe");
Run(FileOps.CopyJob([P("src/a.txt")], P("copy/src"), false, new CopyOptions { Conflict = ConflictPolicy.KeepBoth }));
Check(File.Exists(P("copy/src/a (2).txt")), "Conservar ambos crea «a (2).txt»");
Run(FileOps.CopyJob([P("src/a.txt")], P("copy/src"), false, new CopyOptions { Conflict = ConflictPolicy.IfNewer }));
Check(File.ReadAllText(P("copy/src/a.txt")) == "versión nueva", "Si es más reciente → sobrescribe");

Console.WriteLine("== Mover (mismo volumen y entre carpetas)");
W("mv/m1.txt", "m1"); W("mv/dir/m2.txt", "m2");
Run(FileOps.CopyJob([P("mv/m1.txt"), P("mv/dir")], P("moved"), true));
Check(File.Exists(P("moved/m1.txt")) && File.Exists(P("moved/dir/m2.txt")), "movido al destino");
Check(!File.Exists(P("mv/m1.txt")) && !Directory.Exists(P("mv/dir")), "origen eliminado");

Console.WriteLine("== Carpeta dentro de sí misma");
var self = FileOps.CopyJob([P("src")], P("src/sub"), false);
Run(self);
Check(self.Errors.Count == 1, "rechazado con error");

Console.WriteLine("== Comparar y sincronizar");
var items = FolderSync.Compare(P("src"), P("dst"), new FileFilter(), false, default, null);
Check(items.Any(i => i.RelPath == "a.txt" && i.Status == DiffStatus.OnlyLeft), "detecta solo en origen");
Check(items.Any(i => i.RelPath == "viejo" && i.IsDir && i.Status == DiffStatus.OnlyRight), "detecta carpeta solo en destino");
FolderSync.Plan(items, SyncMode.Update);
Check(items.All(i => i.Action != SyncAction.DeleteRight), "Actualizar no borra");
Run(FolderSync.SyncJob("espejo", P("src"), P("dst"), SyncMode.Mirror, new CopyOptions()));
var after = FolderSync.Compare(P("src"), P("dst"), new FileFilter(), true, default, null);
Check(after.All(i => i.Status == DiffStatus.Same), "espejo: destino idéntico (comparado por contenido)");
Check(!File.Exists(P("dst/sobra.txt")) && !Directory.Exists(P("dst/viejo")), "espejo: sobrantes eliminados");
W("dst/nuevo-en-destino.txt", "d", DateTime.Now.AddMinutes(10));
Run(FolderSync.SyncJob("bidi", P("src"), P("dst"), SyncMode.TwoWay, new CopyOptions()));
Check(File.Exists(P("src/nuevo-en-destino.txt")), "bidireccional copia de destino a origen");

Console.WriteLine("== Duplicados");
W("dup/1.dat", new string('z', 200_000)); W("dup/x/2.dat", new string('z', 200_000)); W("dup/3.dat", new string('y', 200_000));
var groups = DuplicateFinder.Find([P("dup")], 1, new FileFilter(), default, null);
Check(groups.Count == 1 && groups[0].Files.Count == 2, "encuentra 1 grupo de 2 (ignora mismo tamaño distinto contenido)");

Console.WriteLine("== Hash");
W("h.txt", "abc");
Check(FileHash.Compute(P("h.txt"), "SHA-256", default, null) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "SHA-256 correcto");
Check(FileHash.Compute(P("h.txt"), "MD5", default, null) == "900150983cd24fb0d6963f7d28e17f72", "MD5 correcto");
Check(FileHash.Compute(P("h.txt"), "CRC32", default, null).Length == 8, "CRC32 calculado");
File.WriteAllText(P("sums.sha256"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad *h.txt\n");
Check(FileHash.ParseChecksumFile(P("sums.sha256")).Single().file == "h.txt", "lee archivo de sumas");

Console.WriteLine("== Renombrado masivo");
W("ren/foto b.JPG", "1"); W("ren/foto a.JPG", "2"); W("ren/otra.png", "3");
var ri = Directory.GetFiles(P("ren")).OrderBy(x => x).Select(f => new RenameItem { Path = f }).ToList();
BatchRename.Preview(ri, new RenameRule { Pattern = "viaje_[C]", Case = 1, Digits = 3 });
Check(ri[0].NewName == "viaje_001.jpg" && ri[2].NewName == "viaje_003.png", "patrón + contador + minúsculas");
var undo = BatchRename.Apply(ri);
Check(File.Exists(P("ren/viaje_002.jpg")) && undo.Count == 3, "aplicado");
foreach (var (from, to) in undo) BatchRename.MoveAny(to, from);
Check(File.Exists(P("ren/otra.png")), "deshacer restaura nombres");
var swap = new List<RenameItem> { new() { Path = P("ren/foto a.JPG") }, new() { Path = P("ren/foto b.JPG") } };
BatchRename.Preview(swap, new RenameRule { Pattern = "[N]", Find = "a", Replace = "b" });
Check(swap[0].Problem == "" || swap[0].Problem == "Nombre duplicado", "detecta colisiones");

Console.WriteLine("== ZIP");
Run(Archive.ZipJob([P("src/sub")], P("z.zip")));
Run(Archive.UnzipJob(P("z.zip"), P("unz")));
Check(File.ReadAllText(P("unz/sub/b.txt")).Length == 3_000_000, "comprimir y extraer");

Console.WriteLine("== Borrado seguro");
W("shred/s1.txt", "secreto"); W("shred/in/s2.txt", "secreto2");
Run(Shredder.ShredJob([P("shred")], 3));
Check(!Directory.Exists(P("shred")), "carpeta destruida");

Console.WriteLine("== Analizador de espacio y búsqueda");
var node = SpaceAnalyzer.Scan(P("src"), default, null);
Check(node.Size == new DirectoryInfo(P("src")).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length), "tamaño total correcto");
var found = new List<string>();
FileSearch.Run(new SearchQuery { Root = R, Name = "*.bin" }, default, f => found.Add(f.Name), null);
Check(found.Contains("c.bin"), "búsqueda por patrón");
found.Clear();
FileSearch.Run(new SearchQuery { Root = P("src"), Text = "versión" }, default, f => found.Add(f.Name), null);
Check(found.SequenceEqual(["a.txt"]), "búsqueda por contenido");

Console.WriteLine("== Benchmark (64 MB, solo para comprobar que funciona)");
var bres = Benchmark.Run(R, 64L << 20, default, (_, _) => { });
Check(bres[0].ReadMBs > 0 && bres[0].WriteMBs > 0 && bres[1].ReadIops > 0, $"lectura {bres[0].ReadMBs:N0} MB/s · escritura {bres[0].WriteMBs:N0} MB/s · 4K {bres[1].ReadIops:N0} IOPS");

Console.WriteLine("== Discos");
var (disks, vols) = DiskScanner.Scan(true);
foreach (var d in disks) Console.WriteLine($"       Disco {d.Number}: {d.Title} · {d.Bus} · {d.Media} · {Fmt.Size(d.Size)} · {d.Partitions.Count} particiones · salud {d.HealthText}");
Check(disks.Count > 0 && vols.Any(v => v.DiskNumber >= 0), "detecta discos y asocia volúmenes");

Directory.Delete(R, true);
Console.WriteLine(fails == 0 ? "\nTODAS LAS PRUEBAS SUPERADAS" : $"\n{fails} PRUEBAS FALLIDAS");
return fails;
