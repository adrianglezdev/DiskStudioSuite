using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DiskStudio.Core;

namespace DiskStudio;

/// <summary>Pantalla de carga: logo animado sobre fondo sólido durante unos segundos mientras se detectan los discos.</summary>
public class SplashWindow : Window
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(3600);
    readonly Grid root = new();
    readonly TextBlock status = new();
    readonly Border fill = new();
    readonly string[] messages = ["Iniciando…", "Detectando discos…", "Leyendo particiones…", "Comprobando la salud de los discos…", "Preparando la interfaz…"];

    public SplashWindow()
    {
        Title = "Disk Studio Suite";
        Width = 720; Height = 500;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = false;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));

        var night = new SolidColorBrush(Brand.Night);
        var card = new Border { CornerRadius = new CornerRadius(18), Background = night, ClipToBounds = true, Child = root };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.45 };
        var outer = new Grid { Margin = new Thickness(18) };
        outer.Children.Add(card);
        Content = outer;

        // Resplandor que "respira" detrás del logo
        var glow = new Ellipse
        {
            Width = 340, Height = 340, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -14, 0, 0),
            Fill = new RadialGradientBrush(Color.FromArgb(150, Brand.Mint.R, Brand.Mint.G, Brand.Mint.B), Color.FromArgb(0, Brand.Teal.R, Brand.Teal.G, Brand.Teal.B)),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.9, 0.9), Opacity = 0
        };
        root.Children.Add(glow);

        // Rejilla de puntos sutil (evoca los cuadraditos del logo)
        root.Children.Add(Dots());

        // Órbitas con puntos de colores de la marca
        var orbit = new Canvas { Width = 270, Height = 270, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 21, 0, 0), Opacity = 0 };
        var ring = new Ellipse { Width = 260, Height = 260, Stroke = new SolidColorBrush(Color.FromArgb(40, Brand.Aqua.R, Brand.Aqua.G, Brand.Aqua.B)), StrokeThickness = 1, StrokeDashArray = [2, 6] };
        Canvas.SetLeft(ring, 5); Canvas.SetTop(ring, 5);
        orbit.Children.Add(ring);
        var orbiters = new List<(FrameworkElement el, double speed, double phase)>();
        foreach (var (c, size, speed, phase) in new[] { (Brand.Aqua, 10.0, 1.0, 0.0), (Brand.Lime, 8.0, 1.0, 180.0), (Brand.Mint, 6.0, -1.6, 90.0) })
        {
            var holder = new Canvas { Width = 270, Height = 270, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(phase) };
            var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(c) };
            dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = c, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 };
            Canvas.SetLeft(dot, 135 - size / 2); Canvas.SetTop(dot, 5 - size / 2 + 0.5);
            holder.Children.Add(dot);
            orbit.Children.Add(holder);
            orbiters.Add((holder, speed, phase));
        }
        root.Children.Add(orbit);

        // Logo
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo-512.png")),
            Width = 196, Height = 196, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 58, 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5), Opacity = 0
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var logoScale = new ScaleTransform(0.6, 0.6);
        var logoRot = new RotateTransform(-12);
        var logoMove = new TranslateTransform(0, 0);
        logo.RenderTransform = new TransformGroup { Children = { logoScale, logoRot, logoMove } };
        root.Children.Add(logo);

        // Nombre y lema
        var title = new TextBlock { Text = "Disk Studio Suite", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        var tagline = new TextBlock { Text = L.T("Discos  ·  Copias  ·  Clonado  ·  Formato"), FontSize = 13, Foreground = new SolidColorBrush(Brand.Mint), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 312, 0, 0), Opacity = 0, RenderTransform = new TranslateTransform(0, 14) };
        texts.Children.Add(title); texts.Children.Add(tagline);
        root.Children.Add(texts);

        // Barra de progreso con el degradado de la marca
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(70, 0, 70, 40) };
        fill.Height = 4; fill.CornerRadius = new CornerRadius(2); fill.HorizontalAlignment = HorizontalAlignment.Left; fill.Width = 0;
        fill.Background = new LinearGradientBrush(new GradientStopCollection { new(Brand.Teal, 0), new(Brand.Aqua, 0.6), new(Brand.Lime, 1) }, 0);
        fill.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Brand.Aqua, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.7 };
        track.Child = fill;
        root.Children.Add(track);
        status.FontSize = 12; status.Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)); status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Bottom; status.Margin = new Thickness(0, 0, 0, 14); status.Text = L.T(messages[0]);
        root.Children.Add(status);
        var ver = new TextBlock { Text = "v" + typeof(SplashWindow).Assembly.GetName().Version?.ToString(3), FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 14) };
        root.Children.Add(ver);

        Loaded += (_, _) =>
        {
            var back = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var cubic = new CubicEase { EasingMode = EasingMode.EaseOut };
            DoubleAnimation da(double to, double ms, double delay = 0, IEasingFunction e = null) => new(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = e };

            // Entrada del logo
            logo.BeginAnimation(OpacityProperty, da(1, 600, 100));
            logoScale.BeginAnimation(ScaleTransform.ScaleXProperty, da(1, 900, 100, back));
            logoScale.BeginAnimation(ScaleTransform.ScaleYProperty, da(1, 900, 100, back));
            logoRot.BeginAnimation(RotateTransform.AngleProperty, da(0, 1000, 100, cubic));
            // Flotación suave continua
            logoMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-5, 5, TimeSpan.FromMilliseconds(1300)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(1000), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

            // Resplandor que respira
            glow.BeginAnimation(OpacityProperty, da(1, 800, 200));
            var gs = (ScaleTransform)glow.RenderTransform;
            var pulse = new DoubleAnimation(0.85, 1.08, TimeSpan.FromMilliseconds(1100)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            gs.BeginAnimation(ScaleTransform.ScaleXProperty, pulse); gs.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

            // Órbitas
            orbit.BeginAnimation(OpacityProperty, da(1, 700, 450));
            foreach (var (el, speed, phase) in orbiters)
                ((RotateTransform)el.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(phase, phase + 360 * Math.Sign(speed), TimeSpan.FromSeconds(2.6 / Math.Abs(speed))) { RepeatBehavior = RepeatBehavior.Forever });
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            ring.RenderTransform = new RotateTransform();
            ((RotateTransform)ring.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever });

            // Textos
            texts.BeginAnimation(OpacityProperty, da(1, 600, 650));
            ((TranslateTransform)texts.RenderTransform).BeginAnimation(TranslateTransform.YProperty, da(0, 700, 650, cubic));

            // Progreso
            double w = track.ActualWidth;
            fill.BeginAnimation(WidthProperty, new DoubleAnimation(0, w, Duration - TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(200), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });

            // Mensajes de estado
            int i = 0;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(720) };
            t.Tick += (_, _) =>
            {
                if (++i < messages.Length) status.Text = L.T(messages[i]);
                else t.Stop();
            };
            t.Start();
            AppState.Changed += OnDisks;
        };
        Closed += (_, _) => AppState.Changed -= OnDisks;
    }

    void OnDisks()
    {
        int n = AppState.Disks.Count;
        if (n > 0) status.Text = L.T($"{n} discos detectados · {AppState.Volumes.Count(v => v.Ready)} volúmenes");
    }

    static UIElement Dots()
    {
        var c = new Canvas { IsHitTestVisible = false, Opacity = 0.5 };
        var rnd = new Random(7);
        Color[] cols = [Brand.Aqua, Brand.Lime, Brand.Mint, Brand.Teal];
        for (int k = 0; k < 26; k++)
        {
            double s = rnd.Next(3, 7);
            var r = new Rectangle { Width = s, Height = s, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb((byte)rnd.Next(40, 120), cols[k % 4].R, cols[k % 4].G, cols[k % 4].B)) };
            bool left = k % 2 == 0;
            Canvas.SetLeft(r, left ? rnd.Next(20, 170) : rnd.Next(500, 660));
            Canvas.SetTop(r, rnd.Next(20, 380));
            c.Children.Add(r);
            var tw = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(rnd.Next(700, 1600))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(rnd.Next(0, 900)) };
            r.BeginAnimation(OpacityProperty, tw);
        }
        return c;
    }

    /// <summary>Muestra la pantalla de carga el tiempo indicado y la desvanece.</summary>
    public async Task RunAsync()
    {
        await Task.Delay(Duration);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(280));
        var tcs = new TaskCompletionSource();
        fade.Completed += (_, _) => tcs.TrySetResult();
        BeginAnimation(OpacityProperty, fade);
        await tcs.Task;
    }

    /// <summary>Modo de prueba: guarda fotogramas de la animación.</summary>
    public async Task CaptureFrames(string dir, params int[] ms)
    {
        System.IO.Directory.CreateDirectory(dir);
        var start = DateTime.Now;
        foreach (int t in ms)
        {
            var wait = TimeSpan.FromMilliseconds(t) - (DateTime.Now - start);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            var el = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, ActualWidth, ActualHeight));
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"splash_{t:D4}.png"));
            enc.Save(fs);
        }
    }
}
