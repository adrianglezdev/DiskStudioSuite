using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace DiskStudio.Core;

public enum JobState { Queued, Running, Paused, Done, Failed, Cancelled }

public class FailedItem
{
    public string Path { get; init; } = "";
    public string Error { get; init; } = "";
}

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Trabajo de la cola (copia, movimiento, clonado, imagen…).</summary>
public class TransferJob : Notifier
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = Icons.Copy;
    public Func<JobContext, Task> Work { get; init; }
    public Func<List<string>, TransferJob> RetryFactory { get; init; }
    public ObservableCollection<FailedItem> Errors { get; } = [];
    public List<double> SpeedHistory { get; } = [];
    public DateTime Created { get; } = DateTime.Now;

    internal readonly CancellationTokenSource Cts = new();
    internal readonly ManualResetEventSlim Gate = new(true);
    internal long bytesDone, bytesTotal, filesDone, filesTotal;
    internal string current = "";
    internal DateTime started;
    internal double activeSeconds;
    DateTime lastSample;
    long lastBytes;

    JobState state;
    public JobState State { get => state; set { Set(ref state, value); Raise(nameof(StateText)); Raise(nameof(IsActive)); Raise(nameof(IsFinished)); Raise(nameof(CanPause)); Raise(nameof(CanResume)); } }
    public bool IsActive => state is JobState.Running or JobState.Paused or JobState.Queued;
    public bool IsFinished => !IsActive;
    public bool CanPause => state == JobState.Running;
    public bool CanResume => state == JobState.Paused;
    public bool CanRetry => IsFinished && Errors.Count > 0 && RetryFactory != null;

    double progress; public double Progress { get => progress; set => Set(ref progress, value); }
    string detail = ""; public string Detail { get => detail; set => Set(ref detail, value); }
    string stats = ""; public string Stats { get => stats; set => Set(ref stats, value); }
    string speedText = ""; public string SpeedText { get => speedText; set => Set(ref speedText, value); }
    string result = ""; public string Result { get => result; set => Set(ref result, value); }
    bool indeterminate = true; public bool Indeterminate { get => indeterminate; set => Set(ref indeterminate, value); }

    public string StateText => state switch
    {
        JobState.Queued => "En cola", JobState.Running => "En curso", JobState.Paused => "En pausa",
        JobState.Done => Errors.Count > 0 ? $"Terminado con {Errors.Count} errores" : "Completado",
        JobState.Failed => "Error", JobState.Cancelled => "Cancelado", _ => ""
    };

    public void Pause() { if (state == JobState.Running) { Gate.Reset(); State = JobState.Paused; } }
    public void Resume() { if (state == JobState.Paused) { Gate.Set(); State = JobState.Running; } }
    public void Cancel() { Cts.Cancel(); Gate.Set(); if (state == JobState.Queued) State = JobState.Cancelled; }

    internal void NotifyFinished() { Raise(nameof(CanRetry)); Raise(nameof(StateText)); }

    /// <summary>Llamado periódicamente desde el hilo de interfaz.</summary>
    internal void Refresh()
    {
        long done = Interlocked.Read(ref bytesDone), total = Interlocked.Read(ref bytesTotal);
        var now = DateTime.Now;
        if (state == JobState.Running)
        {
            double dt = (now - lastSample).TotalSeconds;
            if (lastSample != default && dt > 0)
            {
                activeSeconds += dt;
                double inst = (done - lastBytes) / dt;
                SpeedHistory.Add(Math.Max(0, inst));
                if (SpeedHistory.Count > 240) SpeedHistory.RemoveAt(0);
            }
            lastSample = now; lastBytes = done;
        }
        else lastSample = default;

        Indeterminate = total <= 0 && state == JobState.Running;
        Progress = total > 0 ? Math.Min(1, (double)done / total) : 0;
        Detail = current;
        double avg = activeSeconds > 0.5 ? done / activeSeconds : 0;
        string eta = avg > 0 && total > done ? " · quedan " + Fmt.Time(TimeSpan.FromSeconds((total - done) / avg)) : "";
        double recent = SpeedHistory.Count > 0 ? SpeedHistory.TakeLast(4).Average() : 0;
        SpeedText = state == JobState.Running && recent > 0 ? Fmt.Speed(recent) + eta : "";
        Stats = total > 0
            ? $"{Fmt.Size(done)} de {Fmt.Size(total)}" + (filesTotal > 0 ? $" · {filesDone}/{filesTotal} archivos" : "")
            : filesTotal > 0 ? $"{filesDone}/{filesTotal}" : "";
        Raise(nameof(SpeedHistory));
    }
}

