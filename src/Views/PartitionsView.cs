using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Editor de particiones de un disco.</summary>
public class PartitionsView : UserControl
{
    readonly ComboBox diskBox = new() { MinWidth = 460 };
    readonly PartitionMap map = new() { Margin = new Thickness(0, 4, 0, 16) };
    readonly StackPanel list = new();
    readonly StackPanel actions = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock selInfo = new() { Margin = new Thickness(0, 0, 0, 10) };
    readonly StackPanel diskNotes = new();
    DiskSegment selected;
    readonly Dictionary<DiskSegment, Border> rows = [];

    static readonly (string, string)[] Help =
    [
        ("Reducir y ampliar", "Windows mueve los datos de forma segura. Para ampliar hace falta espacio sin asignar justo después de la partición. Solo NTFS (y ReFS) se pueden reducir o ampliar en caliente."),
        ("Dividir", "Reduce la partición y crea una nueva en el espacio que queda libre, sin borrar los datos de la original."),
        ("Eliminar", "Borra la partición y todo su contenido. Las particiones que Windows necesita para arrancar no se pueden eliminar."),
        ("Copia de seguridad de la tabla", "Antes de cada cambio se guarda una copia de la tabla de particiones en la carpeta de configuración, restaurable desde «MBR ↔ GPT»."),
        ("Partición activa", "Solo en discos MBR con arranque BIOS: indica qué partición arranca. Normalmente no hace falta cambiarla."),
    ];

    record DiskItem(PhysicalDisk Disk)
    {
        public override string ToString() => $"Disco {Disk.Number} · {Disk.Title} · {Fmt.Size(Disk.Size)} · {Disk.PartitionStyle}" + (Disk.IsSystem ? " · SISTEMA" : "");
    }

