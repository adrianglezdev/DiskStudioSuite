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
        Loaded += (_, _) => { Navigate("disks"); AppState.Rescan(); if (Environment.GetEnvironmentVariable("DSS_SNAP") is string s) _ = Snapshots(s); };
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
        "disks" => NavDisks, "explorer" => NavExplorer, "transfers" => NavTransfers, "sync" => NavSync, "clone" => NavClone,
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
            _ when sender == NavDisks => "disks", _ when sender == NavExplorer => "explorer", _ when sender == NavTransfers => "transfers",
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
                Key.D1 => "disks", Key.D2 => "explorer", Key.D3 => "transfers", Key.D4 => "sync", Key.D5 => "clone",
                Key.D6 => "format", Key.D7 => "tools", Key.D8 => "tasks", Key.D9 => "log", _ => null
            };
            if (key != null) { Navigate(key); e.Handled = true; }
        }
    }

    async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
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
    bool forceClose;

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
            DialogTitle.Text = title;
            DialogText.Text = text;
            DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            DialogIcon.Text = glyph ?? "";
            DialogIcon.Foreground = glyphBrush ?? (Brush)FindResource("AccentTextFillColorPrimaryBrush");
            DialogIcon.Visibility = glyph == null ? Visibility.Collapsed : Visibility.Visible;
            DialogExtra.Content = extra;
            DialogButtons.Children.Clear();
            DialogButtons.Columns = buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button
                {
                    Content = buttons[i],
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
