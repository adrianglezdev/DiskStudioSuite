using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace DiskStudio.Core;

public enum TaskKind { Copy, Update, Mirror, TwoWay, Versioned }

public class AutoTask : Notifier
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    string name = "Nueva tarea"; public string Name { get => name; set => Set(ref name, value); }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public TaskKind Kind { get; set; } = TaskKind.Update;
    public string Include { get; set; } = "";
    public string Exclude { get; set; } = "";
    public bool Verify { get; set; }
    bool enabled = true; public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    /// <summary>Nº de serie del disco que dispara la tarea al conectarlo (vacío = sin disparador por disco).</summary>
    public string TriggerSerial { get; set; } = "";
    public string TriggerName { get; set; } = "";
    /// <summary>Si la letra del disco cambia, el destino/origen en ese disco se reasigna.</summary>
    public bool FollowLetter { get; set; } = true;
    DateTime? lastRun; public DateTime? LastRun { get => lastRun; set { Set(ref lastRun, value); Raise(nameof(LastRunText)); } }

    // Programación
    public ScheduleKind Schedule { get; set; } = ScheduleKind.None;
    public int EveryHours { get; set; } = 6;
    public string Time { get; set; } = "21:00";
    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday];
    public DateTime? NextRun { get; set; }
    /// <summary>Si el equipo estaba apagado a la hora prevista, ejecutarla en cuanto se pueda.</summary>
    public bool RunMissed { get; set; } = true;
    // Versiones
    public int KeepVersions { get; set; } = 10;

    [JsonIgnore] public TimeSpan TimeOfDay => TimeSpan.TryParse(Time, out var t) ? t : new TimeSpan(21, 0, 0);
    public string KindText => Kind switch { TaskKind.Copy => "Copiar todo", TaskKind.Update => "Actualizar (solo cambios)", TaskKind.Mirror => "Espejo", TaskKind.Versioned => $"Por versiones (últimas {KeepVersions})", _ => "Bidireccional" };
    public string TriggerText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(TriggerSerial)) parts.Add($"Al conectar {TriggerName}");
            if (Schedule != ScheduleKind.None) parts.Add(Core.Schedule.Describe(Schedule, EveryHours, TimeOfDay, Days));
            return parts.Count == 0 ? "Manual" : string.Join(" · ", parts);
        }
    }
    public string LastRunText => LastRun?.ToString("dd/MM/yyyy HH:mm") ?? "Nunca";
    public string NextRunText => Schedule == ScheduleKind.None || !Enabled ? "" : NextRun?.ToString("dd/MM/yyyy HH:mm") ?? "—";
    public void Refresh() { Raise(nameof(KindText)); Raise(nameof(TriggerText)); Raise(nameof(Name)); }

    public void PlanNext(DateTime from) => NextRun = Core.Schedule.Next(Schedule, EveryHours, TimeOfDay, Days, from);

    public TransferJob ToJob(VolumeInfo arrived = null)
    {
        string src = Source, dst = Destination;
        if (arrived != null && FollowLetter)
        {
            // Reasignar la ruta que estaba en el disco disparador a su letra actual
            string remap(string p) => p.Length >= 3 && AppState.VolumeOf(p)?.DiskNumber != arrived.DiskNumber && !Directory.Exists(Path.GetPathRoot(p))
                ? arrived.Root + p[3..] : p;
            if (!Directory.Exists(Path.GetPathRoot(dst) ?? "")) dst = remap(dst);
            if (!Directory.Exists(Path.GetPathRoot(src) ?? "")) src = remap(src);
        }
        var filter = new FileFilter { Include = Include, Exclude = Exclude };
        var opt = new CopyOptions { Verify = Verify, Filter = filter };
        LastRun = DateTime.Now;
        AutoTasks.Save();
        return Kind switch
        {
            TaskKind.Copy => FileOps.CopyJob([src], dst, false, new CopyOptions { Verify = Verify, Filter = opt.Filter, Conflict = ConflictPolicy.IfDifferent }),
            TaskKind.Mirror => FolderSync.SyncJob("Tarea: " + Name, src, dst, SyncMode.Mirror, opt, false, Icons.Tasks),
            TaskKind.TwoWay => FolderSync.SyncJob("Tarea: " + Name, src, dst, SyncMode.TwoWay, opt, false, Icons.Tasks),
            TaskKind.Versioned => VersionedBackup.Job("Tarea: " + Name, src, dst, filter, KeepVersions, Verify),
            _ => FolderSync.SyncJob("Tarea: " + Name, src, dst, SyncMode.Update, opt, false, Icons.Tasks),
        };
    }
}

public static class AutoTasks
{
    public static ObservableCollection<AutoTask> Items { get; } = Load();
    static string FilePath => Path.Combine(Settings.DataDir, "tasks.json");
    static readonly Dictionary<string, TransferJob> running = [];
    static DispatcherTimer timer;

    static ObservableCollection<AutoTask> Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ObservableCollection<AutoTask>>(File.ReadAllText(FilePath)) ?? []; }
        catch { }
        return [];
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    static bool Busy(AutoTask t) => running.TryGetValue(t.Id, out var j) && j.IsActive;

    public static void Start(AutoTask t, VolumeInfo arrived = null)
    {
        if (Busy(t)) { Log.Info($"La tarea «{t.Name}» ya está en marcha."); return; }
        var job = t.ToJob(arrived);
        running[t.Id] = job;
        TransferQueue.Enqueue(job);
    }

    public static void Hook()
    {
        AppState.VolumeArrived += (v, disk) =>
        {
            if (!Settings.Current.AutoTasks || disk == null || string.IsNullOrEmpty(disk.Serial)) return;
            foreach (var t in Items.Where(t => t.Enabled && string.Equals(t.TriggerSerial, disk.Serial, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Info($"Tarea automática \"{t.Name}\" disparada por {v.Letter} {v.Name}");
                Toast.Show("Tarea automática", $"{t.Name} · {t.KindText}", Icons.Tasks);
                Start(t, v);
            }
        };

        // Tareas programadas: se revisan cada 30 s mientras la app está abierta (también en segundo plano)
        foreach (var t in Items.Where(t => t.Schedule != ScheduleKind.None))
            if (t.NextRun == null || (!t.RunMissed && t.NextRun < DateTime.Now)) t.PlanNext(DateTime.Now);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Tick, DispatcherPriority.ApplicationIdle);
    }

    static void Tick()
    {
        if (!Settings.Current.AutoTasks) return;
        var now = DateTime.Now;
        bool changed = false;
        foreach (var t in Items.Where(t => t.Enabled && t.Schedule != ScheduleKind.None && t.NextRun is DateTime n && n <= now).ToList())
        {
            string dstRoot = Path.GetPathRoot(t.Destination) ?? "";
            if (!Directory.Exists(t.Source) || !Directory.Exists(dstRoot))
            {
                // Destino no conectado: reintentar en 15 minutos (y al conectar su disco si tiene disparador)
                Log.Warn($"Tarea programada «{t.Name}»: " + (!Directory.Exists(t.Source) ? "el origen" : "el destino") + " no está disponible. Se reintentará.");
                t.NextRun = now.AddMinutes(15);
                changed = true;
                continue;
            }
            Log.Info($"Tarea programada «{t.Name}» ({Schedule.Describe(t.Schedule, t.EveryHours, t.TimeOfDay, t.Days)})");
            Toast.Show("Tarea programada", t.Name, Icons.Clock);
            Start(t);
            t.PlanNext(now);
            changed = true;
        }
        if (changed) { Save(); Changed?.Invoke(); }
    }

    public static event Action Changed;
}
