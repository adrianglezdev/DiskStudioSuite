using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskStudio.Core;

// ───────────────────────── Analizador de espacio ─────────────────────────
public class SpaceNode
{
    public string Name = "", Path = "";
    public long Size;
    public int Files, Folders;
    public bool IsDir;
    public SpaceNode Parent;
    public List<SpaceNode> Children = [];
    public DateTime Modified;
    public override string ToString() => Name;
}

public static class SpaceAnalyzer
{
    public static SpaceNode Scan(string root, CancellationToken tk, Action<string, long> progress)
    {
        var node = new SpaceNode { Name = root, Path = root, IsDir = true };
        long counter = 0;
        Walk(node, tk, progress, ref counter);
        return node;
    }

    static void Walk(SpaceNode n, CancellationToken tk, Action<string, long> progress, ref long counter)
    {
        tk.ThrowIfCancellationRequested();
        DirectoryInfo di = new(n.Path);
        try
        {
            foreach (var e in di.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (e is FileInfo f)
                {
                    n.Children.Add(new SpaceNode { Name = f.Name, Path = f.FullName, Size = f.Length, Parent = n, Modified = f.LastWriteTime });
                    n.Size += f.Length; n.Files++;
                }
                else if (e is DirectoryInfo d)
                {
                    var c = new SpaceNode { Name = d.Name, Path = d.FullName, IsDir = true, Parent = n, Modified = d.LastWriteTime };
                    Walk(c, tk, progress, ref counter);
                    n.Children.Add(c);
                    n.Size += c.Size; n.Files += c.Files; n.Folders += c.Folders + 1;
                }
                if (++counter % 2000 == 0) progress?.Invoke(n.Path, counter);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    public static IEnumerable<SpaceNode> AllFiles(SpaceNode n)
    {
        foreach (var c in n.Children)
        {
            if (c.IsDir) foreach (var x in AllFiles(c)) yield return x;
            else yield return c;
        }
    }

    public static List<(string ext, long size, int count)> ByExtension(SpaceNode root) =>
        AllFiles(root).GroupBy(f => System.IO.Path.GetExtension(f.Name).ToLowerInvariant())
            .Select(g => (g.Key == "" ? "(sin extensión)" : g.Key, g.Sum(x => x.Size), g.Count()))
            .OrderByDescending(x => x.Item2).ToList();
}

// ───────────────────────── Duplicados ─────────────────────────
public class DupGroup
{
    public long Size { get; init; }
    public List<DupFile> Files { get; init; } = [];
    public long Wasted => Size * (Files.Count - 1);
}

public class DupFile : Notifier
{
    public string Path { get; init; } = "";
    public DateTime Modified { get; init; }
    public DupGroup Group { get; set; }
    bool marked; public bool Marked { get => marked; set => Set(ref marked, value); }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path);
    public string SizeText => Fmt.Size(Group?.Size ?? 0);
    public string ModifiedText => Modified.ToString("dd/MM/yyyy HH:mm");
}

public static class DuplicateFinder
{
    public static List<DupGroup> Find(IEnumerable<string> roots, long minSize, FileFilter filter, CancellationToken tk, Action<string> progress)
    {
        filter.Prepare();
        var bySize = new Dictionary<long, List<FileInfo>>();
        int count = 0;
        foreach (var root in roots)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                tk.ThrowIfCancellationRequested();
                var d = stack.Pop();
                try
                {
                    foreach (var f in d.GetFiles())
                    {
                        if (f.Length < minSize || filter.SkipFile(f)) continue;
                        if (!bySize.TryGetValue(f.Length, out var l)) bySize[f.Length] = l = [];
                        l.Add(f);
                        if (++count % 500 == 0) progress?.Invoke($"Indexando… {count} archivos");
                    }
                    foreach (var s in d.GetDirectories()) if (!filter.SkipDir(s)) stack.Push(s);
                }
                catch { }
            }
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var groups = new ConcurrentBag<DupGroup>();
        int done = 0;
        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = tk, MaxDegreeOfParallelism = 4 }, kv =>
        {
            // 1º: hash de los primeros 64 KB; 2º: hash completo
            foreach (var partial in kv.Value.GroupBy(f => Hex(PartialHash(f.FullName))).Where(g => g.Count() > 1))
                foreach (var full in partial.GroupBy(f => kv.Key <= 65536 ? partial.Key : Hex(Hashing.XxHash(f.FullName, tk))).Where(g => g.Count() > 1))
                {
                    var g = new DupGroup { Size = kv.Key };
                    foreach (var f in full.OrderBy(f => f.LastWriteTime)) g.Files.Add(new DupFile { Path = f.FullName, Modified = f.LastWriteTime, Group = g });
                    groups.Add(g);
                }
            int n = Interlocked.Increment(ref done);
            if (n % 20 == 0) progress?.Invoke($"Comparando contenido… {n}/{candidates.Count} grupos");
        });
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    static byte[] PartialHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[65536];
            int n = fs.Read(buf, 0, buf.Length);
            return XxHash128.Hash(buf.AsSpan(0, n));
        }
        catch { return Guid.NewGuid().ToByteArray(); }
    }

    static string Hex(byte[] b) => Convert.ToHexString(b);
}

