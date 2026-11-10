using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskStudio.Views;

/// <summary>Pequeños constructores de interfaz para vistas generadas por código.</summary>
static class UI
{
    public static Style S(string key) => (Style)Application.Current.FindResource(key);
    public static Brush B(string key) => Application.Current.TryFindResource(key) as Brush;

    public static TextBlock Text(string text, string style = null, double? size = null, Brush fg = null, FontWeight? weight = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (style != null) t.Style = S(style);
        else t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        if (size != null) t.FontSize = size.Value;
        if (fg != null) t.Foreground = fg;
        if (weight != null) t.FontWeight = weight.Value;
        if (margin != null) t.Margin = margin.Value;
        return t;
    }

    public static TextBlock Icon(string glyph, double size = 16, Brush fg = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = glyph, Style = S("Icon"), FontSize = size };
        if (fg != null) t.Foreground = fg; else t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        if (margin != null) t.Margin = margin.Value;
        return t;
    }

    public static Border Tile(string glyph, Brush bg = null, double size = 40)
    {
        var b = new Border { Style = S("IconTile"), Width = size, Height = size, CornerRadius = new CornerRadius(size / 4) };
        if (bg != null) b.Background = bg;
        bool light = bg is SolidColorBrush sc && (0.299 * sc.Color.R + 0.587 * sc.Color.G + 0.114 * sc.Color.B) > 170;
        b.Child = Icon(glyph, size * 0.45, light ? new SolidColorBrush(Color.FromRgb(0x0F, 0x2A, 0x24)) : Brushes.White);
        return b;
    }

    public static Border Chip(string text, Brush accent = null)
    {
        var b = new Border { Style = S("Chip") };
        var t = new TextBlock { Text = text, Style = S("ChipText") };
        if (accent != null)
        {
            t.Foreground = accent;
            b.BorderBrush = accent;
            if (accent is SolidColorBrush sc) b.Background = new SolidColorBrush(Color.FromArgb(28, sc.Color.R, sc.Color.G, sc.Color.B));
        }
        b.Child = t;
        return b;
    }

    public static Button Btn(string text, string glyph, RoutedEventHandler click, string style = "ToolButton", string tip = null)
    {
        var b = new Button { Style = S(style), Tag = glyph, Content = text, ToolTip = tip };
        if (click != null) b.Click += click;
        return b;
    }

    public static Button IconBtn(string glyph, string tip, RoutedEventHandler click) => new Button { Style = S("IconButton"), Tag = glyph, ToolTip = tip }.Also(b => { if (click != null) b.Click += click; });

    /// <summary>Botón ⓘ que abre una explicación.</summary>
    public static Button InfoBtn(string title, params (string head, string body)[] sections)
    {
        var b = IconBtn(DiskStudio.Core.Icons.Info, "¿Qué hace esto?", (_, _) => _ = DiskStudio.Core.Dialogs.Explain(title, sections));
        b.Padding = new Thickness(5);
        b.Margin = new Thickness(4, 0, 0, 0);
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) if (i != null) sp.Children.Add(i);
        return sp;
    }

    public static StackPanel Col(params UIElement[] items)
    {
        var sp = new StackPanel();
        foreach (var i in items) if (i != null) sp.Children.Add(i);
        return sp;
    }

    public static Border Card(UIElement child, Thickness? margin = null, double padding = 18) =>
        new() { Style = S("Card"), Child = child, Margin = margin ?? new Thickness(0, 0, 0, 14), Padding = new Thickness(padding) };

    public static T Also<T>(this T o, Action<T> a) { a(o); return o; }

    public static ContextMenu Menu(params (string text, string glyph, Action act)[] items)
    {
        var m = new ContextMenu();
        foreach (var (text, glyph, act) in items)
        {
            if (text == "-") { m.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = text, Icon = glyph == null ? null : Icon(glyph, 14) };
            mi.Click += (_, _) => act();
            m.Items.Add(mi);
        }
        return m;
    }

    public static Grid Columns(params (UIElement el, GridLength w)[] cols)
    {
        var g = new Grid();
        for (int i = 0; i < cols.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols[i].w });
            if (cols[i].el == null) continue;
            Grid.SetColumn(cols[i].el, i);
            g.Children.Add(cols[i].el);
        }
        return g;
    }

    public static GridLength Auto => GridLength.Auto;
    public static GridLength Star => new(1, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);
}
