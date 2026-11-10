using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskStudio;

/// <summary>
/// Colores de marca de Disk Studio Suite. Sustituye el color de acento de Windows en el tema Fluent
/// (botones, casillas, barras, selección…) por la paleta de la marca, con variantes para claro y oscuro.
/// </summary>
public static class Brand
{
    public static readonly Color Teal = Hex("#20876C"), Aqua = Hex("#2DFED3"), Slate = Hex("#647471"), Mint = Hex("#6BC399"), Lime = Hex("#E8F32C");
    /// <summary>Fondo sólido de la pantalla de carga.</summary>
    public static readonly Color Night = Hex("#0F2A24");

    public static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    public static bool IsDark(string theme) => theme switch
    {
        "Dark" => true,
        "Light" => false,
        _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0
    };

    public static void Apply(bool dark)
    {
        var r = Application.Current.Resources;
        void brush(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); r[key] = b; }
        Color a(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        // Tonos del acento de Windows → tonos de la marca (mismo «nivel» de claridad)
        var shades = new (string key, Color brand)[]
        {
            ("SystemAccentColor", Hex("#2A9C7E")), ("SystemAccentColorLight1", Hex("#45B08D")), ("SystemAccentColorLight2", Mint),
            ("SystemAccentColorLight3", Hex("#8EF0D4")), ("SystemAccentColorDark1", Teal), ("SystemAccentColorDark2", Hex("#16705A")),
            ("SystemAccentColorDark3", Hex("#0F5444"))
        };
        // Leer los tonos originales del tema (no los ya sustituidos en la raíz)
        var map = new Dictionary<(byte, byte, byte), Color>();
        foreach (var (key, brandColor) in shades)
            if (FindInMerged(r, key) is Color orig) map[(orig.R, orig.G, orig.B)] = brandColor;
        try
        {
            if (FindInMerged(r, "SystemColorHighlightColor") is Color hl && !map.ContainsKey((hl.R, hl.G, hl.B))) map[(hl.R, hl.G, hl.B)] = Teal;
        }
        catch { }
        // Variantes del acento que el tema define directamente en sus pinceles (p. ej. #0067C0 en claro)
        Color fillC = dark ? Mint : Teal, textC = dark ? Aqua : Hex("#16705A");
        foreach (var (key, target) in new[] { ("AccentFillColorDefaultBrush", fillC), ("AccentFillColorSecondaryBrush", fillC), ("AccentFillColorTertiaryBrush", fillC),
                                               ("AccentTextFillColorPrimaryBrush", textC), ("AccentTextFillColorSecondaryBrush", textC), ("AccentTextFillColorTertiaryBrush", fillC) })
            if (FindInMerged(r, key) is SolidColorBrush ob) map.TryAdd((ob.Color.R, ob.Color.G, ob.Color.B), target);
        // Sustituir cualquier pincel o color del tema que use esos tonos (casillas, radios, deslizadores, interruptores, barras…)
        var replace = new Dictionary<object, object>();
        void walk(ResourceDictionary d)
        {
            foreach (var key in d.Keys)
            {
                object v;
                try { v = d[key]; } catch { continue; }
                if (v is SolidColorBrush b && map.TryGetValue((b.Color.R, b.Color.G, b.Color.B), out var nb))
                    replace[key] = Color.FromArgb(b.Color.A, nb.R, nb.G, nb.B);
                else if (v is Color c && map.TryGetValue((c.R, c.G, c.B), out var nc))
                    replace[key] = Color.FromArgb(c.A, nc.R, nc.G, nc.B);
            }
            foreach (var m in d.MergedDictionaries) walk(m);
        }
        foreach (var m in r.MergedDictionaries) walk(m);
        foreach (var (key, val) in replace)
        {
            var orig = FindInMerged(r, key);
            if (orig is SolidColorBrush) brush(key.ToString(), (Color)val);
            else r[key] = val;
        }
        foreach (var (key, brandColor) in shades) r[key] = brandColor;

        Color fill = dark ? Mint : Teal;
        brush("AccentFillColorDefaultBrush", fill);
        brush("AccentFillColorSecondaryBrush", a(fill, 0xE6));
        brush("AccentFillColorTertiaryBrush", a(fill, 0xCC));
        brush("AccentFillColorDisabledBrush", dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x37, 0, 0, 0));
        brush("AccentTextFillColorPrimaryBrush", dark ? Aqua : Hex("#16705A"));
        brush("AccentTextFillColorSecondaryBrush", dark ? Aqua : Hex("#16705A"));
        brush("AccentTextFillColorTertiaryBrush", dark ? Mint : Teal);
        brush("TextOnAccentFillColorPrimaryBrush", dark ? Hex("#062019") : Colors.White);
        brush("TextOnAccentFillColorSecondaryBrush", dark ? Hex("#0F3A30") : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        brush("TextOnAccentFillColorSelectedTextBrush", Colors.White);
        brush("AccentFillColorSelectedTextBackgroundBrush", Teal);
    }

    static object FindInMerged(ResourceDictionary root, object key)
    {
        object found = null;
        void walk(ResourceDictionary d)
        {
            foreach (var m in d.MergedDictionaries) walk(m);
            if (d != root && d.Contains(key)) { try { found = d[key]; } catch { } }
        }
        walk(root);
        return found;
    }

    static bool hooked;
    public static void Hook()
    {
        if (hooked) return;
        hooked = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(IsDark(Core.Settings.Current.Theme)));
        };
    }
}