// ───────────────────────── Hash ─────────────────────────
public static class FileHash
{
    public static readonly string[] Algorithms = ["SHA-256", "SHA-1", "MD5", "SHA-512", "CRC32", "XXH128"];

    public static string Compute(string file, string alg, CancellationToken tk, Action<long> progress)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        var buf = new byte[4 * 1048576];
        NonCryptographicHashAlgorithm nc = alg switch { "CRC32" => new Crc32(), "XXH128" => new XxHash128(), _ => null };
        using HashAlgorithm h = alg switch { "SHA-1" => SHA1.Create(), "MD5" => MD5.Create(), "SHA-512" => SHA512.Create(), "SHA-256" => SHA256.Create(), _ => null };
        int n; long total = 0;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            tk.ThrowIfCancellationRequested();
            if (nc != null) nc.Append(buf.AsSpan(0, n)); else h.TransformBlock(buf, 0, n, null, 0);
            total += n; progress?.Invoke(total);
        }
        if (nc != null) return Convert.ToHexString(nc.GetCurrentHash()).ToLowerInvariant();
        h.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(h.Hash!).ToLowerInvariant();
    }

    /// <summary>Lee un archivo .sha256/.md5/.sfv: devuelve (hash, nombre).</summary>
    public static List<(string hash, string file)> ParseChecksumFile(string path)
    {
        var r = new List<(string, string)>();
        foreach (var line in File.ReadAllLines(path))
        {
            var m = Regex.Match(line, @"^([0-9a-fA-F]{8,128})\s+\*?(.+?)\s*$");
            if (m.Success) { r.Add((m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value)); continue; }
            m = Regex.Match(line, @"^(.+?)\s+([0-9a-fA-F]{8})$"); // SFV
            if (m.Success && !line.StartsWith(';')) r.Add((m.Groups[2].Value.ToLowerInvariant(), m.Groups[1].Value));
        }
        return r;
    }

    public static string GuessAlgorithm(string hash) => hash.Length switch { 8 => "CRC32", 32 => "MD5", 40 => "SHA-1", 64 => "SHA-256", 128 => "SHA-512", _ => "SHA-256" };
}

// ───────────────────────── Renombrado masivo ─────────────────────────
public class RenameItem : Notifier
{
    public string Path { get; init; } = "";
    public string OldName => System.IO.Path.GetFileName(Path);
    string newName = ""; public string NewName { get => newName; set { Set(ref newName, value); Raise(nameof(Changed)); } }
    string problem = ""; public string Problem { get => problem; set => Set(ref problem, value); }
    public bool Changed => newName != OldName;
}

