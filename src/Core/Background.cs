using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DiskStudio.Core;

// ═══════════════════════════ Historial de salud ═══════════════════════════

public record HealthSample(DateTime Time, int? Health, int? Temp, int? Wear, long? Hours, long? Reallocated, long? Pending, HealthLevel Level);

public static class HealthHistory
{
    static string Dir { get { var d = Path.Combine(Settings.DataDir, "Historial"); Directory.CreateDirectory(d); return d; } }
    static string FileFor(PhysicalDisk d) => Path.Combine(Dir, new string((d.Serial + "_" + d.Model).Where(char.IsLetterOrDigit).Take(60).ToArray()) + ".csv");
    static readonly Dictionary<string, HealthSample> last = [];

    public static long? Attr(PhysicalDisk d, int id)
    {
        var a = d.Attributes.FirstOrDefault(x => x.Id == id);
        if (a == null) return null;
        var digits = new string(a.Raw.TakeWhile(char.IsDigit).ToArray());
        return long.TryParse(digits, out long v) ? v : null;
    }

    public static HealthSample Sample(PhysicalDisk d) =>
        new(DateTime.Now, d.HealthPercent, d.Temperature, d.Wear, d.PowerOnHours, Attr(d, 5), Attr(d, 197), d.Health);

    /// <summary>Guarda una muestra (como mucho una cada 30 minutos, salvo que algo cambie).</summary>
    public static void Record(PhysicalDisk d)
    {
        if (string.IsNullOrEmpty(d.Serial) || (d.HealthPercent == null && d.Temperature == null)) return;
        var s = Sample(d);
        string key = FileFor(d);
        if (!last.TryGetValue(key, out var prev)) prev = Load(d).LastOrDefault();
        bool changed = prev == null || prev.Health != s.Health || prev.Wear != s.Wear || prev.Reallocated != s.Reallocated || prev.Pending != s.Pending || prev.Level != s.Level || Math.Abs((prev.Temp ?? 0) - (s.Temp ?? 0)) >= 3;
        if (prev != null && !changed && (s.Time - prev.Time).TotalMinutes < 30) return;
        last[key] = s;
        try
        {
            bool header = !File.Exists(key);
            using var w = File.AppendText(key);
            if (header) w.WriteLine("fecha;salud;temperatura;desgaste;horas;reasignados;pendientes;estado");
            w.WriteLine(string.Join(";", s.Time.ToString("s", CultureInfo.InvariantCulture), s.Health, s.Temp, s.Wear, s.Hours, s.Reallocated, s.Pending, s.Level));
        }
        catch { }
    }

    public static List<HealthSample> Load(PhysicalDisk d)
    {
        var r = new List<HealthSample>();
        try
        {
            string f = FileFor(d);
            if (!File.Exists(f)) return r;
            foreach (var line in File.ReadLines(f).Skip(1))
            {
                var p = line.Split(';');
                if (p.Length < 8 || !DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                int? i(string x) => int.TryParse(x, out int v) ? v : null;
                long? l(string x) => long.TryParse(x, out long v) ? v : null;
                r.Add(new HealthSample(t, i(p[1]), i(p[2]), i(p[3]), l(p[4]), l(p[5]), l(p[6]), Enum.TryParse<HealthLevel>(p[7], out var lv) ? lv : HealthLevel.Unknown));
            }
        }
        catch { }
        return r;
    }
}

// ═══════════════════════════ Vigilancia de discos ═══════════════════════════

public static class DiskMonitor
{
    static DispatcherTimer timer;
    static readonly Dictionary<string, HealthSample> known = [];
    static readonly Dictionary<string, DateTime> tempAlert = [];
    public static event Action<string, string, bool> Alert;   // título, texto, grave

    public static void Start()
    {
        AppState.Changed += Evaluate;
        Restart();
    }

