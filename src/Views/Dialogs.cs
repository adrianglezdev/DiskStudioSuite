using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskStudio.Core;

/// <summary>Diálogos modernos superpuestos en la ventana principal.</summary>
public static class Dialogs
{
    static MainWindow W => MainWindow.Instance;
    static Brush R(string k) => (Brush)Application.Current.FindResource(k);

    public static Task<int> Show(string title, string text, string[] buttons, int primary = 0, bool danger = false, UIElement extra = null, string glyph = null, Brush glyphBrush = null, int cancel = -1)
        => W.ShowDialog(title, text, glyph, glyphBrush, buttons, primary, danger, extra, cancel < 0 ? buttons.Length - 1 : cancel);

    public static Task Info(string title, string text) => Show(title, text, ["Aceptar"], glyph: Icons.Info, cancel: 0);
    public static Task Error(string title, string text) => Show(title, text, ["Aceptar"], glyph: Icons.Error, glyphBrush: R("BadBrush"), cancel: 0);

    public static async Task<bool> Confirm(string title, string text, string yes = "Aceptar", bool danger = false, string no = "Cancelar")
        => await Show(title, text, [yes, no], 0, danger, glyph: danger ? Icons.Warning : Icons.Info, glyphBrush: danger ? R("CautionBrush") : null) == 0;

    /// <summary>Ventana explicativa (botón ⓘ): secciones con título y texto.</summary>
    public static Task Explain(string title, params (string head, string body)[] sections)
    {
        var sp = new StackPanel { MaxWidth = 560 };
        foreach (var (head, body) in sections)
        {
            if (!string.IsNullOrEmpty(head))
                sp.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, sp.Children.Count == 0 ? 0 : 14, 0, 4), Foreground = R("TextFillColorPrimaryBrush"), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = body, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Foreground = R("TextFillColorSecondaryBrush") });
        }
        var sv = new ScrollViewer { Content = sp, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return Show(title, null, ["Entendido"], 0, extra: sv, glyph: Icons.Info, cancel: 0);
    }

    public static async Task<string> Prompt(string title, string label, string initial = "", string ok = "Aceptar")
    {
        var tb = new TextBox { Text = initial ?? "", MinWidth = 380 };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("Label") });
        sp.Children.Add(tb);
        tb.Loaded += (_, _) =>
        {
            tb.Focus();
            int dot = tb.Text.LastIndexOf('.');
            if (dot > 0) tb.Select(0, dot); else tb.SelectAll();
        };
        int r = await Show(title, null, [ok, "Cancelar"], 0, extra: sp);
        return r == 0 ? tb.Text.Trim() : null;
    }

    /// <summary>Confirmación reforzada: el usuario debe escribir una frase exacta.</summary>
    public static async Task<bool> TypeToConfirm(string title, string text, string phrase, string yes)
    {
        var tb = new TextBox { MinWidth = 380 };
        var sp = new StackPanel();
        var lbl = new TextBlock { Style = (Style)Application.Current.FindResource("Label"), TextWrapping = TextWrapping.Wrap };
        lbl.Inlines.Add("Para confirmar, escribe ");
        string shown = L.T(phrase);
        lbl.Inlines.Add(new System.Windows.Documents.Run(shown) { FontWeight = FontWeights.Bold, Foreground = R("BadBrush") });
        sp.Children.Add(lbl);
        sp.Children.Add(tb);
        while (true)
        {
            int r = await Show(title, text, [yes, "Cancelar"], 0, true, sp, Icons.Warning, R("BadBrush"));
            if (r != 0) return false;
            string typed = tb.Text.Trim();
            if (typed == phrase || typed == shown) return true;
            tb.Text = "";
        }
    }

    public static async Task<(ConflictAnswer, bool)> AskConflict(FileInfo src, FileInfo dst)
    {
        var cb = new CheckBox { Content = "Aplicar a todos los conflictos de esta operación", Margin = new Thickness(0, 4, 0, 0) };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] rows = ["", "Tamaño", "Modificado"];
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition());
        void cell(string t, int r, int c, bool bold = false, Brush fg = null)
        {
            var tb = new TextBlock { Text = t, Margin = new Thickness(0, 3, 16, 3), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = fg ?? R("TextFillColorPrimaryBrush") };
            Grid.SetRow(tb, r); Grid.SetColumn(tb, c); grid.Children.Add(tb);
        }
        for (int i = 1; i < 3; i++) cell(rows[i], i, 0, fg: R("TextFillColorSecondaryBrush"));
        cell("Nuevo (origen)", 0, 1, true); cell("Existente (destino)", 0, 2, true);
        cell(Fmt.Size(src.Length), 1, 1); cell(Fmt.Size(dst.Length), 1, 2);
        bool newer = src.LastWriteTimeUtc > dst.LastWriteTimeUtc;
        cell(src.SafeWriteTime().ToString("dd/MM/yyyy HH:mm:ss") + (newer ? "  ✓ más reciente" : ""), 2, 1, fg: newer ? R("GoodBrush") : null);
        cell(dst.SafeWriteTime().ToString("dd/MM/yyyy HH:mm:ss") + (!newer ? "  ✓ más reciente" : ""), 2, 2, fg: !newer ? R("GoodBrush") : null);
        var sp = new StackPanel();
        sp.Children.Add(grid); sp.Children.Add(cb);
        int r = await Show("El archivo ya existe", dst.FullName, ["Reemplazar", "Omitir", "Conservar ambos", "Cancelar"], 0, extra: sp, glyph: Icons.Copy);
        var ans = r switch { 0 => ConflictAnswer.Overwrite, 1 => ConflictAnswer.Skip, 2 => ConflictAnswer.KeepBoth, _ => ConflictAnswer.Cancel };
        return (ans, cb.IsChecked == true);
    }

    public static string PickFolder(string initial = null, string title = "Seleccionar carpeta")
    {
        var d = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(MainWindow.Instance) == true ? d.FolderName : null;
    }

    public static string PickFile(string filter = "Todos los archivos|*.*", string title = "Abrir archivo")
    {
        var d = new OpenFileDialog { Filter = filter, Title = title };
        return d.ShowDialog(MainWindow.Instance) == true ? d.FileName : null;
    }

    public static string[] PickFiles(string filter = "Todos los archivos|*.*")
    {
        var d = new OpenFileDialog { Filter = filter, Multiselect = true };
        return d.ShowDialog(MainWindow.Instance) == true ? d.FileNames : null;
    }

    public static string SaveFile(string filter, string name, string initialDir = null)
    {
        var d = new SaveFileDialog { Filter = filter, FileName = name };
        if (initialDir != null && Directory.Exists(initialDir)) d.InitialDirectory = initialDir;
        return d.ShowDialog(MainWindow.Instance) == true ? d.FileName : null;
    }
}