public class RenameRule
{
    public string Pattern = "[N]";       // [N] nombre, [E] extensión, [C] contador, [D] fecha, [P] carpeta, [S] tamaño
    public string Find = "", Replace = "";
    public bool Regex;
    public int Case;                     // 0 igual, 1 minúsculas, 2 MAYÚSCULAS, 3 Título
    public int Start = 1, Step = 1, Digits = 2;
    public bool KeepExtension = true;
}

public static class BatchRename
{
    public static void Preview(IList<RenameItem> items, RenameRule r)
    {
        int counter = r.Start;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(it.Path);
            string ext = System.IO.Path.GetExtension(it.Path);
            bool isDir = Directory.Exists(it.Path);
            if (isDir) { name = System.IO.Path.GetFileName(it.Path); ext = ""; }
            DateTime dt = File.GetLastWriteTime(it.Path);
            string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(it.Path)) ?? "";

            string s = r.Pattern;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[C(?::(\d+))?\]", m => counter.ToString(new string('0', m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : r.Digits)));
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[D(?::([^\]]+))?\]", m => dt.ToString(m.Groups[1].Success ? m.Groups[1].Value : "yyyy-MM-dd"));
            s = s.Replace("[N]", name).Replace("[P]", parent).Replace("[E]", ext.TrimStart('.'));
            if (!string.IsNullOrEmpty(r.Find))
            {
                try { s = r.Regex ? System.Text.RegularExpressions.Regex.Replace(s, r.Find, r.Replace ?? "") : s.Replace(r.Find, r.Replace ?? "", StringComparison.OrdinalIgnoreCase); }
                catch { }
            }
            s = r.Case switch
            {
                1 => s.ToLowerInvariant(), 2 => s.ToUpperInvariant(),
                3 => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()), _ => s
            };
            if (r.KeepExtension && !r.Pattern.Contains("[E]")) s += r.Case == 1 ? ext.ToLowerInvariant() : r.Case == 2 ? ext.ToUpperInvariant() : ext;
            it.NewName = s;
            it.Problem = s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? "Caracteres no válidos"
                       : s.Length == 0 ? "Nombre vacío"
                       : !names.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(it.Path)!, s)) ? "Nombre duplicado" : "";
            counter += r.Step;
        }
    }

    /// <summary>Aplica en dos fases (nombre temporal) para permitir intercambios. Devuelve la lista para deshacer.</summary>
    public static List<(string from, string to)> Apply(IList<RenameItem> items)
    {
        var done = new List<(string, string)>();
        var temp = new List<(string tmp, string final, string orig)>();
        foreach (var it in items.Where(i => i.Changed && i.Problem == ""))
        {
            string tmp = it.Path + ".~cr" + Guid.NewGuid().ToString("N")[..6];
            MoveAny(it.Path, tmp);
            temp.Add((tmp, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(it.Path)!, it.NewName), it.Path));
        }
        foreach (var (tmp, final, orig) in temp)
        {
            try { MoveAny(tmp, final); done.Add((orig, final)); }
            catch (Exception ex) { MoveAny(tmp, orig); Log.Error($"No se pudo renombrar {orig}: {ex.Message}"); }
        }
        return done;
    }

    public static void MoveAny(string a, string b) { if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b); }
}

