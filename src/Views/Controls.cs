using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskStudio.Core;

namespace DiskStudio.Views;

static class Brushes2
{
    public static Brush Res(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;
    public static Brush Track => Res("ControlStrokeColorDefaultBrush", new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)));
    public static Brush Accent => Res("AccentFillColorDefaultBrush", new SolidColorBrush(Color.FromRgb(0x20, 0x87, 0x6C)));
    public static Brush Text => Res("TextFillColorPrimaryBrush", System.Windows.Media.Brushes.Black);
    public static Brush Text2 => Res("TextFillColorSecondaryBrush", System.Windows.Media.Brushes.Gray);
    public static readonly Brush Good = Frozen(0x16, 0xA3, 0x4A), Caution = Frozen(0xF5, 0x9E, 0x0B), Bad = Frozen(0xDC, 0x26, 0x26);
    public static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    public static Brush ForUsage(double v) => v > 0.9 ? Bad : v > 0.75 ? Caution : Accent;
    public static Brush ForHealth(HealthLevel h) => h switch { HealthLevel.Good => Good, HealthLevel.Caution => Caution, HealthLevel.Bad => Bad, _ => Text2 };
}

/// <summary>Barra de uso redondeada que cambia de color según ocupación.</summary>
public class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public UsageBar() { Height = 6; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2;
        dc.DrawRoundedRectangle(Brushes2.Track, null, new Rect(0, 0, w, h), r, r);
        double v = Math.Clamp(Value, 0, 1);
        if (v > 0) dc.DrawRoundedRectangle(Fill ?? Brushes2.ForUsage(v), null, new Rect(0, 0, Math.Max(h, w * v), h), r, r);
    }
}

/// <summary>Anillo de progreso (salud, uso…).</summary>
public class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Thickness { get; set; } = 7;

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight), t = Thickness;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (s - t) / 2;
        dc.DrawEllipse(null, new Pen(Brushes2.Track, t), c, r, r);
        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0) return;
        var pen = new Pen(Stroke ?? Brushes2.Accent, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Mapa de particiones de un disco físico (como el Administrador de discos).</summary>
public class PartitionMap : Grid
{
    public void SetDisk(PhysicalDisk d)
    {
        Children.Clear(); ColumnDefinitions.Clear();
        if (d.Size <= 0 || d.Partitions.Count == 0) return;
        var segments = new List<(long size, PartitionInfo p)>();
        long pos = 0;
        foreach (var p in d.Partitions)
        {
            if (p.Offset - pos > d.Size / 200) segments.Add((p.Offset - pos, null));
            segments.Add((p.Size, p));
            pos = p.Offset + p.Size;
        }
        if (d.Size - pos > d.Size / 200) segments.Add((d.Size - pos, null));

        int col = 0;
        foreach (var (size, p) in segments)
        {
            double w = Math.Max(size / (double)d.Size, 0.06);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            bool hasVol = p != null && !string.IsNullOrEmpty(p.Letter);
            // Sin bordes: el tipo de partición se distingue por el tinte del fondo, el uso por una barra interior
            // y el espacio sin asignar por un rayado diagonal.
            Brush bg;
            if (p == null) bg = Stripes();
            else if (hasVol) bg = Tint(Brushes2.Accent, 46);
            else bg = (Brush)Application.Current.TryFindResource("SubtleFillColorSecondaryBrush");
            var border = new Border
            {
                Margin = new Thickness(col == 0 ? 0 : 3, 0, 0, 0),
                CornerRadius = new CornerRadius(8),
                Height = 50,
                Background = bg,
                ClipToBounds = true,
                ToolTip = p == null ? $"Sin asignar · {Fmt.Size(size)}"
                        : $"{p.Display}\n{(string.IsNullOrEmpty(p.FileSystem) ? p.Type : p.FileSystem)} · {Fmt.Size(p.Size)}" + (hasVol ? $"\nLibre: {Fmt.Size(p.Free)}" : "")
            };
            var grid = new Grid();
            var sp = new StackPanel { Margin = new Thickness(9, 5, 6, hasVol ? 9 : 5), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = p == null ? "Sin asignar" : p.Display, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = p == null ? Brushes2.Text2 : Brushes2.Text });
            sp.Children.Add(new TextBlock { Text = Fmt.Size(size) + (p != null && !string.IsNullOrEmpty(p.FileSystem) ? " · " + p.FileSystem : ""), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes2.Text2 });
            grid.Children.Add(sp);
            if (hasVol && p.Size > 0)
            {
                double used = Math.Clamp((p.Size - p.Free) / (double)p.Size, 0, 1);
                grid.Children.Add(new UsageBar { Value = used, Height = 4, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(9, 0, 9, 6) });
            }
            border.Child = grid;
            SetColumn(border, col++);
            Children.Add(border);
        }
    }

    static Brush Tint(Brush b, byte alpha)
    {
        var c = (b as SolidColorBrush)?.Color ?? Color.FromRgb(0x20, 0x87, 0x6C);
        var r = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); r.Freeze(); return r;
    }

    static Brush Stripes()
    {
        var c = ((Brushes2.Text2 as SolidColorBrush)?.Color ?? Colors.Gray);
        var line = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B));
        var g = new GeometryDrawing(line, null, Geometry.Parse("M0,8 L8,0 L8,3 L3,8 Z M0,0 L0,3 L3,0 Z"));
        var brush = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}

