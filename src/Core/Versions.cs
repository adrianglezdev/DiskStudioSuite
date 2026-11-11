using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DiskStudio.Core;

public class BackupVersion
{
    public string Path { get; init; } = "";
    public DateTime Date { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string DateText => Date.ToString(L.English ? "dddd d MMMM yyyy, HH:mm" : "dddd d 'de' MMMM yyyy, HH:mm", L.Culture);
}

/// <summary>
/// Copias por versiones: cada ejecución crea una carpeta con fecha que contiene la copia completa.
/// Los archivos que no han cambiado no se copian otra vez: se enlazan (enlaces duros de NTFS) con la versión anterior,
/// así que cada versión ocupa solo lo que ha cambiado y se puede abrir o restaurar por separado.
/// </summary>
public static class VersionedBackup
{
    const string Stamp = "yyyy-MM-dd_HHmmss";
    static readonly Regex VersionName = new(@"^\d{4}-\d{2}-\d{2}_\d{6}$");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existing, IntPtr sec);

    public static List<BackupVersion> List(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root).Select(d => (d, n: System.IO.Path.GetFileName(d))).Where(x => VersionName.IsMatch(x.n))
            .Select(x => new BackupVersion { Path = x.d, Date = DateTime.ParseExact(x.n, Stamp, CultureInfo.InvariantCulture) })
            .OrderByDescending(v => v.Date).ToList();
    }

    public static TransferJob Job(string title, string source, string root, FileFilter filter, int keep, bool verify) => new()
    {
        Title = title,
        Subtitle = $"{source} → {root}",
        Glyph = Icons.History,
        Work = ctx => Task.Run(() => Run(ctx, source, root, filter, keep, verify, DateTime.Now))
    };

    /// <summary>Crea una versión nueva. Devuelve su carpeta.</summary>
    public static string Run(JobContext ctx, string source, string root, FileFilter filter, int keep, bool verify, DateTime when)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("No se encuentra el origen: " + source);
        if (FileOps.IsInside(root, source)) throw new InvalidOperationException("La carpeta de versiones no puede estar dentro del origen.");
        Directory.CreateDirectory(root);
        filter = (filter ?? new FileFilter()).Prepare();
        var prev = List(root).FirstOrDefault();
        string final = System.IO.Path.Combine(root, when.ToString(Stamp, CultureInfo.InvariantCulture));
        if (Directory.Exists(final)) throw new IOException("Ya existe una versión con esta fecha.");
        string temp = final + ".parcial";
        if (Directory.Exists(temp)) FileOps.ForceDeleteDir(temp);
        Directory.CreateDirectory(temp);

        // 1) Inventario
        ctx.SetCurrent("Analizando el origen…");
        var files = new List<(FileInfo f, string rel, string link)>();
        var dirs = new List<string>();
        void walk(DirectoryInfo d, string rel)
        {
            ctx.Check();
            IEnumerable<FileSystemInfo> items;
            try { items = d.EnumerateFileSystemInfos(); }
            catch (Exception ex) { ctx.Fail(d.FullName, ex.Message); return; }
            foreach (var i in items)
            {
                string r = rel.Length == 0 ? i.Name : System.IO.Path.Combine(rel, i.Name);
                if (i is DirectoryInfo sub) { if (filter.SkipDir(sub)) continue; dirs.Add(r); walk(sub, r); }
                else if (i is FileInfo f && !filter.SkipFile(f))
                {
                    string link = null;
                    if (prev != null)
                    {
                        var old = new FileInfo(System.IO.Path.Combine(prev.Path, r));
                        if (old.Exists && old.Length == f.Length && Math.Abs((old.LastWriteTimeUtc - f.LastWriteTimeUtc).TotalSeconds) < 2.1) link = old.FullName;
                    }
                    files.Add((f, r, link));
                }
            }
        }
        walk(new DirectoryInfo(source), "");
        long toCopy = files.Where(x => x.link == null).Sum(x => x.f.Length);
        ctx.SetTotal(toCopy, files.Count);

        // 2) Copia o enlace
        foreach (var d in dirs) Directory.CreateDirectory(System.IO.Path.Combine(temp, d));
        var buf = new byte[Math.Max(1, Settings.Current.BufferMB) << 20];
        long linked = 0, copied = 0;
        bool linksWork = true;
        foreach (var (f, rel, link) in files)
        {
            ctx.Check();
            string target = System.IO.Path.Combine(temp, rel);
            try
            {
                if (link != null && linksWork)
                {
                    if (CreateHardLink(target, link, IntPtr.Zero)) { linked++; ctx.FileDone(); continue; }
                    int err = Marshal.GetLastWin32Error();
                    if (err is 1 or 50 or 17) linksWork = false;   // el sistema de archivos no admite enlaces: copiar todo
                    ctx.AddTotal(f.Length);
                }
                else if (link != null) ctx.AddTotal(f.Length);
                ctx.SetCurrent(rel);
                using (var i = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                using (var o = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    int n;
                    while ((n = i.Read(buf)) > 0) { ctx.Check(); o.Write(buf, 0, n); ctx.AddBytes(n); }
                }
                File.SetLastWriteTimeUtc(target, f.LastWriteTimeUtc);
                File.SetCreationTimeUtc(target, f.CreationTimeUtc);
                if (verify && !Hashing.SameContent(f.FullName, target, ctx.Token)) throw new IOException("La verificación falló: la copia no coincide.");
                copied++;
                ctx.FileDone();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ctx.Fail(f.FullName, ex.Message); }
        }
        Directory.Move(temp, final);

        // 3) Conservar solo las últimas N
        if (keep > 0)
            foreach (var old in List(root).Skip(keep))
            {
                ctx.SetCurrent("Borrando versiones antiguas…");
                try { FileOps.ForceDeleteDir(old.Path); } catch (Exception ex) { Log.Warn($"No se pudo borrar la versión {old.Name}: {ex.Message}"); }
            }
        Log.Ok($"Versión {System.IO.Path.GetFileName(final)} creada: {copied} archivos copiados, {linked} sin cambios enlazados" + (!linksWork ? " (el destino no admite enlaces: se copió todo)" : ""));
        return final;
    }
}