// ───────────────────────── Borrado seguro ─────────────────────────
public static class Shredder
{
    public static TransferJob ShredJob(IList<string> paths, int passes) => new()
    {
        Title = $"Borrado seguro de {(paths.Count == 1 ? Path.GetFileName(paths[0]) : paths.Count + " elementos")}",
        Subtitle = $"{passes} pasada(s) · irrecuperable",
        Glyph = Icons.Wipe,
        Work = ctx => Task.Run(() =>
        {
            var files = new List<string>(); var dirs = new List<string>();
            foreach (var p in paths)
            {
                if (File.Exists(p)) files.Add(p);
                else if (Directory.Exists(p))
                {
                    files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories));
                    dirs.AddRange(Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length));
                    dirs.Add(p);
                }
            }
            ctx.SetTotal(files.Sum(f => new FileInfo(f).Length) * passes, files.Count);
            var buf = new byte[1048576];
            foreach (var f in files)
            {
                ctx.Check();
                ctx.SetCurrent(f);
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    long len = new FileInfo(f).Length;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
                        for (int pass = 0; pass < passes; pass++)
                        {
                            fs.Position = 0;
                            for (long done = 0; done < len;)
                            {
                                ctx.Check();
                                if (pass == passes - 1 && passes > 1) RandomNumberGenerator.Fill(buf); else Array.Fill(buf, (byte)(pass % 2 == 0 ? 0x00 : 0xFF));
                                if (passes == 1) RandomNumberGenerator.Fill(buf);
                                int n = (int)Math.Min(buf.Length, len - done);
                                fs.Write(buf, 0, n); done += n; ctx.AddBytes(n);
                            }
                            fs.Flush(true);
                        }
                    string rnd = Path.Combine(Path.GetDirectoryName(f)!, Guid.NewGuid().ToString("N")[..12]);
                    File.Move(f, rnd);
                    File.SetCreationTime(rnd, new DateTime(2000, 1, 1)); File.SetLastWriteTime(rnd, new DateTime(2000, 1, 1));
                    File.Delete(rnd);
                }
                catch (Exception ex) { ctx.Fail(f, ex.Message); }
                ctx.FileDone();
            }
            foreach (var d in dirs) try { Directory.Delete(d, false); } catch { }
        })
    };

    public static TransferJob WipeFreeSpaceJob(string root) => new()
    {
        Title = $"Limpiar espacio libre de {root}",
        Subtitle = "Sobrescribe el espacio libre para impedir recuperar archivos borrados",
        Glyph = Icons.Wipe,
        Work = ctx => Task.Run(() =>
        {
            string dir = Path.Combine(root, "DiskStudioWipe_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                var di = new DriveInfo(root);
                long free = di.AvailableFreeSpace;
                ctx.SetTotal(free);
                var buf = new byte[4 * 1048576];
                RandomNumberGenerator.Fill(buf);
                int idx = 0;
                while (true)
                {
                    ctx.Check();
                    string f = Path.Combine(dir, $"w{idx++:D5}.tmp");
                    try
                    {
                        using var fs = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
                        for (int i = 0; i < 256; i++) // archivos de 1 GB
                        {
                            ctx.Check();
                            fs.Write(buf, 0, buf.Length); ctx.AddBytes(buf.Length);
                            ctx.SetCurrent($"Escritos {Fmt.Size(ctx.BytesDone)} de {Fmt.Size(free)}");
                        }
                    }
                    catch (IOException) { break; } // disco lleno
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        })
    };
}

// ───────────────────────── Benchmark ─────────────────────────
public class BenchResult
{
    public string Test { get; init; } = "";
    public double ReadMBs { get; set; }
    public double WriteMBs { get; set; }
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
}

public static unsafe class Benchmark
{
    public static List<BenchResult> Run(string root, long fileSize, CancellationToken tk, Action<string, double> progress)
    {
        string file = Path.Combine(root, "DiskStudioBench.tmp");
        const int seqBlock = 1048576, rndBlock = 4096;
        const FileOptions NoBuffering = (FileOptions)0x20000000;
        var results = new List<BenchResult>();
        byte* buf = (byte*)NativeMemory.AlignedAlloc(seqBlock, 4096);
        try
        {
            new Span<byte>(buf, seqBlock).Fill(0);
            RandomNumberGenerator.Fill(new Span<byte>(buf, seqBlock));
            var seq = new BenchResult { Test = "Secuencial 1 MiB" };
            var rnd = new BenchResult { Test = "Aleatorio 4 KiB" };
            results.Add(seq); results.Add(rnd);

            // Escritura secuencial
            using (var h = File.OpenHandle(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, NoBuffering | FileOptions.WriteThrough, fileSize))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (long off = 0; off < fileSize; off += seqBlock)
                {
                    tk.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, new ReadOnlySpan<byte>(buf, seqBlock), off);
                    progress("Escritura secuencial", 0.25 * off / fileSize);
                }
                seq.WriteMBs = fileSize / 1048576.0 / sw.Elapsed.TotalSeconds;
            }
            // Lectura secuencial
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (long off = 0; off < fileSize; off += seqBlock)
                {
                    tk.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, new Span<byte>(buf, seqBlock), off);
                    progress("Lectura secuencial", 0.25 + 0.25 * off / fileSize);
                }
                seq.ReadMBs = fileSize / 1048576.0 / sw.Elapsed.TotalSeconds;
            }
            var rng = new Random(42);
            long blocks = fileSize / rndBlock;
            TimeSpan dur = TimeSpan.FromSeconds(5);
            // Lectura aleatoria 4K
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); long ops = 0;
                while (sw.Elapsed < dur)
                {
                    tk.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, new Span<byte>(buf, rndBlock), rng.NextInt64(blocks) * rndBlock); ops++;
                    if (ops % 256 == 0) progress("Lectura aleatoria 4K", 0.5 + 0.25 * sw.Elapsed / dur);
                }
                rnd.ReadIops = ops / sw.Elapsed.TotalSeconds; rnd.ReadMBs = rnd.ReadIops * rndBlock / 1048576.0;
            }
            // Escritura aleatoria 4K
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, NoBuffering | FileOptions.WriteThrough))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); long ops = 0;
                while (sw.Elapsed < dur)
                {
                    tk.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, new ReadOnlySpan<byte>(buf, rndBlock), rng.NextInt64(blocks) * rndBlock); ops++;
                    if (ops % 256 == 0) progress("Escritura aleatoria 4K", 0.75 + 0.25 * sw.Elapsed / dur);
                }
                rnd.WriteIops = ops / sw.Elapsed.TotalSeconds; rnd.WriteMBs = rnd.WriteIops * rndBlock / 1048576.0;
            }
            progress("Terminado", 1);
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            try { File.Delete(file); } catch { }
        }
        return results;
    }
}