    public static void Restart()
    {
        timer?.Stop();
        if (!Settings.Current.Monitor) return;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Clamp(Settings.Current.MonitorMinutes, 1, 240)) };
        timer.Tick += (_, _) => AppState.Rescan(true);
        timer.Start();
    }

    static void Evaluate()
    {
        foreach (var d in AppState.Disks.Where(d => !string.IsNullOrEmpty(d.Serial)))
        {
            HealthHistory.Record(d);
            if (!Settings.Current.Monitor) continue;
            var s = HealthHistory.Sample(d);
            string key = d.Serial + d.Model;
            if (known.TryGetValue(key, out var prev))
            {
                if (s.Level > prev.Level && s.Level >= HealthLevel.Caution)
                    Raise($"Salud del disco: {d.Title}", s.Level == HealthLevel.Bad ? $"El disco está en mal estado ({d.HealthText}). Haz una copia de seguridad cuanto antes." : $"El disco muestra señales de desgaste o errores ({d.HealthText}).", s.Level == HealthLevel.Bad);
                if (s.Reallocated > (prev.Reallocated ?? 0) || s.Pending > (prev.Pending ?? 0))
                    Raise($"Sectores dañados: {d.Title}", $"Han aparecido sectores defectuosos (reasignados: {s.Reallocated ?? 0}, pendientes: {s.Pending ?? 0}). Es una señal temprana de fallo: haz una copia de seguridad.", true);
                if (s.Health is int h && prev.Health is int ph && ph - h >= 5)
                    Raise($"Salud del disco: {d.Title}", $"La salud ha bajado del {ph}% al {h}%.", false);
            }
            known[key] = s;
            if (s.Temp is int t && t >= Settings.Current.TempLimit && (!tempAlert.TryGetValue(key, out var at) || (DateTime.Now - at).TotalHours >= 1))
            {
                tempAlert[key] = DateTime.Now;
                Raise($"Temperatura alta: {d.Title}", $"El disco está a {t} °C (límite configurado: {Settings.Current.TempLimit} °C). Revisa la ventilación.", t >= Settings.Current.TempLimit + 10);
            }
        }
    }

    static void Raise(string title, string text, bool severe)
    {
        (severe ? (Action<string>)Log.Error : Log.Warn)($"{title}: {text}");
        Alert?.Invoke(title, text, severe);
    }
}

// ═══════════════════════════ Actualizaciones ═══════════════════════════

public record ReleaseAsset(string Name, string Url, long Size);
public record UpdateInfo(Version Version, string Tag, string Url, string Notes, bool Prerelease, List<ReleaseAsset> Assets = null);

public static class Updates
{
    public const string Repo = "adrianglezdev/DiskStudioSuite";
    public static Version Current => typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static UpdateInfo Available { get; private set; }
    public static event Action<UpdateInfo> Found;

    /// <summary>Incluir versiones en pruebas: según el ajuste o, si no se ha elegido, solo mientras se use una 0.x.</summary>
    public static bool IncludePrerelease => Settings.Current.UpdatePrerelease ?? Current.Major == 0;

    /// <summary>Consulta las releases públicas de GitHub. No envía ningún dato del equipo.</summary>
    public static async Task<UpdateInfo> Check()
    {
        string json;
        // Pruebas: DSS_UPDATE_FEED apunta a un JSON local con el mismo formato que la API de GitHub
        if (Environment.GetEnvironmentVariable("DSS_UPDATE_FEED") is string feed && File.Exists(feed)) json = await File.ReadAllTextAsync(feed);
        else
        {
            using var http = Http();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=15");
        }
        Settings.Current.LastUpdateCheck = DateTime.Now;
        Settings.Save();
        var best = Pick(json, Current, IncludePrerelease);
        Available = best;
        if (best != null) Found?.Invoke(best);
        return best;
    }

    public static HttpClient Http(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DiskStudioSuite-UpdateCheck");
        return http;
    }

