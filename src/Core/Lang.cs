using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace DiskStudio.Core;

/// <summary>
/// Traducción de la interfaz. Los textos se escriben en español en el código y, si el idioma es inglés,
/// se traducen al mostrarse con el diccionario <c>Lang/en.json</c>. Las entradas con {0}, {1}… son plantillas
/// que también traducen textos con datos variables (tamaños, letras, nombres…).
/// </summary>
public static class L
{
    public static bool English { get; private set; }
    public static CultureInfo Culture => English ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("es-ES");

    static Dictionary<string, string> exact = new(StringComparer.Ordinal);
    static readonly Dictionary<string, List<Pattern>> byPrefix = new(StringComparer.Ordinal);
    static readonly List<Pattern> loose = [];
    static readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);
    const int Key = 3;

    sealed record Pattern(Regex Rx, string En, int Holes, int Literal);
    /// <summary>Diagnóstico (DSS_LANGMISS=archivo): apunta los textos que se quedan sin traducir.</summary>
    static readonly string MissLog = Environment.GetEnvironmentVariable("DSS_LANGMISS");

    /// <summary>Idioma efectivo: "auto" usa español si Windows está en español y si no, inglés.</summary>
    public static bool ResolveEnglish(string setting) => setting switch
    {
        "en" => true, "es" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "es"
    };

    public static void Init(string setting)
    {
        English = ResolveEnglish(setting);
        if (!English) return;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Culture;
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Lang/en.json"));
            if (res == null) { English = false; return; }
            using var sr = new StreamReader(res.Stream, Encoding.UTF8);
            Load(sr.ReadToEnd());
        }
        catch { English = false; }
    }

    /// <summary>Carga un diccionario (usado también por las pruebas).</summary>
    public static void Load(string json, bool enable = true)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        exact = new Dictionary<string, string>(StringComparer.Ordinal);
        byPrefix.Clear(); loose.Clear(); cache.Clear();
        foreach (var (es, en) in d)
        {
            if (string.IsNullOrEmpty(es) || string.IsNullOrEmpty(en)) continue;
            exact[es] = en;
            // También sin los espacios de los extremos (fragmentos que se pegan a otro texto)
            if (es.Trim() != es && es.Trim().Length > 1) exact.TryAdd(es.Trim(), en.Trim());
            if (!Regex.IsMatch(es, @"\{\d+\}")) continue;
            int holes = 0;
            var sb = new StringBuilder("^");
            int last = 0;
            int literal = Regex.Replace(es, @"\{\d+\}", "").Trim().Length;
            // Plantillas con poco texto fijo («Disco {0}»): el dato solo puede ser un número, una letra de unidad o un tamaño,
            // para no traducir por error nombres que el usuario puso a sus discos.
            string hole = literal <= 9 ? @"[^a-záéíóúñ]*?[^a-záéíóúñ\s]?(?:[KMGTP]?B|s|h|m|min|°C)?" : ".+?";
            foreach (Match m in Regex.Matches(es, @"\{(\d+)\}"))
            {
                sb.Append(Regex.Escape(es[last..m.Index]));
                sb.Append($"(?<g{m.Groups[1].Value}>{hole})");
                last = m.Index + m.Length;
                holes++;
            }
            sb.Append(Regex.Escape(es[last..])).Append('$');
            var p = new Pattern(new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), en, holes, literal);
            int first = es.IndexOf('{');
            if (first >= Key) { string k = es[..Key]; if (!byPrefix.TryGetValue(k, out var l)) byPrefix[k] = l = []; l.Add(p); }
            else loose.Add(p);
        }
        // Las plantillas más específicas (más texto fijo) primero
        foreach (var l in byPrefix.Values) l.Sort((a, b) => b.Literal.CompareTo(a.Literal));
        loose.Sort((a, b) => b.Literal.CompareTo(a.Literal));
        English = enable;
    }

    public static void Disable() { English = false; cache.Clear(); }

    /// <summary>Traduce un texto ya construido (con o sin datos variables).</summary>
    public static string T(string s)
    {
        if (!English || string.IsNullOrEmpty(s)) return s;
        if (exact.TryGetValue(s, out var e)) return e;
        if (cache.TryGetValue(s, out var c)) return c;
        string r = Translate(s, 0);
        if (r == s && MissLog != null && Regex.IsMatch(s, "[a-záéíóúñ]{3}")) lock (cache) File.AppendAllText(MissLog, s.Replace("\n", "\\n") + "\n");
        if (cache.Count > 40000) cache.Clear();
        cache[s] = r;
        return r;
    }

    /// <summary>Formatea con una plantilla traducida: L.F("Quedan {0} trabajos", n).</summary>
    public static string F(string template, params object[] args)
    {
        string t = English && exact.TryGetValue(template, out var e) ? e : template;
        try { return string.Format(Culture, t, args.Select(a => a is string str ? T(str) : a).ToArray()); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, template, args); }
    }

    static string Translate(string s, int depth)
    {
        if (exact.TryGetValue(s, out var e)) return e;
        if (depth > 3 || s.Length > 4000) return s;
        // Espacios alrededor
        string core = s.Trim();
        if (core.Length == 0) return s;
        if (core.Length != s.Length)
        {
            string t = Translate(core, depth + 1);
            return t == core ? s : s[..s.IndexOf(core[0])] + t + s[(s.IndexOf(core[0]) + core.Length)..];
        }
        // Textos compuestos con « · » o varias líneas: cada parte por separado
        foreach (var sep in new[] { "\n", "  ·  ", " · " })
        {
            if (!s.Contains(sep)) continue;
            var parts = s.Split(sep);
            var tr = parts.Select(x => Translate(x, depth + 1)).ToArray();
            if (!tr.SequenceEqual(parts)) return string.Join(sep, tr);
        }
        // Plantillas
        IEnumerable<Pattern> candidates = s.Length >= Key && byPrefix.TryGetValue(s[..Key], out var l) ? l.Concat(loose).OrderByDescending(p => p.Literal) : loose;
        foreach (var p in candidates)
        {
            var m = p.Rx.Match(s);
            if (!m.Success) continue;
            string en = p.En;
            return Regex.Replace(en, @"\{(\d+)\}", x =>
            {
                var g = m.Groups["g" + x.Groups[1].Value];
                return g.Success ? Translate(g.Value, depth + 1) : x.Value;
            });
        }
        // Textos compuestos: líneas, " · ", ": " y signos finales
        foreach (var sep in new[] { " — ", ". ", ": ", ", " })
        {
            if (!s.Contains(sep)) continue;
            var parts = s.Split(sep);
            var tr = parts.Select(x => Translate(x, depth + 1)).ToArray();
            if (!tr.SequenceEqual(parts)) return string.Join(sep, tr);
        }
        foreach (var end in new[] { "…", ":", ".", "?" })
            if (s.Length > end.Length && s.EndsWith(end) && exact.TryGetValue(s[..^end.Length], out var t2)) return t2 + end;
        return s;
    }

    // ───────────── Interfaz ─────────────
    static readonly DataTemplate stringTemplate = MakeTemplate();
    static DataTemplate MakeTemplate()
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new Binding { Converter = new ToStringT() });
        return new DataTemplate { VisualTree = f };
    }

    sealed class ToStringT : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) => T(v?.ToString() ?? "");
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Traduce los textos de un árbol de controles (no toca los valores enlazados a datos).</summary>
    public static void Apply(DependencyObject root)
    {
        if (!English || root == null) return;
        var seen = new HashSet<DependencyObject>();
        Walk(root, seen);
    }

    static void Walk(DependencyObject d, HashSet<DependencyObject> seen)
    {
        if (d == null || !seen.Add(d)) return;
        switch (d)
        {
            case TextBlock tb:
                if (!BindingOperations.IsDataBound(tb, TextBlock.TextProperty))
                {
                    if (tb.Inlines.Count > 1 || tb.Inlines.FirstInline is not Run)
                    {
                        foreach (var r in tb.Inlines.OfType<Run>().ToList()) if (!BindingOperations.IsDataBound(r, Run.TextProperty)) { string x = T(r.Text); if (x != r.Text) r.Text = x; }
                    }
                    else { string x = T(tb.Text); if (x != tb.Text) tb.Text = x; }
                }
                break;
            case ComboBox cb:
                if (cb.ItemTemplate == null && string.IsNullOrEmpty(cb.DisplayMemberPath) && cb.Items.Count > 0 && cb.Items[0] is not UIElement) cb.ItemTemplate = stringTemplate;
                break;
            case HeaderedContentControl hc when hc.Header is string h && !BindingOperations.IsDataBound(hc, HeaderedContentControl.HeaderProperty):
                hc.Header = T(h);
                break;
            case HeaderedItemsControl hi when hi.Header is string h2 && !BindingOperations.IsDataBound(hi, HeaderedItemsControl.HeaderProperty):
                hi.Header = T(h2);
                break;
            case DataGrid g:
                foreach (var col in g.Columns) if (col.Header is string ch) col.Header = T(ch);
                break;
        }
        if (d is ContentControl cc && cc.Content is string s && !BindingOperations.IsDataBound(cc, ContentControl.ContentProperty) && d is not ComboBoxItem && d is not ListBoxItem)
        { string x = T(s); if (x != s) cc.Content = x; }
        if (d is FrameworkElement fe && fe.ToolTip is string tip) { string x = T(tip); if (x != tip) fe.ToolTip = x; }

        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i), seen);
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c, seen);
    }

    /// <summary>Engancha la traducción a menús contextuales y descripciones emergentes de toda la app.</summary>
    public static void Hook()
    {
        if (!English) return;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Apply((DependencyObject)s)));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((s, _) =>
        {
            if (s is FrameworkElement fe && fe.ToolTip is string t) fe.ToolTip = T(t);
            else if (s is FrameworkElement f2 && f2.ToolTip is DependencyObject o) Apply(o);
        }));
    }
}