// ───────────────────────── Búsqueda ─────────────────────────
public class SearchQuery
{
    public string Root = "", Name = "*", Text = "";
    public long MinSize = -1, MaxSize = -1;
    public DateTime? After, Before;
    public bool IncludeHidden, Folders;
}

public static class FileSearch
{
    public static void Run(SearchQuery q, CancellationToken tk, Action<FileSystemInfo> found, Action<string> progress)
    {
        var filter = new FileFilter { Include = string.IsNullOrWhiteSpace(q.Name) ? "" : q.Name.Contains('*') || q.Name.Contains('?') || q.Name.Contains(';') ? q.Name : $"*{q.Name}*", IncludeHidden = q.IncludeHidden }.Prepare();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(q.Root));
        var nameRx = new FileFilter { Include = filter.Include }.Prepare();
        while (stack.Count > 0)
        {
            tk.ThrowIfCancellationRequested();
            var d = stack.Pop();
            progress?.Invoke(d.FullName);
            FileInfo[] files; DirectoryInfo[] dirs;
            try { files = d.GetFiles(); dirs = d.GetDirectories(); } catch { continue; }
            foreach (var sub in dirs)
            {
                if (new FileFilter { IncludeHidden = q.IncludeHidden }.Prepare().SkipDir(sub)) continue;
                stack.Push(sub);
                if (q.Folders && string.IsNullOrEmpty(q.Text) && !nameRx.SkipFile(new FileInfo(sub.FullName))) found(sub);
            }
            foreach (var f in files)
            {
                if (filter.SkipFile(f)) continue;
                if (q.MinSize >= 0 && f.Length < q.MinSize) continue;
                if (q.MaxSize >= 0 && f.Length > q.MaxSize) continue;
                if (q.After is DateTime a && f.LastWriteTime < a) continue;
                if (q.Before is DateTime b && f.LastWriteTime > b) continue;
                if (!string.IsNullOrEmpty(q.Text) && !Contains(f, q.Text, tk)) continue;
                found(f);
            }
        }
    }

    static bool Contains(FileInfo f, string text, CancellationToken tk)
    {
        if (f.Length > 200 * 1048576) return false;
        try
        {
            using var sr = new StreamReader(f.FullName, Encoding.UTF8, true);
            var buf = new char[1 << 16]; string carry = "";
            int n;
            while ((n = sr.Read(buf, 0, buf.Length)) > 0)
            {
                tk.ThrowIfCancellationRequested();
                string chunk = carry + new string(buf, 0, n);
                if (chunk.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
                carry = chunk.Length > text.Length ? chunk[^text.Length..] : chunk;
            }
        }
        catch { }
        return false;
    }
}

