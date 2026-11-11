using System.Windows;
using System.Windows.Threading;
using DiskStudio.Core;

namespace DiskStudio;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        L.Init(Environment.GetEnvironmentVariable("DSS_LANG") ?? Settings.Current.Language);
        L.Hook();
        ApplyTheme(Settings.Current.Theme);
        Brand.Hook();
        DispatcherUnhandledException += OnError;
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (Environment.GetEnvironmentVariable("DSS_ACCENTDUMP") is string dump)
        {
            var w = new Window { Width = 200, Height = 100, Content = new System.Windows.Controls.CheckBox { IsChecked = true } };
            w.Show();
            await Task.Delay(1500);
            Brand.Apply(Brand.IsDark(Settings.Current.Theme));
            DumpAccentKeys(dump);
            var cb = (System.Windows.Controls.CheckBox)w.Content;
            System.IO.File.AppendAllText(dump, "\nCHECKBOX: " + string.Join(", ", FindBrushes(cb)));
            Shutdown(); return;
        }
        bool snapshots = Environment.GetEnvironmentVariable("DSS_SNAP") != null;
        string splashSnap = Environment.GetEnvironmentVariable("DSS_SPLASHSNAP");
        bool background = e.Args.Contains("--segundo-plano");
        bool verbs = e.Args.Any(a => a is "--copiar" or "--hash" or "--espacio");
        if (!snapshots && splashSnap == null)
        {
            if (!SingleInstance.Claim(e.Args.Contains("--reinicio")))
            {
                // Ya hay una ventana abierta: pasarle la petición
                SingleInstance.Forward(e.Args.Length > 0 ? e.Args : ["--mostrar"]);
                Shutdown(); return;
            }
            SingleInstance.Listen(Dispatcher);
        }
        SplashWindow splash = null;
        if (!snapshots && !background && !verbs && !e.Args.Contains("--sin-presentacion"))
        {
            splash = new SplashWindow();
            splash.Show();
        }
        AppState.Rescan();               // se detectan los discos mientras se ve la presentación
        var main = new MainWindow();
        MainWindow = main;
        if (splash != null)
        {
            if (splashSnap != null) { await splash.CaptureFrames(splashSnap, 150, 700, 1400, 2600, 3400); Shutdown(); return; }
            await splash.RunAsync();
        }
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Loaded += (_, _) => Brand.Apply(Brand.IsDark(Settings.Current.Theme));
        DiskMonitor.Start();
        if (background)
        {
            new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
            TrayIcon.Ensure();
        }
        else
        {
            main.Show();
            main.Activate();
            if (Settings.Current.CloseToTray) TrayIcon.Ensure();
        }
        splash?.Close();
        if (verbs) main.HandleArgs(e.Args);
        if (snapshots) return;
        // Mantener al día las rutas del ejecutable (por si se ha movido)
        try { if (Settings.Current.ExplorerMenu) ShellIntegration.SetExplorerMenu(true); if (Settings.Current.StartWithWindows) ShellIntegration.SetAutostart(true); } catch { }
        if (Settings.Current.CheckUpdates && (Settings.Current.LastUpdateCheck is not DateTime last || (DateTime.Now - last).TotalHours > 20))
            _ = CheckUpdatesLater();
    }

    static async Task CheckUpdatesLater()
    {
        await Task.Delay(8000);
        try
        {
            var u = await Updates.Check();
            if (u != null) { Log.Info($"Hay una versión nueva de Disk Studio Suite: {u.Tag}"); Toast.Show("Hay una versión nueva", $"Disk Studio Suite {u.Tag} está disponible. Descárgala desde Ajustes.", Icons.Update); }
        }
        catch (Exception ex) { Log.Info("No se pudo comprobar si hay actualizaciones: " + ex.Message); }
    }

    /// <summary>Diagnóstico: lista los recursos del tema cuyo color es azulado (acento de Windows sin sustituir).</summary>
    static void DumpAccentKeys(string file)
    {
        var lines = new List<string>();
        void walk(ResourceDictionary d, string path)
        {
            foreach (var key in d.Keys)
            {
                object v;
                try { v = d[key]; } catch { continue; }
                System.Windows.Media.Color? c = v switch { System.Windows.Media.SolidColorBrush b => b.Color, System.Windows.Media.Color col => col, _ => null };
                if (c is { } x && x.B > x.R + 40 && x.B > 120) lines.Add($"{key}\t{x}\t{path}");
            }
            foreach (var m in d.MergedDictionaries) walk(m, path + "/" + (m.Source?.ToString() ?? "?"));
        }
        walk(Current.Resources, "app");
        System.IO.File.WriteAllLines(file, lines.Distinct().OrderBy(l => l));
    }

    static IEnumerable<string> FindBrushes(System.Windows.DependencyObject d)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is System.Windows.Controls.Border b && b.Background is System.Windows.Media.SolidColorBrush sb) yield return $"{b.Name}:{sb.Color}";
            if (c is System.Windows.Shapes.Shape s && s.Fill is System.Windows.Media.SolidColorBrush sf) yield return $"{s.Name}:{sf.Color}";
            foreach (var x in FindBrushes(c)) yield return x;
        }
    }

    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
        bool dark = Brand.IsDark(theme);
        Brand.Apply(dark);
        // El diccionario del tema nuevo se carga de forma diferida: volver a aplicar la marca cuando ya esté
        Current.Dispatcher.BeginInvoke(() => Brand.Apply(dark), DispatcherPriority.Loaded);
        Current.Dispatcher.BeginInvoke(() => Brand.Apply(dark), DispatcherPriority.ApplicationIdle);
    }

    void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Error inesperado: " + e.Exception.Message);
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.DataDir, "errores.log"), $"{DateTime.Now:s}\n{e.Exception}\n\n"); } catch { }
        _ = Dialogs.Error("Se produjo un error", e.Exception.Message);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Save();
        AutoTasks.Save();
        TrayIcon.Remove();
        base.OnExit(e);
    }
}
