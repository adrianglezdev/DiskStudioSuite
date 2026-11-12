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

        System.Windows.Automation.AutomationProperties.SetName(MinButton, L.T("Minimizar"));
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, L.T("Maximizar"));
        System.Windows.Automation.AutomationProperties.SetName(CloseButton, L.T("Cerrar"));
        StateChanged += (_, _) => UpdateChrome();
        DpiChanged += (_, _) => UpdateChrome();
        Activated += (_, _) => CaptionButtons.Opacity = 1;
        Deactivated += (_, _) => CaptionButtons.Opacity = 0.5;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Se añade después del de WindowChrome: los ganchos se llaman del más reciente al más antiguo, así que este decide primero
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        // Sin WS_SYSMENU Windows deja de pintar sus propios botones, que se transparentaban bajo los de la app.
        // El menú del sistema se sigue abriendo con clic derecho en la franja y con Alt+Espacio (ver ChromeProc).
        const int GWL_STYLE = -16, WS_SYSMENU = 0x80000;
        SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) & ~WS_SYSMENU);
        UpdateChrome();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (msg == WM_DEVICECHANGE)
        {
            int ev = wParam.ToInt32();
            if (ev is 0x8000 or 0x8004 or 0x0007) { deviceTimer.Stop(); deviceTimer.Start(); }
            return IntPtr.Zero;
        }
        return ChromeProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // ───────────── Barra de título propia ─────────────
    const int ResizeBand = 4;      // franja de redimensionado que se respeta por encima de los botones
    int maxVisual;                 // 0 normal · 1 con el ratón encima · 2 pulsado
    bool maxPressed;

    void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void OnMaxRestore(object sender, RoutedEventArgs e) => ToggleMaximize();
    void OnCloseButton(object sender, RoutedEventArgs e) => Close();

    void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>Ajusta la franja superior al estado de la ventana.</summary>
    void UpdateChrome()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Tag = max ? "" : "";
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, L.T(max ? "Restaurar" : "Maximizar"));
        // Maximizada, Windows saca el marco de la ventana fuera de la pantalla: se compensa para que no se recorte el contenido
        var inset = max ? MaximizedInset() : default;
        Root.Margin = inset;
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = Root.RowDefinitions[0].Height.Value - chrome.ResizeBorderThickness.Top + inset.Top;
        SetMaxVisual(0);
    }

    /// <summary>Lo que sobresale la ventana maximizada del área de trabajo de su pantalla, en unidades de WPF.</summary>
    Thickness MaximizedInset()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var w) && GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* el más cercano */), ref mi))
        {
            int l = mi.rcWork.L - w.L, t = mi.rcWork.T - w.T, r = w.R - mi.rcWork.R, b = w.B - mi.rcWork.B;
            int limit = (int)(16 * dpi.DpiScaleX);
            if (l >= 0 && t >= 0 && r >= 0 && b >= 0 && l <= limit && t <= limit && r <= limit && b <= limit)
                return new Thickness(l / dpi.DpiScaleX, t / dpi.DpiScaleY, r / dpi.DpiScaleX, b / dpi.DpiScaleY);
        }
        // Si la medida no es fiable (ventana aún sin colocar), el grosor del marco según el sistema
        uint d = (uint)Math.Round(dpi.PixelsPerInchX);
        return new Thickness((GetSystemMetricsForDpi(32 /* SM_CXSIZEFRAME */, d) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, d)) / dpi.DpiScaleX);
    }

    /// <summary>
    /// El botón de maximizar se declara a Windows como tal (HTMAXBUTTON) para que Windows 11 muestre los diseños de acoplamiento
    /// al pasar el ratón. A cambio deja de recibir los eventos de ratón de WPF, así que aquí se dibuja y se pulsa a mano.
    /// </summary>
    IntPtr ChromeProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCLBUTTONDBLCLK = 0xA3;
        const int WM_MOUSEMOVE = 0x200, WM_NCMOUSELEAVE = 0x2A2, WM_MOUSELEAVE = 0x2A3;
        const int HTRIGHT = 11, HTTOP = 12, HTTOPRIGHT = 14, HTMAXBUTTON = 9;
        const int WM_SYSCOMMAND = 0x112, SC_KEYMENU = 0xF100;
        switch (msg)
        {
            case WM_SYSCOMMAND:
                // Alt+Espacio: menú del sistema (mover, tamaño, minimizar…), que Windows ya no abre solo
                if ((wParam.ToInt64() & 0xFFF0) != SC_KEYMENU || lParam.ToInt64() != ' ') break;
                handled = true;
                var dpi = VisualTreeHelper.GetDpi(this);
                var corner0 = Root.PointToScreen(new Point(0, Root.RowDefinitions[0].Height.Value));
                SystemCommands.ShowSystemMenu(this, new Point(corner0.X / dpi.DpiScaleX, corner0.Y / dpi.DpiScaleY));
                break;
            case WM_NCHITTEST:
                if (!CaptionButtons.IsVisible || PresentationSource.FromVisual(Root) == null) break;
                long l = lParam.ToInt64();
                var screen = new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                if (!Inside(CaptionButtons, screen)) break;
                if (WindowState == WindowState.Normal && ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
                {
                    // Los botones llegan hasta el borde: se deja una franja fina para seguir pudiendo redimensionar desde arriba y desde la esquina
                    var p = Root.PointFromScreen(screen);
                    bool top = p.Y < ResizeBand, right = p.X >= Root.ActualWidth - ResizeBand;
                    if (top || right)
                    {
                        handled = true;
                        bool corner = (top && p.X >= Root.ActualWidth - 3 * ResizeBand) || (right && p.Y < 3 * ResizeBand);
                        return new IntPtr(corner ? HTTOPRIGHT : top ? HTTOP : HTRIGHT);
                    }
                }
                if (Inside(MaxButton, screen)) { handled = true; return new IntPtr(HTMAXBUTTON); }
                break;
            case WM_NCMOUSEMOVE:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    SetMaxVisual(maxPressed ? 2 : 1);
                    var tme = new TRACKMOUSEEVENT { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x12 /* TME_LEAVE | TME_NONCLIENT */, hwndTrack = hwnd };
                    TrackMouseEvent(ref tme);
                }
                else { maxPressed = false; SetMaxVisual(0); }
                break;
            case WM_MOUSEMOVE or WM_NCMOUSELEAVE or WM_MOUSELEAVE:
                if (maxVisual != 0 || maxPressed) { maxPressed = false; SetMaxVisual(0); }
                break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK:
                if (wParam.ToInt32() != HTMAXBUTTON) break;
                handled = true; maxPressed = true; SetMaxVisual(2);
                break;
            case WM_NCLBUTTONUP:
                if (wParam.ToInt32() != HTMAXBUTTON) break;
                handled = true;
                bool click = maxPressed;
                maxPressed = false; SetMaxVisual(0);
                if (click) ToggleMaximize();
                break;
        }
        return IntPtr.Zero;
    }

    static bool Inside(FrameworkElement el, Point screen)
    {
        var p = el.PointFromScreen(screen);
        return p.X >= 0 && p.Y >= 0 && p.X < el.ActualWidth && p.Y < el.ActualHeight;
    }

    void SetMaxVisual(int state)
    {
        if (state == maxVisual) return;
        maxVisual = state;
        if (state == 0) MaxButton.ClearValue(BackgroundProperty);
        else MaxButton.SetResourceReference(BackgroundProperty, state == 2 ? "SubtleFillColorTertiaryBrush" : "SubtleFillColorSecondaryBrush");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

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