// ───────────────────────── ZIP y tamaño de carpetas ─────────────────────────
public static class Archive
{
    public static TransferJob ZipJob(IList<string> paths, string zipFile) => new()
    {
        Title = "Comprimir en " + Path.GetFileName(zipFile),
        Subtitle = $"{paths.Count} elemento(s)",
        Glyph = Icons.Zip,
        Work = ctx => Task.Run(() =>
        {
            var entries = new List<(string file, string name)>();
            foreach (var p in paths)
            {
                if (File.Exists(p)) entries.Add((p, Path.GetFileName(p)));
                else if (Directory.Exists(p))
                {
                    string baseDir = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p;
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        entries.Add((f, Path.GetRelativePath(baseDir, f)));
                }
            }
            ctx.SetTotal(entries.Sum(e => new FileInfo(e.file).Length), entries.Count);
            string tmp = zipFile + ".dss-part";
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var buf = new byte[1048576];
                foreach (var (file, name) in entries)
                {
                    ctx.Check(); ctx.SetCurrent(name);
                    try
                    {
                        var e = zip.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Optimal);
                        e.LastWriteTime = File.GetLastWriteTime(file);
                        using var es = e.Open();
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) { ctx.Check(); es.Write(buf, 0, n); ctx.AddBytes(n); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { ctx.Fail(file, ex.Message); }
                    ctx.FileDone();
                }
            }
            if (File.Exists(zipFile)) File.Delete(zipFile);
            File.Move(tmp, zipFile);
        })
    };

    public static TransferJob UnzipJob(string zipFile, string dest) => new()
    {
        Title = "Extraer " + Path.GetFileName(zipFile),
        Subtitle = "→ " + dest,
        Glyph = Icons.Zip,
        Work = ctx => Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipFile);
            ctx.SetTotal(zip.Entries.Sum(e => e.Length), zip.Entries.Count);
            string full = Path.GetFullPath(dest);
            var buf = new byte[1048576];
            foreach (var e in zip.Entries)
            {
                ctx.Check(); ctx.SetCurrent(e.FullName);
                string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) { ctx.Fail(e.FullName, "Ruta no permitida"); continue; }
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) target = FileOps.UniqueName(target);
                using (var es = e.Open())
                using (var fs = new FileStream(target, FileMode.Create))
                {
                    int n;
                    while ((n = es.Read(buf, 0, buf.Length)) > 0) { ctx.Check(); fs.Write(buf, 0, n); ctx.AddBytes(n); }
                }
                File.SetLastWriteTime(target, e.LastWriteTime.DateTime);
                ctx.FileDone();
            }
        })
    };

    public static (long size, int files, int dirs) FolderSize(string dir, CancellationToken tk)
    {
        long size = 0; int files = 0, dirs = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
        {
            tk.ThrowIfCancellationRequested();
            if (e is FileInfo f) { size += f.Length; files++; } else dirs++;
        }
        return (size, files, dirs);
    }
}