    /// <summary>Elige la versión más alta que sea más nueva que la actual.</summary>
    public static UpdateInfo Pick(string json, Version current, bool? includePre = null)
    {
        using var doc = JsonDocument.Parse(json);
        UpdateInfo best = null;
        bool allowPre = includePre ?? current.Major == 0;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var dr) && dr.GetBoolean()) continue;
            bool pre = r.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();
            if (pre && !allowPre) continue;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var v)) continue;
            v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            var cur = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (v <= cur || (best != null && v <= best.Version)) continue;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var arr))
                foreach (var x in arr.EnumerateArray())
                    assets.Add(new ReleaseAsset(x.GetProperty("name").GetString() ?? "", x.GetProperty("browser_download_url").GetString() ?? "", x.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
            best = new UpdateInfo(v, tag, r.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases", r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", pre, assets);
        }
        return best;
    }
}

// ═══════════════════════════ Integración con Windows ═══════════════════════════

public static class ShellIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Disk Studio Suite";
    static string Exe => Environment.ProcessPath ?? "";

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{Exe}\" --segundo-plano");
        else k.DeleteValue(RunName, false);
    }

    public static bool AutostartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    static readonly (string cls, string key, string text, string arg)[] Verbs =
    [
        (@"*\shell", "DiskStudioSuite.Copy", "Copiar con Disk Studio Suite", "--copiar"),
        (@"Directory\shell", "DiskStudioSuite.Copy", "Copiar con Disk Studio Suite", "--copiar"),
        (@"*\shell", "DiskStudioSuite.Hash", "Calcular hash con Disk Studio Suite", "--hash"),
        (@"Directory\shell", "DiskStudioSuite.Space", "Analizar espacio con Disk Studio Suite", "--espacio"),
        (@"Drive\shell", "DiskStudioSuite.Space", "Analizar espacio con Disk Studio Suite", "--espacio"),
    ];

    /// <summary>Añade o quita las opciones del menú contextual del Explorador (solo para el usuario actual).</summary>
    public static void SetExplorerMenu(bool on)
    {
        foreach (var (cls, key, text, arg) in Verbs)
        {
            string path = $@"Software\Classes\{cls}\{key}";
            if (!on) { Registry.CurrentUser.DeleteSubKeyTree(path, false); continue; }
            using var k = Registry.CurrentUser.CreateSubKey(path);
            k.SetValue("MUIVerb", text);
            k.SetValue("Icon", $"\"{Exe}\",0");
            using var c = k.CreateSubKey("command");
            c.SetValue("", $"\"{Exe}\" {arg} \"%1\"");
        }
    }
}

// ═══════════════════════════ Instancia única ═══════════════════════════

/// <summary>Si la app ya está abierta, la segunda instancia le pasa sus argumentos y se cierra.</summary>
public static class SingleInstance
{
    static Mutex mutex;
    static string Id => "DiskStudioSuite-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    public static event Action<string[]> ArgsReceived;

    /// <returns>true si esta es la instancia principal.</returns>
    public static bool Claim(bool waitForPrevious)
    {
        var deadline = DateTime.Now.AddSeconds(waitForPrevious ? 8 : 0);
        while (true)
        {
            try
            {
                mutex = new Mutex(true, @"Local\" + Id, out bool created);
                if (created) return true;
                mutex.Dispose(); mutex = null;
            }
            catch (UnauthorizedAccessException) { }
            if (DateTime.Now >= deadline) return false;
            Thread.Sleep(250);
        }
    }

    public static bool Forward(string[] args)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            c.Connect(3000);
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            c.Write(data);
            return true;
        }
        catch { return false; }
    }

    public static void Listen(Dispatcher ui)
    {
        var t = new Thread(() =>
        {
            var sec = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User!;
            sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            while (true)
            {
                try
                {
                    using var s = NamedPipeServerStreamAcl.Create(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
                    s.WaitForConnection();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var args = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(ms.ToArray())) ?? [];
                    ui.BeginInvoke(() => ArgsReceived?.Invoke(args));
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "DSS-Instancia" };
        t.Start();
    }
}
