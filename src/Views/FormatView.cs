using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Formatear volúmenes o reiniciar discos completos (pendrives, tarjetas, discos externos…).</summary>
public class FormatView : UserControl, ISnapshotDemo
{
    readonly RadioButton modeVolume = new() { Style = S("SegmentItem"), Tag = Icons.Drive, Content = "Formatear volumen", GroupName = "fmode", IsChecked = true };
    readonly RadioButton modeDisk = new() { Style = S("SegmentItem"), Tag = Icons.Refresh, Content = "Reiniciar disco completo", GroupName = "fmode" };
    readonly ComboBox target = new() { MinWidth = 460 };
    readonly CheckBox showInternal = new() { Content = "Mostrar también discos internos", Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock targetInfo = new();
    readonly RadioButton tMbr = new() { Style = S("SegmentItem"), Tag = Icons.Usb, Content = "MBR (recomendado)", GroupName = "ftable", IsChecked = true };
    readonly RadioButton tGpt = new() { Style = S("SegmentItem"), Tag = Icons.Drive, Content = "GPT", GroupName = "ftable" };
    readonly Dictionary<string, RadioButton> fsButtons = [];
    readonly TextBox label = new() { Width = 300 };
    readonly ComboBox cluster = new() { Width = 180 };
    readonly CheckBox quick = new() { Content = "Formato rápido", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox compress = new() { Content = "Comprimir archivos y carpetas (NTFS)", VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel notes = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly StackPanel tableRow, warnBox;
    readonly TextBlock warnText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    readonly Button go;
    readonly Dictionary<string, Border> guideCards = [];
    string fs = "exFAT";
    bool userLabel;

    static readonly (string fs, string title, string tag, string[] pros, string[] cons, string[] works)[] Guide =
    [
        ("exFAT", "exFAT", "Pendrives y discos externos para usar en cualquier sitio",
            ["Funciona en Windows, Mac y la mayoría de televisores, coches y cámaras actuales", "Admite archivos de más de 4 GB"],
            ["Sin registro de cambios: desconéctalo siempre con «Expulsar»"], ["Windows", "Mac", "Linux", "TV/consolas actuales"]),
        ("NTFS", "NTFS", "Discos que vas a usar sobre todo en Windows",
            ["Más resistente a cortes de luz o desconexiones (registro de cambios)", "Permisos, compresión y archivos enormes"],
            ["En Mac solo se puede leer; muchos televisores y consolas no lo reconocen"], ["Windows", "Linux", "Mac (solo lectura)"]),
        ("FAT32", "FAT32", "Máxima compatibilidad con equipos antiguos",
            ["Lo lee casi todo: consolas, coches, impresoras, BIOS, TV antiguas"],
            ["Archivos de 4 GB como máximo", "Windows no lo crea en más de 32 GB (Disk Studio Suite sí)"], ["Todo", "Equipos antiguos"]),
    ];

    static readonly (string, string)[] ResetInfo =
    [
        ("Formatear volumen", "Borra y prepara una partición concreta (una letra de unidad), sin tocar el resto del disco."),
        ("Reiniciar disco completo", "Borra TODAS las particiones del disco y crea una sola que ocupa todo el espacio, con el formato que elijas. Es lo que suele arreglar un pendrive o tarjeta que:\n• muestra menos capacidad de la real,\n• aparece como RAW o «sin formato»,\n• tiene varias particiones (por ejemplo, después de usarlo como USB de instalación),\n• está marcado como de solo lectura por software.\nSi el pendrive tiene un interruptor físico de protección, hay que moverlo a mano."),
        ("Tabla de particiones", "MBR es la opción más compatible para pendrives y tarjetas. GPT es preferible para discos de más de 2 TB."),
    ];

    static readonly (string, string)[] ClusterInfo =
    [
        ("Tamaño de clúster", "Es la unidad mínima que ocupa cada archivo. Con clústeres grandes el disco es algo más rápido con archivos grandes, pero los archivos pequeños desperdician más espacio."),
        ("Recomendación", "Deja «Predeterminado» salvo que sepas lo que necesitas: Windows (y Disk Studio Suite para FAT32) eligen el valor adecuado según el tamaño."),
    ];

    static readonly (string, string)[] QuickInfo =
    [
        ("Formato rápido", "Crea un sistema de archivos vacío en segundos. Los datos antiguos no se sobrescriben: con programas de recuperación podrían recuperarse."),
        ("Formato completo", "Además escribe ceros en todo el volumen y comprueba los sectores dañados. Tarda mucho más (minutos u horas), pero deja el disco limpio y avisa si está en mal estado. Útil antes de regalar o vender un disco."),
    ];

    public FormatView()
    {
        var modeSeg = Seg(modeVolume, modeDisk);
        var fsSeg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var f in new[] { "NTFS", "exFAT", "FAT32" })
        {
            var rb = new RadioButton { Style = S("SegmentItem"), Tag = Icons.Drive, Content = f, GroupName = "ffs", IsChecked = f == fs };
            string ff = f;
            rb.Checked += (_, _) => SetFs(ff);
            fsButtons[f] = rb;
            fsSeg.Children.Add(rb);
        }
        tableRow = Col(Text("Tabla de particiones", "Label"), Row(Seg(tMbr, tGpt), InfoBtn("Reiniciar disco completo", ResetInfo))).Also(c => c.Margin = new Thickness(0, 0, 0, 14));
        cluster.ItemsSource = new[] { "Predeterminado", "4 KB", "8 KB", "16 KB", "32 KB", "64 KB" };
        cluster.SelectedIndex = 0;

        var warnIcon = Icon(Icons.Warning, 16, Brushes2.Bad, new Thickness(0, 0, 12, 0));
        var c = ((SolidColorBrush)Brushes2.Bad).Color;
        warnBox = Row(warnIcon, warnText);
        var warn = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 16), Background = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B)), Child = warnBox };
        warnText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        warnText.MaxWidth = 560;

        go = Btn("Formatear", Icons.Drive, (_, _) => Start(), "DangerButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);

        var form = Card(Col(
            Row(modeSeg, InfoBtn("¿Volumen o disco completo?", ResetInfo)).Also(r => r.Margin = new Thickness(0, 0, 0, 18)),
            Text("Unidad", "Label"), target, showInternal, targetInfo.Also(t => { t.Style = S("Caption"); t.FontSize = 13; t.Margin = new Thickness(0, 8, 0, 16); }),
            tableRow,
            Text("Sistema de archivos", "Label"), Row(Seg(fsSeg), InfoBtn("¿Qué formato elijo?", Guide.Select(g => (g.title + " · " + g.tag, "✓ " + string.Join("\n✓ ", g.pros) + "\n✗ " + string.Join("\n✗ ", g.cons))).ToArray())).Also(r => r.Margin = new Thickness(0, 0, 0, 14)),
            Columns((Col(Text("Nombre del volumen", "Label"), label), Auto), (null, Px(16)), (Col(Text("Tamaño de clúster", "Label"), Row(cluster, InfoBtn("Tamaño de clúster", ClusterInfo))), Auto)).Also(g => g.Margin = new Thickness(0, 0, 0, 14)),
            Row(quick, InfoBtn("Formato rápido o completo", QuickInfo)).Also(r => r.Margin = new Thickness(0, 0, 0, 6)),
            compress.Also(x => x.Margin = new Thickness(0, 0, 0, 10)),
            notes, warn, go), new Thickness(0, 0, 16, 0), 24);
        form.VerticalAlignment = VerticalAlignment.Top;

        var guide = Col(Text("¿Qué formato elijo?", "SectionTitle"));
        foreach (var g in Guide)
        {
            var gg = g;
            var pros = Col(g.pros.Select(p => (UIElement)Row(Icon(Icons.Check, 11, Brushes2.Good, new Thickness(0, 0, 8, 0)), Text(p, "Caption", 12).Also(t => { t.TextTrimming = TextTrimming.None; t.MaxWidth = 300; }))).ToArray());
            var cons = Col(g.cons.Select(p => (UIElement)Row(Icon(Icons.Close, 11, Brushes2.Bad, new Thickness(0, 0, 8, 0)), Text(p, "Caption", 12).Also(t => { t.TextTrimming = TextTrimming.None; t.MaxWidth = 300; }))).ToArray());
            var works = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var w in g.works) works.Children.Add(Chip(w).Also(x => x.Margin = new Thickness(0, 0, 6, 4)));
            var card = new Border
            {
                Style = S("Card"), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.Hand,
                Child = Col(Row(Text(g.title, "CardTitle", 16), Text("  " + g.tag, "Caption", 12)), pros.Also(p => p.Margin = new Thickness(0, 8, 0, 2)), cons, works)
            };
            card.MouseLeftButtonUp += (_, _) => fsButtons[gg.fs].IsChecked = true;
            guideCards[g.fs] = card;
            guide.Children.Add(card);
        }

        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        var header = Col(Text("Formatear", "PageTitle"), Text("Prepara pendrives, tarjetas y discos con el sistema de archivos que necesites.", "PageSubtitle").Also(t => t.Margin = new Thickness(0, 4, 0, 18)));
        DockPanel.SetDock(header, Dock.Top); page.Children.Add(header);
        page.Children.Add(Columns((form, new GridLength(1, GridUnitType.Star)), (guide, Px(380))).Also(g => g.MaxWidth = 1200).Also(g => g.HorizontalAlignment = HorizontalAlignment.Left));
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        modeVolume.Checked += (_, _) => Refill(); modeDisk.Checked += (_, _) => Refill();
        showInternal.Click += (_, _) => Refill();
        target.SelectionChanged += (_, _) => { userLabel = false; Update(); };
        label.PreviewKeyDown += (_, _) => userLabel = true;
        foreach (var x in new Control[] { cluster }) ((ComboBox)x).SelectionChanged += (_, _) => Update();
        quick.Click += (_, _) => Update(); compress.Click += (_, _) => Update();
        tMbr.Checked += (_, _) => Update(); tGpt.Checked += (_, _) => Update();
        label.TextChanged += (_, _) => Update();
        AppState.Changed += Refill;
        Refill();
        SetFs(fs);
    }

    static Border Seg(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) sp.Children.Add(i);
        var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = sp, HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return b;
    }

    record Target(VolumeInfo Volume, PhysicalDisk Disk)
    {
        public override string ToString() => Volume != null
            ? $"{Volume.Letter}  {Volume.Name} · {Fmt.Size(Volume.Total)} · {Volume.FileSystem}" + (string.IsNullOrEmpty(Volume.Bus) ? "" : " · " + Volume.Bus)
            : $"Disco {Disk.Number} · {Disk.Title} · {Fmt.Size(Disk.Size)} · {Disk.Bus}" + (Disk.Volumes.Count > 0 ? "  (" + string.Join(", ", Disk.Volumes.Select(v => v.Letter)) + ")" : "  (sin volúmenes)");
    }

    Target Selected => target.SelectedItem as Target;
    bool DiskMode => modeDisk.IsChecked == true;

    void Refill()
    {
        var prev = Selected;
        bool all = showInternal.IsChecked == true;
        List<Target> items = DiskMode
            ? AppState.Disks.Where(d => !d.IsSystem && !d.IsBoot && (all || d.IsRemovable)).Select(d => new Target(null, d)).ToList()
            : AppState.Volumes.Where(v => v.Ready && !v.IsSystem && v.Type is DriveType.Fixed or DriveType.Removable && (all || v.IsRemovable)).Select(v => new Target(v, null)).ToList();
        target.ItemsSource = items;
        target.SelectedItem = items.FirstOrDefault(t => prev != null && (t.Volume?.Letter == prev.Volume?.Letter && t.Disk?.Number == prev.Disk?.Number)) ?? items.FirstOrDefault();
        tableRow.Visibility = DiskMode ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    public void Prepare(VolumeInfo v)
    {
        modeVolume.IsChecked = true;
        if (!v.IsRemovable) showInternal.IsChecked = true;
        Refill();
        target.SelectedItem = ((List<Target>)target.ItemsSource).FirstOrDefault(t => t.Volume?.Letter == v.Letter);
    }

    public void PrepareDisk(PhysicalDisk d)
    {
        modeDisk.IsChecked = true;
        if (!d.IsRemovable) showInternal.IsChecked = true;
        Refill();
        target.SelectedItem = ((List<Target>)target.ItemsSource).FirstOrDefault(t => t.Disk?.Number == d.Number);
    }

    void SetFs(string f)
    {
        fs = f;
        foreach (var (k, card) in guideCards)
        {
            if (k == f) { card.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush"); card.BorderThickness = new Thickness(2); }
            else { card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush"); card.BorderThickness = new Thickness(1); }
        }
        compress.IsEnabled = f == "NTFS";
        if (f != "NTFS") compress.IsChecked = false;
        Update();
    }

    long Size => Selected?.Volume?.Total ?? Selected?.Disk?.Size ?? 0;
    int ClusterBytes => cluster.SelectedIndex switch { 1 => 4096, 2 => 8192, 3 => 16384, 4 => 32768, 5 => 65536, _ => 0 };

    FormatOptions Options() => new()
    {
        FileSystem = fs, Label = Formatter.CleanLabel(label.Text, fs), ClusterSize = ClusterBytes, Quick = quick.IsChecked == true,
        Compress = compress.IsChecked == true, Table = tGpt.IsChecked == true ? TableStyle.Gpt : TableStyle.Mbr
    };

    void Update()
    {
        if (go == null) return;
        notes.Children.Clear();
        var t = Selected;
        go.IsEnabled = t != null;
        if (t == null)
        {
            targetInfo.Text = showInternal.IsChecked == true ? "No hay unidades disponibles." : "No hay pendrives ni discos externos conectados. Marca «Mostrar también discos internos» para ver el resto.";
            warnText.Text = "";
            return;
        }
        if (!userLabel)
        {
            string current = t.Volume?.Label ?? t.Disk?.Volumes.OrderByDescending(x => x.Total).FirstOrDefault()?.Label ?? "";
            label.Text = Formatter.CleanLabel(current.Length > 0 ? current : (t.Disk ?? AppState.Disks.Find(d => d.Number == t.Volume.DiskNumber))?.IsRemovable == true ? "USB" : "DATOS", fs);
            userLabel = false;
        }
        if (t.Volume != null)
        {
            var v = t.Volume;
            targetInfo.Text = $"{v.KindText} · {v.FileSystem} · {Fmt.Size(v.Total - v.Free)} usados de {Fmt.Size(v.Total)}" + (string.IsNullOrEmpty(v.DiskModel) ? "" : $" · {v.DiskModel}");
            warnText.Text = $"Se borrará todo el contenido de {v.Letter} «{v.Name}» ({Fmt.Size(v.Total - v.Free)} de datos). El resto del disco no se toca.";
        }
        else
        {
            var d = t.Disk;
            targetInfo.Text = $"{d.Partitions.Count} partición(es) · tabla {d.PartitionStyle} · {d.Bus}" + (d.Media != "" ? $" · {d.Media}" : "");
            warnText.Text = $"Se borrarán TODAS las particiones y datos del disco {d.Number} «{d.Title}» ({Fmt.Size(d.Size) + (d.Volumes.Count > 0 ? ", unidades " + string.Join(", ", d.Volumes.Select(v => v.Letter)) : "")}).";
        }
        var o = Options();
        var probs = Formatter.Problems(Size, o, t.Disk?.SectorSize ?? 512);
        if (DiskMode && o.Table == TableStyle.Mbr && Size > 0xFFFFFFFFL * 512) probs.Add("MBR solo puede usar 2 TB de este disco: elige GPT.");
        foreach (var p in probs) note(Icons.Error, p, Brushes2.Bad);
        if (fs == "FAT32" && Size > Formatter.FatLimitWindows) note(Icons.Info, "Windows no permite FAT32 de más de 32 GB: Disk Studio Suite usará su propio formateador FAT32.", Brushes2.Accent);
        if (fs == "FAT32") note(Icons.Info, "En FAT32 cada archivo puede ocupar como máximo 4 GB.", Brushes2.Accent);
        if (!o.Quick) note(Icons.Clock, $"El formato completo escribe todo el volumen: puede tardar bastante ({Fmt.Size(Size)}).", Brushes2.Caution);
        if (t.Volume != null && string.Equals(Path.GetPathRoot(AppContext.BaseDirectory), t.Volume.Root, StringComparison.OrdinalIgnoreCase))
            note(Icons.Error, "Disk Studio Suite se está ejecutando desde esta unidad: cópialo a otro disco antes de formatearla.", Brushes2.Bad);
        if (o.Label != label.Text.Trim() && label.Text.Trim().Length > 0) note(Icons.Info, $"El nombre se guardará como «{o.Label}» (límite de {fs}).", Brushes2.Accent);
        go.IsEnabled = probs.Count == 0;

        void note(string glyph, string text, Brush b) => notes.Children.Add(Row(Icon(glyph, 12, b, new Thickness(0, 0, 8, 0)), Text(text, "Caption", 12).Also(x => { x.TextTrimming = TextTrimming.None; x.TextWrapping = TextWrapping.Wrap; x.MaxWidth = 540; })).Also(r => r.Margin = new Thickness(0, 2, 0, 2)));
    }

    async void Start()
    {
        var t = Selected;
        if (t == null) return;
        if (!Native.IsAdmin)
        {
            if (await Dialogs.Confirm("Se necesita administrador", "Formatear requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin();
            return;
        }
        var o = Options();
        if (TransferQueue.Active != null && (TransferQueue.Active.Subtitle + TransferQueue.Active.Title).Contains(t.Volume?.Letter ?? "Disco " + t.Disk?.Number))
        { await Dialogs.Error("Unidad en uso", "Hay una operación en curso con esta unidad."); return; }
        string phrase = t.Volume != null ? $"FORMATEAR {t.Volume.Letter}" : $"BORRAR DISCO {t.Disk.Number}";
        if (!await Dialogs.TypeToConfirm(t.Volume != null ? $"Formatear {t.Volume.Letter} como {o.FileSystem}" : $"Reiniciar el disco {t.Disk.Number}",
            warnText.Text + " No se puede deshacer.", phrase, "Formatear")) return;
        TransferQueue.Enqueue(t.Volume != null ? Formatter.FormatVolumeJob(t.Volume, o) : Formatter.ResetDiskJob(t.Disk, o));
        Toast.Show("Formateo en cola", t.ToString(), Icons.Drive);
        MainWindow.Instance.Navigate("transfers");
    }

    public Task Demo(string action)
    {
        showInternal.IsChecked = true;
        if (action == "disk") modeDisk.IsChecked = true;
        Refill();
        if (action == "fat32") { var v = AppState.Volumes.FirstOrDefault(x => x.IsRemovable); if (v != null) Prepare(v); fsButtons["FAT32"].IsChecked = true; quick.IsChecked = false; Update(); }
        return Task.CompletedTask;
    }
}