public enum ScheduleKind { None, EveryHours, Daily, Weekly }

/// <summary>Cálculo de la próxima ejecución de una tarea programada.</summary>
public static class Schedule
{
    public static DateTime? Next(ScheduleKind kind, int everyHours, TimeSpan time, IReadOnlyCollection<DayOfWeek> days, DateTime after)
    {
        switch (kind)
        {
            case ScheduleKind.EveryHours:
                int h = Math.Clamp(everyHours, 1, 168);
                return after.AddHours(h);
            case ScheduleKind.Daily:
            {
                var t = after.Date + time;
                return t > after ? t : t.AddDays(1);
            }
            case ScheduleKind.Weekly:
            {
                if (days == null || days.Count == 0) return null;
                for (int i = 0; i <= 7; i++)
                {
                    var t = after.Date.AddDays(i) + time;
                    if (t > after && days.Contains(t.DayOfWeek)) return t;
                }
                return null;
            }
            default: return null;
        }
    }

    public static string Describe(ScheduleKind kind, int everyHours, TimeSpan time, IReadOnlyCollection<DayOfWeek> days)
    {
        var es = L.Culture;
        return kind switch
        {
            ScheduleKind.EveryHours => everyHours == 1 ? "Cada hora" : $"Cada {everyHours} horas",
            ScheduleKind.Daily => $"Todos los días a las {time:hh\\:mm}",
            ScheduleKind.Weekly => (days?.Count ?? 0) == 0 ? "Semanal (sin días)" : string.Join(", ", days.OrderBy(d => ((int)d + 6) % 7).Select(d => es.DateTimeFormat.GetAbbreviatedDayName(d))) + $" a las {time:hh\\:mm}",
            _ => ""
        };
    }
}