public class JobContext(TransferJob job)
{
    public TransferJob Job { get; } = job;
    public CancellationToken Token => Job.Cts.Token;
    DateTime throttleStart = DateTime.Now;
    long throttleBytes;

    public void Check()
    {
        Job.Gate.Wait(Token);
        Token.ThrowIfCancellationRequested();
    }

    public void SetTotal(long bytes, long files = 0) { Interlocked.Exchange(ref Job.bytesTotal, bytes); Job.filesTotal = files; }
    public void AddTotal(long bytes) => Interlocked.Add(ref Job.bytesTotal, bytes);
    public void SetCurrent(string s) => Job.current = s;
    public void FileDone() => Interlocked.Increment(ref Job.filesDone);
    public long BytesDone => Interlocked.Read(ref Job.bytesDone);

    public void AddBytes(long n)
    {
        Interlocked.Add(ref Job.bytesDone, n);
        int limit = Settings.Current.SpeedLimitMBs;
        if (limit > 0)
        {
            throttleBytes += n;
            double expected = throttleBytes / (limit * 1048576.0);
            double elapsed = (DateTime.Now - throttleStart).TotalSeconds;
            if (expected > elapsed) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(1, expected - elapsed)));
            if (elapsed > 5) { throttleStart = DateTime.Now; throttleBytes = 0; }
        }
    }

    public void Fail(string path, string error)
    {
        Log.Error($"{path}: {error}");
        var item = new FailedItem { Path = path, Error = error };
        var d = Application.Current?.Dispatcher;
        if (d == null) lock (Job.Errors) Job.Errors.Add(item);
        else d.BeginInvoke(() => Job.Errors.Add(item));
    }
}

/// <summary>Cola de trabajos: se ejecutan de uno en uno.</summary>
public static class TransferQueue
{
    public static ObservableCollection<TransferJob> Jobs { get; } = [];
    public static TransferJob Active { get; private set; }
    public static event Action StateChanged;
    static AfterQueue after = AfterQueue.Nothing;
    public static AfterQueue After { get => after; set { after = value; StateChanged?.Invoke(); } }
    static bool running;
    static readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    static TransferQueue()
    {
        timer.Tick += (_, _) => { foreach (var j in Jobs) if (j.State is JobState.Running or JobState.Paused) j.Refresh(); StateChanged?.Invoke(); };
    }

    public static int PendingCount => Jobs.Count(j => j.IsActive);

    public static void Enqueue(TransferJob job)
    {
        Jobs.Insert(0, job);
        Log.Info($"En cola: {job.Title}");
        if (!running) _ = RunLoop();
        StateChanged?.Invoke();
    }

    public static void ClearFinished()
    {
        foreach (var j in Jobs.Where(j => j.IsFinished).ToList()) Jobs.Remove(j);
        StateChanged?.Invoke();
    }

    static async Task RunLoop()
    {
        running = true;
        timer.Start();
        try
        {
            while (true)
            {
                var job = Jobs.LastOrDefault(j => j.State == JobState.Queued);
                if (job == null) break;
                Active = job;
                job.State = JobState.Running;
                job.started = DateTime.Now;
                StateChanged?.Invoke();
                var ctx = new JobContext(job);
                try
                {
                    await Task.Run(() => job.Work(ctx), job.Cts.Token);
                    job.State = JobState.Done;
                    job.current = "";
                    job.Refresh();
                    job.Progress = 1;
                    job.Result = $"{Fmt.Size(job.bytesDone)} en {Fmt.Time(DateTime.Now - job.started)}";
                    if (job.Errors.Count > 0) { Log.Warn($"{job.Title}: terminado con {job.Errors.Count} errores"); Toast.Show("Terminado con errores", job.Title, Icons.Warning); }
                    else { Log.Ok($"{job.Title}: completado ({job.Result})"); Toast.Show("Completado", job.Title, Icons.Success); }
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Cancelled; job.Detail = ""; job.SpeedText = "";
                    Log.Warn($"{job.Title}: cancelado");
                }
                catch (Exception ex)
                {
                    job.State = JobState.Failed; job.Detail = ex.Message; job.SpeedText = "";
                    job.Result = ex.Message;
                    Log.Error($"{job.Title}: {ex.Message}");
                    Toast.Show("Error", job.Title + "\n" + ex.Message, Icons.Error);
                }
                job.NotifyFinished();
                Active = null;
                StateChanged?.Invoke();
                AppState.Rescan(false);
            }
        }
        finally
        {
            running = false;
            timer.Stop();
            StateChanged?.Invoke();
        }
        if (after != AfterQueue.Nothing)
        {
            var a = after; after = AfterQueue.Nothing;
            Log.Info(a == AfterQueue.Sleep ? "Cola terminada: suspendiendo el equipo." : "Cola terminada: apagando el equipo en 60 s.");
            Native.Shutdown(a == AfterQueue.Sleep);
        }
    }

}