/// <summary>Gráfica de velocidad de transferencia.</summary>
public class SpeedGraph : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(IList<double>), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IList<double> Data { get => (IList<double>)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public void Redraw() => InvalidateVisual();

    public SpeedGraph()
    {
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TransferJob o) o.PropertyChanged -= OnJob;
            if (e.NewValue is TransferJob n) { n.PropertyChanged += OnJob; Data = n.SpeedHistory; }
        };
    }

    void OnJob(object s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferJob.SpeedHistory)) Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var data = Data;
        if (data == null || data.Count < 2) return;
        var pts = data.ToArray();
        double max = Math.Max(1, pts.Max()) * 1.15;
        int n = Math.Max(pts.Length, 60);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double x0 = w - (pts.Length - 1) * w / (n - 1);
            ctx.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < pts.Length; i++)
                ctx.LineTo(new Point(x0 + i * w / (n - 1), h - pts[i] / max * h), true, true);
            ctx.LineTo(new Point(w, h), true, false);
        }
        g.Freeze();
        var accent = (Brushes2.Accent as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(110, accent.R, accent.G, accent.B), Color.FromArgb(10, accent.R, accent.G, accent.B), 90);
        dc.DrawGeometry(fill, new Pen(Brushes2.Accent, 1.5), g);
        var ft = new FormattedText("máx. " + Fmt.Speed(pts.Max()), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes2.Text2, 1.0);
        dc.DrawText(ft, new Point(4, 2));
    }
}

/// <summary>Treemap "squarified" del analizador de espacio.</summary>
public class Treemap : FrameworkElement
{
    SpaceNode root;
    readonly List<(Rect r, SpaceNode n)> hits = [];
    public event Action<SpaceNode> NodeClicked;
    public event Action<SpaceNode> NodeHovered;
    readonly ToolTip tip = new();

    static readonly Color[] Palette =
    [
        // Paleta de marca y tonos derivados
        Color.FromRgb(0x20, 0x87, 0x6C), Color.FromRgb(0x6B, 0xC3, 0x99), Color.FromRgb(0x64, 0x74, 0x71), Color.FromRgb(0x1B, 0xB8, 0x98),
        Color.FromRgb(0xA9, 0xB5, 0x1E), Color.FromRgb(0x14, 0x5E, 0x4C), Color.FromRgb(0x3F, 0xA4, 0x86), Color.FromRgb(0x8A, 0x9A, 0x97),
        Color.FromRgb(0x17, 0xA8, 0x8F), Color.FromRgb(0x4E, 0x5B, 0x59)
    ];
    static readonly Dictionary<string, Color> extColors = new(StringComparer.OrdinalIgnoreCase);

    public Treemap() { ToolTip = tip; ToolTipService.SetInitialShowDelay(this, 200); }

    public void SetRoot(SpaceNode n) { root = n; InvalidateVisual(); }

    static Color ColorFor(SpaceNode n, int depth)
    {
        if (n.IsDir) return Palette[Math.Abs(n.Name.GetHashCode()) % Palette.Length];
        string ext = System.IO.Path.GetExtension(n.Name);
        if (!extColors.TryGetValue(ext, out var c)) extColors[ext] = c = Palette[extColors.Count % Palette.Length];
        return c;
    }

    protected override void OnRender(DrawingContext dc)
    {
        hits.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (root == null || root.Size <= 0) return;
        Layout(dc, root.Children.Where(c => c.Size > 0).ToList(), new Rect(0, 0, ActualWidth, ActualHeight), 0, null);
    }

