using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DiskStudio.Core;
using DiskStudio.Views;

namespace DiskStudio;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; }
    readonly Dictionary<string, FrameworkElement> pages = [];
    DispatcherTimer deviceTimer;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        if (Native.IsAdmin) { AdminButton.Visibility = Visibility.Collapsed; AdminBadge.Visibility = Visibility.Visible; Title = "Disk Studio Suite (Administrador)"; }

        deviceTimer = AppState.Debounce(TimeSpan.FromMilliseconds(1200), () => AppState.Rescan());
        Toast.Requested += ShowToast;
        TransferQueue.StateChanged += UpdateQueueMini;
        AutoTasks.Hook();
        Loaded += (_, _) =>
        {
            if (current == null) Navigate("disks");
            AppState.Rescan();
            L.Apply(this);
            if (Environment.GetEnvironmentVariable("DSS_SNAP") is string s) _ = Snapshots(s);
        };
        if (L.English)
        {
            Title = L.T(Title);
            var lt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            lt.Tick += (_, _) => { if (!IsVisible) return; if (Host.Content is DependencyObject d) L.Apply(d); if (DialogLayer.Visibility == Visibility.Visible) L.Apply(DialogCard); L.Apply(QueueMini); };
            lt.Start();
        }
        DiskMonitor.Alert += (title, text, severe) => Toast.Show(title, text, severe ? Icons.Error : Icons.Warning);
        SingleInstance.ArgsReceived += HandleArgs;
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (msg == WM_DEVICECHANGE)
        {
            int ev = wParam.ToInt32();
            if (ev is 0x8000 or 0x8004 or 0x0007) { deviceTimer.Stop(); deviceTimer.Start(); }
        }
        return IntPtr.Zero;
    }

    // ───────────── Navegación ─────────────
    static readonly Dictionary<string, Func<FrameworkElement>> factories = new()
    {
        ["disks"] = () => new DisksView(),
        ["partitions"] = () => new PartitionsView(),
        ["boot"] = () => new BootUsbView(),
        ["explorer"] = () => new ExplorerView(),
        ["transfers"] = () => new TransfersView(),
        ["sync"] = () => new SyncView(),
        ["clone"] = () => new CloneView(),
        ["format"] = () => new FormatView(),
        ["tools"] = () => new ToolsView(),
        ["tasks"] = () => new TasksView(),
        ["log"] = () => new LogView(),
        ["settings"] = () => new SettingsView(),
    };

    RadioButton NavFor(string key) => key switch
    {
        "disks" => NavDisks, "partitions" => NavPartitions, "boot" => NavBoot, "explorer" => NavExplorer, "transfers" => NavTransfers, "sync" => NavSync, "clone" => NavClone,
        "format" => NavFormat, "tools" => NavTools, "tasks" => NavTasks, "log" => NavLog, "settings" => NavSettings, _ => null
    };

    string current;
    bool navigating;

    public T Navigate<T>(string key) where T : FrameworkElement => (T)Navigate(key);

    public FrameworkElement Navigate(string key)
    {
        if (!pages.TryGetValue(key, out var page)) pages[key] = page = factories[key]();
        if (current != key)
        {
            current = key;
            navigating = true;
            var nav = NavFor(key);
            if (nav != null) nav.IsChecked = true;
            navigating = false;
            Host.Content = page;
            L.Apply(page);
            page.Opacity = 0;
            var tt = new TranslateTransform(0, 12);
            page.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
        return page;
    }

    void OnNav(object sender, RoutedEventArgs e)
    {
        if (navigating || !IsLoaded) return;
        string key = sender switch
        {
            _ when sender == NavDisks => "disks", _ when sender == NavPartitions => "partitions", _ when sender == NavBoot => "boot", _ when sender == NavExplorer => "explorer", _ when sender == NavTransfers => "transfers",
            _ when sender == NavSync => "sync", _ when sender == NavClone => "clone", _ when sender == NavFormat => "format", _ when sender == NavTools => "tools",
            _ when sender == NavTasks => "tasks", _ when sender == NavLog => "log", _ => "settings"
        };
        Navigate(key);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (DialogLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape && dialogCancelIndex >= 0) { CloseDialog(dialogCancelIndex); e.Handled = true; }
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            string key = e.Key switch
            {
                Key.D1 => "disks", Key.D2 => "partitions", Key.D3 => "explorer", Key.D4 => "transfers", Key.D5 => "sync",
                Key.D6 => "clone", Key.D7 => "format", Key.D8 => "boot", Key.D9 => "tools", Key.D0 => "tasks", _ => null
            };
            if (key != null) { Navigate(key); e.Handled = true; }
        }
    }

    async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Settings.Current.CloseToTray && !exiting && !forceClose)
        {
            e.Cancel = true;
            if (pages.TryGetValue("explorer", out var ex0)) ((ExplorerView)ex0).SaveState();
            Settings.Save();
            HideToTray();
            return;
        }
        if (TransferQueue.PendingCount > 0 && !forceClose)
        {
            e.Cancel = true;
            if (await Dialogs.Confirm("Hay operaciones en curso", $"Quedan {TransferQueue.PendingCount} trabajos en la cola. Si cierras, se cancelarán.", "Cancelar y salir", danger: true))
            {
                foreach (var j in TransferQueue.Jobs) j.Cancel();
                forceClose = true;
                Close();
            }
        }
        else
        {
            if (pages.TryGetValue("explorer", out var ex)) ((ExplorerView)ex).SaveState();
            Settings.Save();
        }
    }
    bool forceClose, exiting;

    // ───────────── Segundo plano ─────────────
    public void HideToTray()
    {
        TrayIcon.Ensure();
        Hide();
        if (!Settings.Current.TrayHintShown)
        {
            Settings.Current.TrayHintShown = true; Settings.Save();
            TrayIcon.Current?.Balloon("Disk Studio Suite", L.T("Sigue funcionando en segundo plano: vigila tus discos y hace las copias programadas. Haz clic en el icono para abrirlo."));
        }
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    /// <summary>Salir de verdad (desde el icono de la bandeja).</summary>
    public void ExitApp()
    {
        exiting = true;
        if (!IsVisible) Show();
        Close();
    }

    // ───────────── Argumentos (menú del Explorador o segunda instancia) ─────────────
    readonly List<string> copyBatch = [], hashBatch = [];
    DispatcherTimer batchTimer;

    public void HandleArgs(string[] args)
    {
        string verb = null;
        bool any = false;
        foreach (var a in args)
        {
            if (a is "--copiar" or "--hash" or "--espacio") { verb = a; continue; }
            if (a.StartsWith("--")) continue;
            any = true;
            switch (verb)
            {
                case "--copiar": copyBatch.Add(a); break;
                case "--hash": hashBatch.Add(a); break;
                case "--espacio": ShowFromTray(); Navigate<ToolsView>("tools").Open("space", a); break;
            }
        }
        if (copyBatch.Count + hashBatch.Count > 0)
        {
            // El Explorador abre una instancia por elemento seleccionado: se agrupan los que llegan juntos
            batchTimer ??= AppState.Debounce(TimeSpan.FromMilliseconds(700), FlushBatch);
            batchTimer.Stop(); batchTimer.Start();
        }
        else if (!any && !args.Contains("--segundo-plano")) ShowFromTray();
    }

    async void FlushBatch()
    {
        ShowFromTray();
        if (hashBatch.Count > 0)
        {
            var files = hashBatch.Distinct().ToArray(); hashBatch.Clear();
            Navigate<ToolsView>("tools").Open("hash", System.IO.Path.GetDirectoryName(files[0]), files);
        }
        if (copyBatch.Count > 0)
        {
            var items = copyBatch.Distinct().ToList(); copyBatch.Clear();
            string dst = Dialogs.PickFolder(null, L.T("Copiar a…"));
            if (dst == null) return;
            if (items.Any(i => FileOps.IsInside(dst, i))) { await Dialogs.Error("Destino no válido", "No puedes copiar una carpeta dentro de sí misma."); return; }
            TransferQueue.Enqueue(FileOps.CopyJob(items, dst, false));
            Navigate("transfers");
        }
    }

    async void OnAdmin(object sender, RoutedEventArgs e)
    {
        if (TransferQueue.PendingCount > 0) { await Dialogs.Info("Operaciones en curso", "Espera a que termine la cola antes de reiniciar como administrador."); return; }
        if (pages.TryGetValue("explorer", out var ex)) ((ExplorerView)ex).SaveState();
        Settings.Save();
        if (!Native.RestartAsAdmin()) Log.Warn("Reinicio como administrador cancelado.");
    }

    void OnQueueMini(object sender, MouseButtonEventArgs e) => Navigate("transfers");

    void UpdateQueueMini()
    {
        var a = TransferQueue.Active;
        int pending = TransferQueue.PendingCount;
        QueueMini.Visibility = a != null ? Visibility.Visible : Visibility.Collapsed;
        if (a == null) return;
        QueueMiniTitle.Text = a.Title;
        QueueMiniBar.IsIndeterminate = a.Indeterminate;
        QueueMiniBar.Value = a.Progress;
        QueueMiniText.Text = (a.State == JobState.Paused ? "En pausa · " : $"{a.Progress:P0} · ") + (a.SpeedText != "" ? a.SpeedText : a.Stats) + (pending > 1 ? $" · +{pending - 1} en cola" : "");
    }

    // ───────────── Notificaciones ─────────────
    void ShowToast(string title, string text, string glyph)
    {
        if (!IsVisible) { TrayIcon.Current?.Balloon(title, text, glyph is Icons.Error or Icons.Warning); return; }
        var accent = glyph == Icons.Error ? (Brush)FindResource("BadBrush") : glyph == Icons.Warning ? (Brush)FindResource("CautionBrush")
                   : glyph == Icons.Success ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("BrandBrush");
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Background = (Brush)TryFindResource("SolidBackgroundFillColorBaseBrush") ?? Brushes.White,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 8, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.25 },
            Opacity = 0,
            RenderTransform = new TranslateTransform(40, 0),
            Cursor = Cursors.Hand
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = accent, VerticalAlignment = VerticalAlignment.Top };
        tile.Child = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), Foreground = Brushes.White, FontSize = 15 };
        g.Children.Add(tile);
        var sp = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextFillColorPrimaryBrush") });
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") });
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        card.Child = g;
        Toasts.Children.Add(card);
        while (Toasts.Children.Count > 4) Toasts.Children.RemoveAt(0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        void close()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => Toasts.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fade);
        }
        card.MouseLeftButtonUp += (_, _) => close();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        t.Tick += (_, _) => { t.Stop(); close(); };
        t.Start();
    }

    // ───────────── Diálogos ─────────────
    TaskCompletionSource<int> dialogTcs;
    int dialogCancelIndex = -1;
    readonly SemaphoreSlim dialogLock = new(1, 1);

    public async Task<int> ShowDialog(string title, string text, string glyph, Brush glyphBrush, string[] buttons, int primary, bool danger, UIElement extra, int cancelIndex)
    {
        await dialogLock.WaitAsync();
        try
        {
            DialogTitle.Text = L.T(title);
            DialogText.Text = L.T(text);
            DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            DialogIcon.Text = glyph ?? "";
            DialogIcon.Foreground = glyphBrush ?? (Brush)FindResource("AccentTextFillColorPrimaryBrush");
            DialogIcon.Visibility = glyph == null ? Visibility.Collapsed : Visibility.Visible;
            DialogExtra.Content = extra;
            if (extra != null) L.Apply(extra);
            if (!IsVisible) ShowFromTray();
            DialogButtons.Children.Clear();
            DialogButtons.Columns = buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button
                {
                    Content = L.T(buttons[i]),
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                    Style = (Style)FindResource(i == primary ? (danger ? "DangerButton" : "PrimaryButton") : "SecondaryButton"),
                    IsDefault = i == primary
                };
                b.Click += (_, _) => CloseDialog(idx);
                DialogButtons.Children.Add(b);
            }
            dialogCancelIndex = cancelIndex;
            dialogTcs = new TaskCompletionSource<int>();
            DialogLayer.Visibility = Visibility.Visible;
            DialogCard.Opacity = 0;
            var st = new ScaleTransform(0.96, 0.96);
            DialogCard.RenderTransformOrigin = new Point(0.5, 0.5);
            DialogCard.RenderTransform = st;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DialogCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            if (extra is FrameworkElement fe) fe.Loaded += (_, _) => FocusFirst(fe);
            else _ = Dispatcher.BeginInvoke(() => (DialogButtons.Children[primary] as Button)?.Focus(), DispatcherPriority.Input);
            return await dialogTcs.Task;
        }
        finally
        {
            DialogLayer.Visibility = Visibility.Collapsed;
            DialogExtra.Content = null;
            dialogLock.Release();
        }
    }

    static void FocusFirst(DependencyObject d)
    {
        if (d is TextBox tb) { tb.Focus(); tb.SelectAll(); return; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) FocusFirst(VisualTreeHelper.GetChild(d, i));
    }

    void CloseDialog(int i) => dialogTcs?.TrySetResult(i);

    /// <summary>Cierra el diálogo abierto desde el código (por ejemplo, al terminar una descarga).</summary>
    public void CloseDialogFromCode(int result) => CloseDialog(result);

    /// <summary>Modo de prueba: DSS_SNAP="carpeta;pagina1,pagina2" guarda capturas de cada página y cierra.</summary>
    async Task Snapshots(string spec)
    {
        var parts = spec.Split(';');
        string dir = parts[0];
        string[] keys = parts.Length > 1 ? parts[1].Split(',') : [.. factories.Keys];
        System.IO.Directory.CreateDirectory(dir);
        await Task.Delay(4000);
        foreach (var k in keys)
        {
            string key = k, action = null;
            if (k.Contains(':')) { key = k.Split(':')[0]; action = k.Split(':')[1]; }
            var page = Navigate(key);
            await Task.Delay(900);
            if (action != null && page is ISnapshotDemo demo) { await demo.Demo(action); await Task.Delay(1500); }
            var root = (FrameworkElement)Content;
            var bg = (Brush)FindResource("SolidBackgroundFillColorBaseBrush");
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(bg, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, k.Replace(':', '_') + ".png"));
            enc.Save(fs);
        }
        forceClose = true;
        Application.Current.Shutdown();
    }
}

/// <summary>Páginas que pueden preparar un estado de demostración para las capturas de prueba.</summary>
public interface ISnapshotDemo
{
    Task Demo(string action);
}