// ───────────────────────── Filtros ─────────────────────────

public class FileFilter
{
    public string Include { get; set; } = "";   // "*.jpg;*.png"  (vacío = todo)
    public string Exclude { get; set; } = "";   // "node_modules;*.tmp"
    public bool IncludeHidden { get; set; } = true;
    Regex inc, exc;

    static readonly string[] Always = ["System Volume Information", "$RECYCLE.BIN", "$Recycle.Bin", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp"];

    static Regex Build(string patterns)
    {
        var parts = patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        return new Regex("^(" + string.Join("|", parts.Select(p => Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", "."))) + ")$",
                         RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public FileFilter Prepare() { inc = Build(Include); exc = Build(Exclude); return this; }

    public bool SkipDir(DirectoryInfo d)
    {
        if (Always.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) return true;
        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        if (!IncludeHidden && (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 && d.Parent != null) return true;
        return exc != null && exc.IsMatch(d.Name);
    }

    public bool SkipFile(FileInfo f)
    {
        if (Always.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) return true;
        if (f.Name.EndsWith(".dss-part", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IncludeHidden && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
        if (exc != null && exc.IsMatch(f.Name)) return true;
        return inc != null && !inc.IsMatch(f.Name);
    }
}

public class CopyOptions
{
    public ConflictPolicy Conflict = Settings.Current.Conflict;
    public bool Verify = Settings.Current.VerifyByDefault;
    public bool PreserveTimes = true;
    public FileFilter Filter = new();
}

// ───────────────────────── Operaciones de archivos ─────────────────────────

public enum OpKind { Copy, CopyTree, Delete, DeleteDir, MkDir }
public record Op(OpKind Kind, string Src, string Dst, long Size = 0);

public enum ConflictAnswer { Overwrite, Skip, KeepBoth, Cancel }

public static class FileOps
{
    /// <summary>Crea un trabajo de copia o movimiento de una selección a una carpeta.</summary>
    public static TransferJob CopyJob(IList<string> sources, string dest, bool move, CopyOptions opt = null)
    {
        opt ??= new CopyOptions();
        string what = sources.Count == 1 ? Path.GetFileName(sources[0].TrimEnd('\\')) : $"{sources.Count} elementos";
        if (string.IsNullOrEmpty(what)) what = sources[0];
        return new TransferJob
        {
            Title = (move ? "Mover " : "Copiar ") + what,
            Subtitle = "→ " + dest,
            Glyph = move ? Icons.Move : Icons.Copy,
            Work = ctx => Task.Run(() => RunCopy(ctx, sources, dest, move, opt)),
            RetryFactory = failed => CopyJob(failed, dest, move, opt)
        };
    }

    static void RunCopy(JobContext ctx, IList<string> sources, string dest, bool move, CopyOptions opt)
    {
        opt.Filter.Prepare();
        var ops = new List<Op>();
        var moveRoots = new List<string>();
        ctx.SetCurrent("Analizando…");
        foreach (string src in sources)
        {
            ctx.Check();
            string name = Path.GetFileName(src.TrimEnd('\\'));
            string target = string.IsNullOrEmpty(name) ? Path.Combine(dest, "Unidad_" + src[0]) : Path.Combine(dest, name);
            if (Directory.Exists(src))
            {
                if (IsInside(dest, src)) { ctx.Fail(src, "No se puede copiar una carpeta dentro de sí misma"); continue; }
                if (move && SameVolume(src, dest) && !Directory.Exists(target))
                {
                    try { Directory.Move(src, target); Log.Info($"Movido {src} → {target}"); continue; }
                    catch { }
                }
                Expand(new DirectoryInfo(src), target, opt.Filter, ops, ctx);
                if (move) moveRoots.Add(src);
            }
            else if (File.Exists(src))
            {
                if (move && SameVolume(src, dest) && !File.Exists(target))
                {
                    try { File.Move(src, target); Log.Info($"Movido {src} → {target}"); continue; }
                    catch { }
                }
                ops.Add(new Op(OpKind.Copy, src, target, new FileInfo(src).Length));
            }
            else ctx.Fail(src, "No existe");
        }
        Execute(ctx, ops, opt, move);
        if (move)
            foreach (var root in moveRoots) RemoveEmptyDirs(root);
    }

    /// <summary>Convierte un árbol de carpetas en operaciones de copia.</summary>
    public static void Expand(DirectoryInfo dir, string target, FileFilter filter, List<Op> ops, JobContext ctx)
    {
        ops.Add(new Op(OpKind.MkDir, dir.FullName, target));
        FileInfo[] files; DirectoryInfo[] dirs;
        try { files = dir.GetFiles(); dirs = dir.GetDirectories(); }
        catch (Exception ex) { ctx?.Fail(dir.FullName, ex.Message); return; }
        foreach (var f in files)
            if (!filter.SkipFile(f)) ops.Add(new Op(OpKind.Copy, f.FullName, Path.Combine(target, f.Name), f.Length));
        foreach (var d in dirs)
        {
            ctx?.Check();
            if (!filter.SkipDir(d)) Expand(d, Path.Combine(target, d.Name), filter, ops, ctx);
        }
    }

    /// <summary>Ejecuta una lista de operaciones con progreso, pausa y verificación.</summary>
    public static void Execute(JobContext ctx, List<Op> ops, CopyOptions opt, bool deleteSourceAfter = false)
    {
        // Expandir árboles completos
        var flat = new List<Op>();
        foreach (var op in ops)
        {
            if (op.Kind == OpKind.CopyTree) Expand(new DirectoryInfo(op.Src), op.Dst, opt.Filter.Prepare(), flat, ctx);
            else flat.Add(op);
        }
        long total = flat.Where(o => o.Kind == OpKind.Copy).Sum(o => o.Size);
        ctx.SetTotal(total, flat.Count(o => o.Kind == OpKind.Copy));

        try
        {
            string root = Path.GetPathRoot(flat.FirstOrDefault(o => o.Kind == OpKind.Copy)?.Dst ?? "");
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                if (di.IsReady && total > di.AvailableFreeSpace)
                    Log.Warn($"Puede que no haya espacio suficiente en {root} ({Fmt.Size(di.AvailableFreeSpace)} libres, se necesitan {Fmt.Size(total)}).");
            }
        }
        catch { }

        ConflictAnswer? sticky = null;
        var buffer = new byte[Math.Clamp(Settings.Current.BufferMB, 1, 64) * 1048576];

        foreach (var op in flat)
        {
            ctx.Check();
            try
            {
                switch (op.Kind)
                {
                    case OpKind.MkDir:
                        Directory.CreateDirectory(op.Dst);
                        break;
                    case OpKind.Delete:
                        ctx.SetCurrent("Eliminando " + op.Src);
                        File.SetAttributes(op.Src, FileAttributes.Normal);
                        File.Delete(op.Src);
                        break;
                    case OpKind.DeleteDir:
                        ctx.SetCurrent("Eliminando " + op.Src);
                        ForceDeleteDir(op.Src);
                        break;
                    case OpKind.Copy:
                        if (CopyFile(ctx, op, opt, buffer, ref sticky) && deleteSourceAfter)
                        {
                            File.SetAttributes(op.Src, FileAttributes.Normal);
                            File.Delete(op.Src);
                        }
                        ctx.FileDone();
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ctx.Fail(op.Src, ex.Message); if (op.Kind == OpKind.Copy) ctx.FileDone(); }
        }
    }

    static bool CopyFile(JobContext ctx, Op op, CopyOptions opt, byte[] buffer, ref ConflictAnswer? sticky)
    {
        var si = new FileInfo(op.Src);
        string dst = op.Dst;
        ctx.SetCurrent(op.Src);

        if (File.Exists(dst))
        {
            var di = new FileInfo(dst);
            ConflictAnswer ans = opt.Conflict switch
            {
                ConflictPolicy.Overwrite => ConflictAnswer.Overwrite,
                ConflictPolicy.Skip => ConflictAnswer.Skip,
                ConflictPolicy.KeepBoth => ConflictAnswer.KeepBoth,
                ConflictPolicy.IfNewer => si.LastWriteTimeUtc > di.LastWriteTimeUtc.AddSeconds(2) ? ConflictAnswer.Overwrite : ConflictAnswer.Skip,
                ConflictPolicy.IfDifferent => SameMeta(si, di) ? ConflictAnswer.Skip : ConflictAnswer.Overwrite,
                _ => sticky ?? AskConflict(ctx, si, di, out sticky)
            };
            if (ans == ConflictAnswer.Cancel) { ctx.Job.Cancel(); ctx.Check(); }
            if (ans == ConflictAnswer.Skip) { ctx.AddBytes(si.Length); return false; }
            if (ans == ConflictAnswer.KeepBoth) dst = UniqueName(dst);
            else File.SetAttributes(dst, FileAttributes.Normal);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string tmp = dst + ".dss-part";
        var hasher = opt.Verify ? new XxHash128() : null;
        try
        {
            using (var fin = new FileStream(op.Src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            using (var fout = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None))
            {
                if (fin.Length > 0) fout.SetLength(fin.Length);
                int n;
                while ((n = fin.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ctx.Check();
                    fout.Write(buffer, 0, n);
                    hasher?.Append(buffer.AsSpan(0, n));
                    ctx.AddBytes(n);
                }
            }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        if (opt.PreserveTimes)
        {
            File.SetCreationTimeUtc(dst, si.CreationTimeUtc);
            File.SetLastWriteTimeUtc(dst, si.LastWriteTimeUtc);
        }
        File.SetAttributes(dst, si.Attributes & ~FileAttributes.ReadOnly);
        if ((si.Attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, si.Attributes);

        if (hasher != null)
        {
            ctx.SetCurrent("Verificando " + dst);
            byte[] a = hasher.GetCurrentHash();
            byte[] b = Hashing.XxHash(dst, ctx.Token);
            if (!a.AsSpan().SequenceEqual(b)) throw new IOException("La verificación ha fallado: la copia no coincide con el original");
        }
        return true;
    }

    static ConflictAnswer AskConflict(JobContext ctx, FileInfo src, FileInfo dst, out ConflictAnswer? sticky)
    {
        var (ans, all) = Application.Current.Dispatcher.Invoke(() => Dialogs.AskConflict(src, dst)).GetAwaiter().GetResult();
        sticky = all ? ans : null;
        return ans;
    }

    public static bool SameMeta(FileInfo a, FileInfo b) =>
        a.Length == b.Length && Math.Abs((a.LastWriteTimeUtc - b.LastWriteTimeUtc).TotalSeconds) <= 2;

    public static string UniqueName(string path)
    {
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    public static void ForceDeleteDir(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        Directory.Delete(dir, true);
    }

    static void RemoveEmptyDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir)) RemoveEmptyDirs(d);
            if (Directory.GetFileSystemEntries(dir).Length == 0 && Path.GetPathRoot(dir) != dir) Directory.Delete(dir);
        }
        catch { }
    }

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public static bool IsInside(string child, string parent)
    {
        string c = Path.GetFullPath(child).TrimEnd('\\') + "\\";
        string p = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }
}

// ───────────────────────── Comparación y sincronización ─────────────────────────

public enum DiffStatus { Same, OnlyLeft, OnlyRight, LeftNewer, RightNewer, Different }
public enum SyncMode { Mirror, Update, TwoWay }
public enum SyncAction { None, ToRight, ToLeft, DeleteRight, DeleteLeft }

public class CompareItem : Notifier
{
    public string RelPath { get; init; } = "";
    public bool IsDir { get; init; }
    public long LeftSize { get; init; } = -1;
    public long RightSize { get; init; } = -1;
    public DateTime? LeftTime { get; init; }
    public DateTime? RightTime { get; init; }
    public DiffStatus Status { get; init; }

    SyncAction action; public SyncAction Action { get => action; set { Set(ref action, value); Raise(nameof(ActionGlyph)); Raise(nameof(ActionText)); } }
    bool include = true; public bool Include { get => include; set => Set(ref include, value); }

    public string LeftSizeText => LeftSize < 0 ? "" : IsDir ? "Carpeta" : Fmt.Size(LeftSize);
    public string RightSizeText => RightSize < 0 ? "" : IsDir ? "Carpeta" : Fmt.Size(RightSize);
    public string LeftTimeText => LeftTime?.ToString("dd/MM/yy HH:mm") ?? "";
    public string RightTimeText => RightTime?.ToString("dd/MM/yy HH:mm") ?? "";
    public string StatusText => Status switch
    {
        DiffStatus.Same => "Idéntico", DiffStatus.OnlyLeft => "Solo en origen", DiffStatus.OnlyRight => "Solo en destino",
        DiffStatus.LeftNewer => "Origen más reciente", DiffStatus.RightNewer => "Destino más reciente", _ => "Diferente"
    };
    public string ActionGlyph => action switch
    {
        SyncAction.ToRight => Icons.Forward, SyncAction.ToLeft => Icons.Back,
        SyncAction.DeleteRight or SyncAction.DeleteLeft => Icons.Delete, _ => ""
    };
    public string ActionText => action switch
    {
        SyncAction.ToRight => "Copiar →", SyncAction.ToLeft => "← Copiar", SyncAction.DeleteRight => "Borrar en destino",
        SyncAction.DeleteLeft => "Borrar en origen", _ => "—"
    };
}

public static class FolderSync
{
    public static List<CompareItem> Compare(string left, string right, FileFilter filter, bool byContent, CancellationToken tk, Action<string> progress)
    {
        filter.Prepare();
        var list = new List<CompareItem>();
        Walk(new DirectoryInfo(left), Directory.Exists(right) ? new DirectoryInfo(right) : null, "", filter, byContent, list, tk, progress);
        return list;
    }

    static void Walk(DirectoryInfo l, DirectoryInfo r, string rel, FileFilter f, bool byContent, List<CompareItem> list, CancellationToken tk, Action<string> progress)
    {
        tk.ThrowIfCancellationRequested();
        progress?.Invoke(rel == "" ? l.FullName : rel);
        var lf = SafeFiles(l).Where(x => !f.SkipFile(x)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var rf = r == null ? new() : SafeFiles(r).Where(x => !f.SkipFile(x)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var ld = SafeDirs(l).Where(x => !f.SkipDir(x)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var rd = r == null ? new() : SafeDirs(r).Where(x => !f.SkipDir(x)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in lf.Keys.Union(rf.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            lf.TryGetValue(name, out var a); rf.TryGetValue(name, out var b);
            DiffStatus st;
            if (b == null) st = DiffStatus.OnlyLeft;
            else if (a == null) st = DiffStatus.OnlyRight;
            else if (FileOps.SameMeta(a, b)) st = byContent && !Hashing.SameContent(a.FullName, b.FullName, tk) ? DiffStatus.Different : DiffStatus.Same;
            else if (byContent && a.Length == b.Length && Hashing.SameContent(a.FullName, b.FullName, tk)) st = DiffStatus.Same;
            else if (a.LastWriteTimeUtc > b.LastWriteTimeUtc.AddSeconds(2)) st = DiffStatus.LeftNewer;
            else if (b.LastWriteTimeUtc > a.LastWriteTimeUtc.AddSeconds(2)) st = DiffStatus.RightNewer;
            else st = DiffStatus.Different;
            list.Add(new CompareItem
            {
                RelPath = Path.Combine(rel, name), Status = st,
                LeftSize = a?.Length ?? -1, RightSize = b?.Length ?? -1,
                LeftTime = a?.LastWriteTime, RightTime = b?.LastWriteTime
            });
        }
        foreach (var name in ld.Keys.Union(rd.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            ld.TryGetValue(name, out var a); rd.TryGetValue(name, out var b);
            string sub = Path.Combine(rel, name);
            if (a != null && b != null) Walk(a, b, sub, f, byContent, list, tk, progress);
            else list.Add(new CompareItem
            {
                RelPath = sub, IsDir = true, Status = a != null ? DiffStatus.OnlyLeft : DiffStatus.OnlyRight,
                LeftSize = a != null ? 0 : -1, RightSize = b != null ? 0 : -1,
                LeftTime = a?.LastWriteTime, RightTime = b?.LastWriteTime
            });
        }
    }

    public static void Plan(IEnumerable<CompareItem> items, SyncMode mode)
    {
        foreach (var i in items)
        {
            i.Action = (mode, i.Status) switch
            {
                (_, DiffStatus.Same) => SyncAction.None,
                (_, DiffStatus.OnlyLeft) => SyncAction.ToRight,
                (SyncMode.Mirror, DiffStatus.OnlyRight) => SyncAction.DeleteRight,
                (SyncMode.TwoWay, DiffStatus.OnlyRight) => SyncAction.ToLeft,
                (SyncMode.Update, DiffStatus.OnlyRight) => SyncAction.None,
                (SyncMode.TwoWay, DiffStatus.RightNewer) => SyncAction.ToLeft,
                (SyncMode.Update, DiffStatus.RightNewer) => SyncAction.None,
                _ => SyncAction.ToRight
            };
        }
    }

    public static List<Op> ToOps(IEnumerable<CompareItem> items, string left, string right)
    {
        var ops = new List<Op>();
        foreach (var i in items.Where(i => i.Include && i.Action != SyncAction.None))
        {
            string l = Path.Combine(left, i.RelPath), r = Path.Combine(right, i.RelPath);
            switch (i.Action)
            {
                case SyncAction.ToRight: ops.Add(i.IsDir ? new Op(OpKind.CopyTree, l, r) : new Op(OpKind.Copy, l, r, i.LeftSize)); break;
                case SyncAction.ToLeft: ops.Add(i.IsDir ? new Op(OpKind.CopyTree, r, l) : new Op(OpKind.Copy, r, l, i.RightSize)); break;
                case SyncAction.DeleteRight: ops.Add(new Op(i.IsDir ? OpKind.DeleteDir : OpKind.Delete, r, null)); break;
                case SyncAction.DeleteLeft: ops.Add(new Op(i.IsDir ? OpKind.DeleteDir : OpKind.Delete, l, null)); break;
            }
        }
        // Primero borrados (liberan espacio), luego copias
        return ops.OrderBy(o => o.Kind is OpKind.Delete or OpKind.DeleteDir ? 0 : 1).ToList();
    }

    /// <summary>Trabajo de sincronización/clonado que compara y aplica en el propio hilo de la cola.</summary>
    public static TransferJob SyncJob(string title, string left, string right, SyncMode mode, CopyOptions opt, bool byContent = false, string glyph = null)
    {
        return new TransferJob
        {
            Title = title,
            Subtitle = $"{left} → {right}",
            Glyph = glyph ?? Icons.Sync,
            Work = ctx => Task.Run(() =>
            {
                Directory.CreateDirectory(right);
                ctx.SetCurrent("Comparando carpetas…");
                var items = Compare(left, right, opt.Filter, byContent, ctx.Token, s => ctx.SetCurrent("Comparando " + s));
                Plan(items, mode);
                var ops = ToOps(items, left, right);
                Log.Info($"{title}: {ops.Count(o => o.Kind is OpKind.Copy or OpKind.CopyTree)} copias, {ops.Count(o => o.Kind is OpKind.Delete or OpKind.DeleteDir)} borrados, {items.Count(i => i.Status == DiffStatus.Same)} sin cambios");
                var o2 = new CopyOptions { Conflict = ConflictPolicy.Overwrite, Verify = opt.Verify, Filter = opt.Filter, PreserveTimes = true };
                FileOps.Execute(ctx, ops, o2);
            })
        };
    }

    public static TransferJob OpsJob(string title, string sub, List<Op> ops, CopyOptions opt) => new()
    {
        Title = title, Subtitle = sub, Glyph = Icons.Sync,
        Work = ctx => Task.Run(() => FileOps.Execute(ctx, ops, new CopyOptions { Conflict = ConflictPolicy.Overwrite, Verify = opt.Verify, Filter = opt.Filter }))
    };

    static FileInfo[] SafeFiles(DirectoryInfo d) { try { return d.GetFiles(); } catch { return []; } }
    static DirectoryInfo[] SafeDirs(DirectoryInfo d) { try { return d.GetDirectories(); } catch { return []; } }
}

public static class Hashing
{
    public static byte[] XxHash(string file, CancellationToken tk)
    {
        var h = new XxHash128();
        var buf = new byte[4 * 1048576];
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) { tk.ThrowIfCancellationRequested(); h.Append(buf.AsSpan(0, n)); }
        return h.GetCurrentHash();
    }

    public static bool SameContent(string a, string b, CancellationToken tk)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        return XxHash(a, tk).AsSpan().SequenceEqual(XxHash(b, tk));
    }
}