    public PartitionsView()
    {
        var header = Columns(
            (Col(Text("Particiones", "PageTitle"), Text("Crea, elimina, reduce, amplía o divide particiones y cambia su letra.", "PageSubtitle").Also(t => t.Margin = new Thickness(0, 4, 0, 18))), Star),
            (Row(InfoBtn("Editor de particiones", Help), Btn("Actualizar", Icons.Refresh, (_, _) => AppState.Rescan(false), "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0))).Also(r => r.VerticalAlignment = VerticalAlignment.Top), Auto));
        var top = Card(Col(Text("Disco", "Label"), diskBox, diskNotes.Also(n => n.Margin = new Thickness(0, 10, 0, 0))), new Thickness(0, 0, 0, 14), 20);
        var body = Card(Col(Text("Mapa del disco", "SectionTitle"), map, list), new Thickness(0, 0, 0, 14), 20);
        var actCard = Card(Col(selInfo, new WrapPanel().Also(w => w.Children.Add(actions))), new Thickness(0), 18);
        var page = Col(header, top, actCard, body);
        page.Margin = new Thickness(36, 28, 36, 24);
        page.MaxWidth = 1100; page.HorizontalAlignment = HorizontalAlignment.Left;
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        diskBox.SelectionChanged += (_, _) => Render();
        AppState.Changed += Refill;
        Refill();
    }

    PhysicalDisk Disk => (diskBox.SelectedItem as DiskItem)?.Disk;

    void Refill()
    {
        int? prev = Disk?.Number;
        var items = AppState.Disks.Select(d => new DiskItem(d)).ToList();
        diskBox.ItemsSource = items;
        diskBox.SelectedItem = items.FirstOrDefault(i => i.Disk.Number == prev) ?? items.FirstOrDefault(i => !i.Disk.IsSystem) ?? items.FirstOrDefault();
        Render();
    }

    public void Prepare(PhysicalDisk d)
    {
        var items = (List<DiskItem>)diskBox.ItemsSource;
        diskBox.SelectedItem = items?.FirstOrDefault(i => i.Disk.Number == d.Number) ?? diskBox.SelectedItem;
    }

    void Render()
    {
        list.Children.Clear(); rows.Clear(); diskNotes.Children.Clear();
        var d = Disk;
        selected = null;
        if (d == null) { map.Children.Clear(); UpdateActions(); return; }
        map.SetDisk(d);
        if (!Native.IsAdmin) diskNotes.Children.Add(Note(Icons.Admin, "Para modificar particiones hay que ejecutar Disk Studio Suite como administrador.", Brushes2.Caution));
        if (d.IsSystem || d.IsBoot) diskNotes.Children.Add(Note(Icons.Warning, "Es el disco del sistema: puedes reducir, ampliar o crear particiones, pero no eliminar las que Windows necesita.", Brushes2.Caution));
        if (d.PartitionStyle == "Sin inicializar")
        {
            diskNotes.Children.Add(Note(Icons.Info, "El disco no tiene tabla de particiones. Inicialízalo para poder crear particiones.", Brushes2.Accent));
            diskNotes.Children.Add(Row(
                Btn("Inicializar como GPT", Icons.Add, (_, _) => Init(d, TableStyle.Gpt), "PrimaryButton"),
                Btn("Inicializar como MBR", Icons.Add, (_, _) => Init(d, TableStyle.Mbr), "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0))));
        }
        foreach (var seg in PartitionEditor.Segments(d))
        {
            var s = seg;
            var p = s.Part;
            string title = p == null ? "Espacio sin asignar" : p.Display;
            string sub = p == null ? "Disponible para crear una partición"
                : $"{PartitionEditor.RoleText(p)}" + (string.IsNullOrEmpty(p.FileSystem) ? "" : $" · {p.FileSystem}") + (p.IsActive ? " · activa" : "") + (p.IsBoot ? " · arranque" : "") + (!string.IsNullOrEmpty(p.Letter) && p.Size > 0 ? $" · {Fmt.Size(p.Free)} libres" : "");
            var info = Col(Text(title, size: 14, weight: FontWeights.SemiBold), Text(sub, "Caption", 12));
            var row = Columns((Tile(p == null ? Icons.Add : !string.IsNullOrEmpty(p.Letter) ? Icons.Drive : Icons.Layers, p == null ? (Brush)FindResource("BrandSlateBrush") : null, 34), Auto),
                (info.Also(i => i.Margin = new Thickness(12, 0, 0, 0)), Star),
                (Col(Text(Fmt.Size(s.Size), size: 14).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right), Text($"desde {Fmt.Size(s.Offset)}", "Caption", 11).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right)), Auto));
            var b = new Border { Style = S("SubtleCard"), Padding = new Thickness(12, 8, 14, 8), Margin = new Thickness(0, 0, 0, 6), Child = row, Cursor = Cursors.Hand, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
            b.MouseLeftButtonUp += (_, _) => Select(s);
            rows[s] = b;
            list.Children.Add(b);
        }
        Select(rows.Keys.FirstOrDefault(s => s.IsFree) ?? rows.Keys.FirstOrDefault());
    }

    static UIElement Note(string glyph, string text, Brush b) => Row(Icon(glyph, 13, b, new Thickness(0, 0, 8, 0)), Text(text, "Caption", 12).Also(x => { x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; x.MaxWidth = 900; })).Also(r => r.Margin = new Thickness(0, 0, 0, 8));

    void Select(DiskSegment s)
    {
        selected = s;
        foreach (var (k, b) in rows)
            if (k == s) b.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush"); else b.BorderBrush = Brushes.Transparent;
        UpdateActions();
    }

    Button Act(string text, string glyph, Action a, bool enabled = true, string tip = null, string style = "SecondaryButton") =>
        Btn(text, glyph, (_, _) => a(), style, tip).Also(b => { b.Margin = new Thickness(0, 0, 8, 8); b.IsEnabled = enabled; if (!enabled && tip != null) ToolTipService.SetShowOnDisabled(b, true); });

    void UpdateActions()
    {
        actions.Children.Clear();
        var d = Disk; var s = selected;
        if (d == null || s == null) { selInfo.Text = "Elige una partición o un espacio sin asignar."; selInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); return; }
        selInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        selInfo.FontSize = 15; selInfo.FontWeight = FontWeights.SemiBold;
        if (s.IsFree)
        {
            selInfo.Text = $"Espacio sin asignar · {Fmt.Size(s.Size)}";
            actions.Children.Add(Act("Crear partición", Icons.Add, () => CreateDialog(d, s), style: "PrimaryButton"));
            return;
        }
        var p = s.Part;
        selInfo.Text = $"{p.Display} · {Fmt.Size(p.Size)}" + (string.IsNullOrEmpty(p.FileSystem) ? "" : " · " + p.FileSystem);
        bool resizable = p.FileSystem is "NTFS" or "ReFS";
        string noDel = PartitionEditor.CannotDelete(d, p);
        bool data = PartitionEditor.RoleOf(p) == PartitionEditor.Role.Data;
        actions.Children.Add(Act("Cambiar tamaño", Icons.Layers, () => ResizeDialog(d, p), resizable, resizable ? null : "Solo se pueden reducir o ampliar particiones NTFS."));
        actions.Children.Add(Act("Dividir", Icons.Layers, () => SplitDialog(d, p), resizable && p.Size >= 256L << 20, resizable ? null : "Solo se pueden dividir particiones NTFS."));
        actions.Children.Add(Act("Cambiar letra", Icons.Tag, () => LetterDialog(d, p), data && !p.IsBoot, p.IsBoot ? "La letra de la unidad de Windows no se puede cambiar." : null));
        if (!string.IsNullOrEmpty(p.Letter))
        {
            actions.Children.Add(Act("Cambiar nombre", Icons.Rename, () => Relabel(p)));
            actions.Children.Add(Act("Abrir", Icons.FolderOpen, () => MainWindow.Instance.Navigate<ExplorerView>("explorer").Open(p.Letter.TrimEnd('\\') + "\\")));
            var v = AppState.Volumes.Find(x => x.Letter.Equals(p.Letter.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (v != null && !v.IsSystem && !d.IsBoot && !p.IsBoot) actions.Children.Add(Act("Formatear", Icons.Drive, () => MainWindow.Instance.Navigate<FormatView>("format").Prepare(v)));
        }
        if (d.PartitionStyle == "MBR" && !p.IsActive && data) actions.Children.Add(Act("Marcar como activa", Icons.Power, () => Run(async () => { if (await Dialogs.Confirm("Marcar como activa", $"El equipo intentará arrancar desde {p.Display} en modo BIOS. Si no tiene un sistema arrancable, el disco dejará de arrancar.", "Marcar")) await Task.Run(() => PartitionEditor.SetActive(d, p)); })));
        actions.Children.Add(Act("Eliminar", Icons.Delete, () => Delete(d, p), noDel == null, noDel, "DangerButton"));
    }

    static async Task<bool> NeedAdmin()
    {
        if (Native.IsAdmin) return false;
        if (await Dialogs.Confirm("Se necesita administrador", "Modificar particiones requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin();
        return true;
    }

    async void Run(Func<Task> a)
    {
        if (await NeedAdmin()) return;
        try { await a(); } catch (Exception ex) { await Dialogs.Error("No se pudo completar", ex.Message); }
        AppState.Rescan(false);
    }

    void Enqueue(TransferJob j) { TransferQueue.Enqueue(j); Toast.Show("En cola", j.Title, j.Glyph); }

    async void Init(PhysicalDisk d, TableStyle t)
    {
        if (await NeedAdmin()) return;
        if (!await Dialogs.Confirm($"Inicializar disco {d.Number}", $"Se creará una tabla {t.ToString().ToUpperInvariant()} vacía en «{d.Title}».", "Inicializar")) return;
        Enqueue(PartitionEditor.InitializeJob(d, t));
    }

    // ───────────── Diálogos ─────────────
    static (Slider slider, TextBox box, UIElement ui) SizePicker(long min, long max, long value, string caption)
    {
        const double MB = 1 << 20;
        var slider = new Slider { Minimum = min / MB, Maximum = max / MB, Value = value / MB, SmallChange = 1, LargeChange = 1024, IsSnapToTickEnabled = false, MinWidth = 420 };
        var box = new TextBox { Width = 120, Text = ((long)(value / MB)).ToString() };
        var lbl = Text(caption, "Caption", 12, margin: new Thickness(0, 4, 0, 0));
        bool busy = false;
        slider.ValueChanged += (_, _) => { if (busy) return; busy = true; box.Text = ((long)slider.Value).ToString(); busy = false; };
        box.TextChanged += (_, _) => { if (busy) return; if (long.TryParse(box.Text, out long v)) { busy = true; slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum); busy = false; } };
        var ui = Col(Columns((slider, Star), (Row(box.Also(b => b.Margin = new Thickness(12, 0, 6, 0)), Text("MB", "Caption")), Auto)), lbl);
        return (slider, box, ui);
    }

    static long Bytes(Slider s) => (long)Math.Round(s.Value) << 20;

    static (ComboBox fs, TextBox label, ComboBox letter, UIElement ui) FormatFields(long size)
    {
        var fs = new ComboBox { ItemsSource = new[] { "NTFS", "exFAT", "FAT32" }, SelectedIndex = 0, Width = 140 };
        var label = new TextBox { Text = "Nuevo vol", Width = 220 };
        var letters = new List<string> { "Sin letra" };
        letters.AddRange(PartitionEditor.FreeLetters());
        var letter = new ComboBox { ItemsSource = letters, SelectedIndex = letters.Count > 1 ? 1 : 0, Width = 120 };
        var ui = Columns((Col(Text("Sistema de archivos", "Label"), fs), Auto), (null, Px(12)), (Col(Text("Nombre", "Label"), label), Auto), (null, Px(12)), (Col(Text("Letra", "Label"), letter), Auto));
        return (fs, label, letter, ui);
    }

    static FormatOptions Options(ComboBox fs, TextBox label) => new() { FileSystem = (string)fs.SelectedItem, Label = Formatter.CleanLabel(label.Text, (string)fs.SelectedItem), Quick = true };
    static string LetterOf(ComboBox c) => c.SelectedIndex <= 0 ? null : (string)c.SelectedItem;

    async void CreateDialog(PhysicalDisk d, DiskSegment s)
    {
        if (await NeedAdmin()) return;
        if (d.PartitionStyle == "MBR" && d.Partitions.Count >= 4) { await Dialogs.Error("Límite de MBR", "Un disco MBR admite como máximo 4 particiones principales. Convierte el disco a GPT para crear más."); return; }
        var (slider, _, sizeUi) = SizePicker(16L << 20, s.Size, s.Size, $"Disponible: {Fmt.Size(s.Size)}");
        var (fs, label, letter, fmtUi) = FormatFields(s.Size);
        var panel = Col(Text("Tamaño", "Label"), sizeUi, fmtUi.Also(f => ((FrameworkElement)f).Margin = new Thickness(0, 14, 0, 0)));
        if (await Dialogs.Show("Crear partición", null, ["Crear", "Cancelar"], 0, extra: panel, glyph: Icons.Add) != 0) return;
        var o = Options(fs, label);
        var probs = Formatter.Problems(Bytes(slider), o, d.SectorSize);
        if (probs.Count > 0) { await Dialogs.Error("No se puede crear", string.Join("\n", probs)); return; }
        Enqueue(PartitionEditor.CreateJob(d, s, Bytes(slider), o, LetterOf(letter)));
    }

    async void ResizeDialog(PhysicalDisk d, PartitionInfo p)
    {
        if (await NeedAdmin()) return;
        (long min, long max) r;
        try { r = await Task.Run(() => PartitionEditor.SupportedSize(d.Number, p.Offset)); }
        catch (Exception ex) { await Dialogs.Error("No se puede cambiar el tamaño", ex.Message); return; }
        if (r.max - r.min < 2L << 20) { await Dialogs.Info("Sin margen", $"Windows no permite cambiar el tamaño de {p.Display}: no hay espacio libre detrás para ampliarla ni datos que se puedan recolocar para reducirla."); return; }
        var (slider, _, ui) = SizePicker(r.min, r.max, p.Size, $"Actual: {Fmt.Size(p.Size)} · mínimo {Fmt.Size(r.min)} · máximo {Fmt.Size(r.max)}");
        var panel = Col(Text("Nuevo tamaño", "Label"), ui, Text("Reducir puede tardar varios minutos si Windows tiene que mover archivos. No apagues el equipo mientras tanto.", "Caption", 12, margin: new Thickness(0, 12, 0, 0)).Also(t => t.TextWrapping = TextWrapping.Wrap));
        if (await Dialogs.Show($"Cambiar tamaño de {p.Display}", null, ["Aplicar", "Cancelar"], 0, extra: panel, glyph: Icons.Layers) != 0) return;
        long size = Bytes(slider);
        if (Math.Abs(size - p.Size) < 1 << 20) return;
        Enqueue(PartitionEditor.ResizeJob(d, p, size));
    }

    async void SplitDialog(PhysicalDisk d, PartitionInfo p)
    {
        if (await NeedAdmin()) return;
        if (d.PartitionStyle == "MBR" && d.Partitions.Count >= 4) { await Dialogs.Error("Límite de MBR", "Un disco MBR admite como máximo 4 particiones principales."); return; }
        (long min, long max) r;
        try { r = await Task.Run(() => PartitionEditor.SupportedSize(d.Number, p.Offset)); }
        catch (Exception ex) { await Dialogs.Error("No se puede dividir", ex.Message); return; }
        long maxFirst = Math.Min(p.Size - (32L << 20), p.Size);
        if (maxFirst <= r.min) { await Dialogs.Info("Sin espacio", $"{p.Display} está demasiado llena para dividirla."); return; }
        var (slider, _, ui) = SizePicker(r.min, maxFirst, (r.min + maxFirst) / 2, $"Tamaño de {p.Display} tras dividir (mínimo {Fmt.Size(r.min)}). El resto será la partición nueva.");
        var (fs, label, letter, fmtUi) = FormatFields(p.Size / 2);
        var rest = Text("", "Caption", 12, margin: new Thickness(0, 10, 0, 0));
        void upd() => rest.Text = $"Partición nueva: {Fmt.Size(p.Size - Bytes(slider))}";
        slider.ValueChanged += (_, _) => upd(); upd();
        var panel = Col(Text($"Tamaño de {p.Display}", "Label"), ui, rest, Text("Partición nueva", "SectionTitle", margin: new Thickness(0, 14, 0, 6)), fmtUi);
        if (await Dialogs.Show($"Dividir {p.Display}", null, ["Dividir", "Cancelar"], 0, extra: panel, glyph: Icons.Layers) != 0) return;
        var o = Options(fs, label);
        var probs = Formatter.Problems(p.Size - Bytes(slider), o, d.SectorSize);
        if (probs.Count > 0) { await Dialogs.Error("No se puede dividir", string.Join("\n", probs)); return; }
        Enqueue(PartitionEditor.SplitJob(d, p, Bytes(slider), o, LetterOf(letter)));
    }

    async void LetterDialog(PhysicalDisk d, PartitionInfo p)
    {
        if (await NeedAdmin()) return;
        var letters = new List<string> { "Sin letra (ocultar)" };
        if (!string.IsNullOrEmpty(p.Letter)) letters.Add(p.Letter.TrimEnd('\\'));
        letters.AddRange(PartitionEditor.FreeLetters());
        var cb = new ComboBox { ItemsSource = letters, SelectedIndex = string.IsNullOrEmpty(p.Letter) ? Math.Min(1, letters.Count - 1) : 1, Width = 200 };
        var panel = Col(Text("Letra de unidad", "Label"), cb, Text("Los programas y accesos directos que usen la letra anterior dejarán de encontrar sus archivos.", "Caption", 12, margin: new Thickness(0, 10, 0, 0)).Also(t => t.TextWrapping = TextWrapping.Wrap));
        if (await Dialogs.Show($"Cambiar letra de {p.Display}", null, ["Aplicar", "Cancelar"], 0, extra: panel, glyph: Icons.Tag) != 0) return;
        string l = cb.SelectedIndex <= 0 ? null : (string)cb.SelectedItem;
        if (string.Equals(l, p.Letter?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        Run(() => Task.Run(() => PartitionEditor.SetLetter(d, p, l)));
    }

    async void Relabel(PartitionInfo p)
    {
        string name = await Dialogs.Prompt("Cambiar nombre del volumen", $"Nuevo nombre para {p.Letter}:", p.Label);
        if (name == null) return;
        if (Native.SetVolumeLabel(p.Letter.TrimEnd('\\') + "\\", name)) { Log.Ok($"Volumen {p.Letter} renombrado a \"{name}\""); AppState.Rescan(false); }
        else await Dialogs.Error("No se pudo cambiar el nombre", Native.IsAdmin ? "Windows rechazó el cambio." : "Puede que necesites ejecutar Disk Studio Suite como administrador.");
    }

    async void Delete(PhysicalDisk d, PartitionInfo p)
    {
        if (await NeedAdmin()) return;
        string why = PartitionEditor.CannotDelete(d, p);
        if (why != null) { await Dialogs.Error("No se puede eliminar", why); return; }
        if (!await Dialogs.TypeToConfirm($"Eliminar {p.Display}", $"Se borrará la partición {p.Display} ({Fmt.Size(p.Size)}) y TODO su contenido. Se guardará una copia de la tabla de particiones, pero no de los datos.",
            $"BORRAR PARTICIÓN {p.Number}", "Eliminar")) return;
        Enqueue(PartitionEditor.DeleteJob(d, p));
    }
}
