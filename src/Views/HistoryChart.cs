using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DiskStudio.Core;

namespace DiskStudio.Views;

/// <summary>Gráfica sencilla del historial de salud y temperatura de un disco.</summary>
public class HistoryChart : FrameworkElement
{
    List<HealthSample> data = [];
    public void SetData(List<HealthSample> d) { data = d; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        const double left = 34, right = 34, top = 10, bottom = 22;
        double pw = w - left - right, ph = h - top - bottom;
        var grid = new Pen(Brushes2.Track, 1);
        var text2 = Brushes2.Text2;
        var tf = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void label(string s, double x, double y, Brush b, TextAlignment a = TextAlignment.Left)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 10.5, b, dpi) { TextAlignment = a };
            dc.DrawText(ft, new Point(x, y - ft.Height / 2));
        }
        for (int i = 0; i <= 4; i++) dc.DrawLine(grid, new Point(left, top + ph * i / 4), new Point(left + pw, top + ph * i / 4));
        if (data.Count < 2) { label(L.T("Aún no hay suficientes datos: el historial se va guardando con el uso."), left, top + ph / 2, text2); return; }

        var t0 = data[0].Time; var t1 = data[^1].Time;
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        double X(DateTime t) => left + pw * (t - t0).TotalSeconds / span;
        var temps = data.Where(s => s.Temp != null).ToList();
        int tmin = temps.Count > 0 ? Math.Min(20, temps.Min(s => s.Temp.Value)) : 20, tmax = temps.Count > 0 ? Math.Max(60, temps.Max(s => s.Temp.Value) + 5) : 60;
        double Yh(int v) => top + ph * (1 - v / 100.0);
        double Yt(int v) => top + ph * (1 - (v - tmin) / (double)(tmax - tmin));

        void line(IEnumerable<Point> pts, Brush b, double thick)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                bool first = true;
                foreach (var p in pts) { if (first) { c.BeginFigure(p, false, false); first = false; } else c.LineTo(p, true, true); }
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(b, thick) { LineJoin = PenLineJoin.Round }, g);
        }
        var health = data.Where(s => s.Health != null).ToList();
        if (health.Count > 1) line(health.Select(s => new Point(X(s.Time), Yh(s.Health.Value))), Brushes2.Good, 2);
        var tempBrush = (Brush)Application.Current.TryFindResource("BrandBrush") ?? Brushes2.Accent;
        if (temps.Count > 1) line(temps.Select(s => new Point(X(s.Time), Yt(s.Temp.Value))), tempBrush, 1.6);

        label("100%", left - 4, top, Brushes2.Good, TextAlignment.Right);
        label("0%", left - 4, top + ph, Brushes2.Good, TextAlignment.Right);
        label($"{tmax} °C", left + pw + 4, top, tempBrush);
        label($"{tmin} °C", left + pw + 4, top + ph, tempBrush);
        label(t0.ToString("d MMM", L.Culture), left, top + ph + 12, text2);
        label(t1.ToString("d MMM HH:mm", L.Culture), left + pw, top + ph + 12, text2, TextAlignment.Right);
    }
}