    void Layout(DrawingContext dc, List<SpaceNode> items, Rect area, int depth, Color? baseColor)
    {
        if (items.Count == 0 || area.Width < 2 || area.Height < 2) return;
        double total = items.Sum(i => (double)i.Size);
        var rects = Squarify(items.Select(i => i.Size / total * area.Width * area.Height).ToList(), area);
        for (int i = 0; i < items.Count; i++)
        {
            var n = items[i]; var r = rects[i];
            if (r.Width < 1 || r.Height < 1) continue;
            var col = baseColor ?? ColorFor(n, depth);
            byte a = (byte)Math.Max(70, 230 - depth * 45);
            var rr = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(a, col.R, col.G, col.B)), null, rr, 3, 3);
            hits.Add((r, n));
            bool label = r.Width > 60 && r.Height > 22;
            if (label)
            {
                var ft = new FormattedText(n.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, Brushes.White, 1.0)
                { MaxTextWidth = r.Width - 8, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(ft, new Point(r.X + 5, r.Y + 3));
                if (r.Height > 38)
                {
                    var f2 = new FormattedText(Fmt.Size(n.Size), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.0)
                    { MaxTextWidth = r.Width - 8, MaxLineCount = 1 };
                    dc.DrawText(f2, new Point(r.X + 5, r.Y + 17));
                }
            }
            if (n.IsDir && depth < 2 && r.Width > 50 && r.Height > 50)
            {
                var inner = new Rect(r.X + 3, r.Y + (label ? 34 : 3), Math.Max(0, r.Width - 6), Math.Max(0, r.Height - (label ? 37 : 6)));
                Layout(dc, n.Children.Where(c => c.Size > 0).Take(200).ToList(), inner, depth + 1, col);
            }
        }
    }

    static List<Rect> Squarify(List<double> areas, Rect r)
    {
        var result = new List<Rect>();
        int i = 0;
        while (i < areas.Count)
        {
            double side = Math.Min(r.Width, r.Height);
            if (side <= 0) { while (result.Count < areas.Count) result.Add(Rect.Empty); break; }
            int j = i; double sum = 0, best = double.MaxValue;
            while (j < areas.Count)
            {
                double s2 = sum + areas[j];
                double worst = Worst(areas, i, j, s2, side);
                if (worst > best) break;
                best = worst; sum = s2; j++;
            }
            double thick = sum / side;
            double off = 0;
            bool horizontal = r.Width >= r.Height;
            for (int k = i; k < j; k++)
            {
                double len = areas[k] / thick;
                result.Add(horizontal ? new Rect(r.X, r.Y + off, thick, len) : new Rect(r.X + off, r.Y, len, thick));
                off += len;
            }
            r = horizontal ? new Rect(r.X + thick, r.Y, Math.Max(0, r.Width - thick), r.Height) : new Rect(r.X, r.Y + thick, r.Width, Math.Max(0, r.Height - thick));
            i = j;
        }
        return result;
    }

    static double Worst(List<double> a, int from, int to, double sum, double side)
    {
        double max = 0, min = double.MaxValue;
        for (int k = from; k <= to; k++) { max = Math.Max(max, a[k]); min = Math.Min(min, a[k]); }
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    SpaceNode HitTest(Point p)
    {
        SpaceNode found = null;
        foreach (var (r, n) in hits) if (r.Contains(p)) found = n; // el último es el más profundo
        return found;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var n = HitTest(e.GetPosition(this));
        if (n != null) { tip.Content = $"{n.Path}\n{Fmt.Size(n.Size)}" + (n.IsDir ? $" · {n.Files:N0} archivos" : ""); NodeHovered?.Invoke(n); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var n = HitTest(e.GetPosition(this));
        if (n == null) return;
        // Subir al hijo directo del nodo raíz actual que contiene el elemento
        var x = n;
        while (x.Parent != null && x.Parent != root) x = x.Parent;
        NodeClicked?.Invoke(x.IsDir ? x : n);
    }
}

/// <summary>Mapa de bloques del análisis de superficie.</summary>
public class SurfaceMap : FrameworkElement
{
    public List<DiskImage.SurfaceBlock> Blocks { get; set; } = [];
    public long TotalBlocks { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (TotalBlocks <= 0) return;
        double cell = 9;
        int cols = Math.Max(1, (int)(ActualWidth / cell));
        long cells = Math.Min(TotalBlocks, cols * (int)Math.Max(1, ActualHeight / cell));
        double per = TotalBlocks / (double)cells;
        var states = new int[cells];
        var done = new bool[cells];
        lock (Blocks)
            for (int i = 0; i < Blocks.Count; i++)
            {
                long c = Math.Min(cells - 1, (long)(i / per));
                done[c] = true; states[c] = Math.Max(states[c], Blocks[i].State);
            }
        for (long c = 0; c < cells; c++)
        {
            Brush b = !done[c] ? Brushes2.Track : states[c] == 2 ? Brushes2.Bad : states[c] == 1 ? Brushes2.Caution : Brushes2.Good;
            dc.DrawRoundedRectangle(b, null, new Rect(c % cols * cell, c / cols * cell, cell - 2, cell - 2), 1.5, 1.5);
        }
    }
}
