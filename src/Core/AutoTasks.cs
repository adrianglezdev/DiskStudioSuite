using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace DiskStudio.Core;

public enum TaskKind { Copy, Update, Mirror, TwoWay }

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
    /// <summary>Nº de serie del disco que dispara la tarea al conectarlo (vacío = solo manual).</summary>
    public string TriggerSerial { get; set; } = "";
    public string TriggerName { get; set; } = "";
    /// <summary>Si la letra del disco cambia, el destino/origen en ese disco se reasigna.</summary>
    public bool FollowLetter { get; set; } = true;
    DateTime? lastRun; public DateTime? LastRun { get => lastRun; set { Set(ref lastRun, value); Raise(nameof(LastRunText)); } }

    public string KindText => Kind switch { TaskKind.Copy => "Copiar todo", TaskKind.Update => "Actualizar (solo cambios)", TaskKind.Mirror => "Espejo", _ => "Bidireccional" };
    public string TriggerText => string.IsNullOrEmpty(TriggerSerial) ? "Manual" : $"Al conectar {TriggerName}";
    public string LastRunText => LastRun?.ToString("dd/MM/yyyy HH:mm") ?? "Nunca";
    public void Refresh() { Raise(nameof(KindText)); Raise(nameof(TriggerText)); Raise(nameof(Name)); }

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
        var opt = new CopyOptions { Verify = Verify, Filter = new FileFilter { Include = Include, Exclude = Exclude } };
        LastRun = DateTime.Now;
        AutoTasks.Save();
        return Kind switch
        {
            TaskKind.Copy => FileOps.CopyJob([src], dst, false, new CopyOptions { Verify = Verify, Filter = opt.Filter, Conflict = ConflictPolicy.IfDifferent }),
            TaskKind.Mirror => FolderSync.SyncJob("Tarea: " + Name, src, dst, SyncMode.Mirror, opt, false, Icons.Tasks),
            TaskKind.TwoWay => FolderSync.SyncJob("Tarea: " + Name, src, dst, SyncMode.TwoWay, opt, false, Icons.Tasks),
            _ => FolderSync.SyncJob("Tarea: " + Name, src, dst, SyncMode.Update, opt, false, Icons.Tasks),
        };
    }
}

public static class AutoTasks
{
    public static ObservableCollection<AutoTask> Items { get; } = Load();
    static string FilePath => Path.Combine(Settings.DataDir, "tasks.json");

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

    public static void Hook()
    {
        AppState.VolumeArrived += (v, disk) =>
        {
            if (!Settings.Current.AutoTasks || disk == null || string.IsNullOrEmpty(disk.Serial)) return;
            foreach (var t in Items.Where(t => t.Enabled && string.Equals(t.TriggerSerial, disk.Serial, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Info($"Tarea automática \"{t.Name}\" disparada por {v.Letter} {v.Name}");
                Toast.Show("Tarea automática", $"{t.Name} · {t.KindText}", Icons.Tasks);
                TransferQueue.Enqueue(t.ToJob(v));
            }
        };
    }
}
