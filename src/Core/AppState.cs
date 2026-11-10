using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace DiskStudio.Core;

public enum ConflictPolicy { Ask, Overwrite, Skip, IfNewer, IfDifferent, KeepBoth }
public enum AfterQueue { Nothing, Sleep, Shutdown }

public class Settings
{
    public string Theme { get; set; } = "System";       // System | Light | Dark
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Ask;
    public bool VerifyByDefault { get; set; }
    public bool ShowHidden { get; set; }
    public bool ConfirmDelete { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool AutoTasks { get; set; } = true;
    public int BufferMB { get; set; } = 4;
    public int SpeedLimitMBs { get; set; }               // 0 = sin límite
    public List<string> Favorites { get; set; } = [];
    public string LeftPath { get; set; }
    public string RightPath { get; set; }

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Disk Studio Suite");
    static string FilePath => Path.Combine(Dir, "settings.json");
    public static string DataDir { get { Migrate(); Directory.CreateDirectory(Dir); return Dir; } }

    /// <summary>Copia los ajustes de la versión anterior (Copyrobot) la primera vez.</summary>
    static void Migrate()
    {
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Copyrobot");
            if (Directory.Exists(Dir) || !Directory.Exists(old)) return;
            foreach (var f in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
            {
                string t = Path.Combine(Dir, Path.GetRelativePath(old, f));
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t, false);
            }
        }
        catch { }
    }

    public static Settings Current { get; private set; } = Load();

    static Settings Load()
    {
        try { Migrate(); if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new Settings();
    }

    public static void Save()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

public enum LogKind { Info, Success, Warning, Error }

public class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string TimeText => Time.ToString("HH:mm:ss");
    public string Glyph => Kind switch { LogKind.Success => Icons.Success, LogKind.Warning => Icons.Warning, LogKind.Error => Icons.Error, _ => Icons.Info };
}

public static class Log
{
    public static ObservableCollection<LogEntry> Entries { get; } = [];

    public static void Write(string text, LogKind kind = LogKind.Info)
    {
        void add()
        {
            Entries.Insert(0, new LogEntry { Kind = kind, Text = text });
            while (Entries.Count > 5000) Entries.RemoveAt(Entries.Count - 1);
        }
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) add(); else d.BeginInvoke(add);
    }

    public static void Info(string t) => Write(t);
    public static void Ok(string t) => Write(t, LogKind.Success);
    public static void Warn(string t) => Write(t, LogKind.Warning);
    public static void Error(string t) => Write(t, LogKind.Error);

    public static void Export(string file) =>
        File.WriteAllLines(file, Entries.Reverse().Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss}\t{e.Kind}\t{e.Text}"));
}

/// <summary>Estado compartido: discos detectados y eventos de conexión.</summary>
public static class AppState
{
    public static List<PhysicalDisk> Disks { get; private set; } = [];
    public static List<VolumeInfo> Volumes { get; private set; } = [];
    public static event Action Changed;
    public static event Action<VolumeInfo, PhysicalDisk> VolumeArrived;

    static bool scanning, pending;
    static bool first = true;

    public static async void Rescan(bool smart = true)
    {
        if (scanning) { pending = true; return; }
        scanning = true;
        try
        {
            var (disks, vols) = await Task.Run(() => DiskScanner.Scan(smart));
            var oldLetters = Volumes.Where(v => v.Ready).Select(v => v.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newLetters = vols.Where(v => v.Ready).Select(v => v.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldVols = Volumes;
            Disks = disks; Volumes = vols;
            Changed?.Invoke();

            if (!first)
            {
                foreach (var v in vols.Where(v => v.Ready && !oldLetters.Contains(v.Letter)))
                {
                    var pd = disks.Find(d => d.Number == v.DiskNumber);
                    Toast.Show("Disco conectado", $"{v.Letter} {v.Name} · {Fmt.Size(v.Total)}" + (pd != null ? $"\n{pd.Title}" : ""), Icons.Usb);
                    Log.Ok($"Disco conectado: {v.Letter} {v.Name} ({pd?.Title}, {Fmt.Size(v.Total)})");
                    VolumeArrived?.Invoke(v, pd);
                }
                foreach (var v in oldVols.Where(v => v.Ready && !newLetters.Contains(v.Letter)))
                {
                    Toast.Show("Disco desconectado", $"{v.Letter} {v.Name}", Icons.Eject);
                    Log.Info($"Disco desconectado: {v.Letter} {v.Name}");
                }
            }
            first = false;
        }
        finally
        {
            scanning = false;
            if (pending) { pending = false; Rescan(smart); }
        }
    }

    public static VolumeInfo VolumeOf(string path)
    {
        string root = Path.GetPathRoot(path ?? "");
        return Volumes.Find(v => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    public static DispatcherTimer Debounce(TimeSpan t, Action a)
    {
        var timer = new DispatcherTimer { Interval = t };
        timer.Tick += (_, _) => { timer.Stop(); a(); };
        return timer;
    }
}

/// <summary>Notificaciones emergentes dentro de la app.</summary>
public static class Toast
{
    public static event Action<string, string, string> Requested;
    public static void Show(string title, string text, string glyph = Icons.Info)
    {
        if (!Settings.Current.Notifications) return;
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(() => Requested?.Invoke(title, text, glyph));
    }
}
