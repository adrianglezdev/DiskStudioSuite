using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Crear un pendrive de arranque desde una ISO (instalación de Windows, Linux, utilidades…).</summary>
public class BootUsbView : UserControl
{
    readonly TextBox isoPath = new();
    readonly StackPanel isoInfo = new() { Margin = new Thickness(0, 6, 0, 14) };
    readonly ComboBox target = new() { MinWidth = 460 };
    readonly CheckBox showAll = new() { Content = "Mostrar también discos internos", Margin = new Thickness(0, 8, 0, 14) };
    readonly RadioButton mUefi = new() { Style = S("SegmentItem"), Tag = Icons.Drive, Content = "UEFI (GPT)", GroupName = "bmode" };
    readonly RadioButton mBoth = new() { Style = S("SegmentItem"), Tag = Icons.Usb, Content = "UEFI + BIOS (MBR)", GroupName = "bmode", IsChecked = true };
    readonly RadioButton mDd = new() { Style = S("SegmentItem"), Tag = Icons.Image, Content = "Imagen DD", GroupName = "bmode" };
    readonly TextBox label = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel notes = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly TextBlock warn = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = 600 };
    readonly Button go;
    IsoInfo iso;

    static readonly (string, string)[] ModeInfo =
    [
        ("UEFI + BIOS (MBR)", "La opción más compatible para instalar Windows: arranca en equipos modernos (UEFI) y antiguos (BIOS). Se copian los archivos de la ISO a una partición FAT32."),
        ("UEFI (GPT)", "Solo para equipos UEFI. Necesaria en pendrives de más de 2 TB. También copia los archivos a FAT32."),
        ("Imagen DD", "Escribe la ISO tal cual, sector a sector, como hace «dd» en Linux. Es lo que necesitan muchas distribuciones Linux y utilidades (ISO «híbridas»). El pendrive quedará con el formato de la ISO y Windows puede mostrarlo raro: es normal."),
        ("Archivos de más de 4 GB", "FAT32 no admite archivos de más de 4 GB. Si la ISO de Windows trae un install.wim más grande, Disk Studio Suite lo divide automáticamente en partes .swm con DISM: el instalador de Windows las reconoce."),
    ];

    public BootUsbView()
    {
        go = Btn("Crear USB de arranque", Icons.Usb, (_, _) => Start(), "DangerButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);
        var c = ((SolidColorBrush)Brushes2.Bad).Color;
        warn.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var warnBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 16), Background = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B)), Child = Row(Icon(Icons.Warning, 16, Brushes2.Bad, new Thickness(0, 0, 12, 0)), warn) };

        var pick = Columns((isoPath, Star), (Btn("Elegir ISO…", Icons.FolderOpen, (_, _) => { var p = Dialogs.PickFile("Imagen ISO|*.iso;*.img|Todos|*.*", "Elegir la ISO"); if (p != null) isoPath.Text = p; }, "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0)), Auto));
        var form = Card(Col(
            Text("Imagen ISO", "Label"), pick, isoInfo,
            Text("Pendrive o disco de destino", "Label"), target, showAll,
            Row(Text("Modo", "Label"), InfoBtn("Modos de arranque", ModeInfo)), Seg(mBoth, mUefi, mDd).Also(s => s.Margin = new Thickness(0, 0, 0, 14)),
            Text("Nombre del volumen", "Label"), label.Also(l => l.Margin = new Thickness(0, 0, 0, 14)),
            notes, warnBox, go), new Thickness(0, 0, 16, 0), 24);
        form.VerticalAlignment = VerticalAlignment.Top;

        var tips = Col(Text("Consejos", "SectionTitle"),
            Tip(Icons.Download, "Descarga la ISO oficial", "Windows: desde la web de Microsoft («Descargar Windows 11», opción «Imagen de disco ISO»). Linux: desde la web de cada distribución."),
            Tip(Icons.Usb, "Usa un pendrive de 8 GB o más", "Todo su contenido se borrará."),
            Tip(Icons.Power, "Arrancar desde el USB", "Al encender el equipo, pulsa la tecla del menú de arranque (suele ser F12, F11, F8 o Esc) y elige el pendrive."),
            Tip(Icons.Shield, "Arranque seguro", "Windows y la mayoría de distribuciones Linux actuales arrancan con el Arranque seguro activado. Si no arranca, prueba a desactivarlo en la UEFI."));

        var page = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        var header = Col(Text("USB de arranque", "PageTitle"), Text("Crea un pendrive para instalar Windows o Linux, o para arrancar utilidades, a partir de una ISO.", "PageSubtitle").Also(t => t.Margin = new Thickness(0, 4, 0, 18)));
        DockPanel.SetDock(header, Dock.Top); page.Children.Add(header);
        page.Children.Add(Columns((form, Star), (tips, Px(340))).Also(g => { g.MaxWidth = 1200; g.HorizontalAlignment = HorizontalAlignment.Left; }));
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        isoPath.TextChanged += (_, _) => Inspect();
        showAll.Click += (_, _) => Refill();
        target.SelectionChanged += (_, _) => Update();
        foreach (var r in new[] { mUefi, mBoth, mDd }) r.Checked += (_, _) => Update();
        AppState.Changed += Refill;
        Refill();
        Inspect();
    }

    static UIElement Tip(string glyph, string title, string text) => new Border
    {
        Style = S("Card"), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10),
        Child = Columns((Icon(glyph, 18, B("AccentTextFillColorPrimaryBrush"), new Thickness(0, 2, 14, 0)).Also(i => i.VerticalAlignment = VerticalAlignment.Top), Auto),
            (Col(Text(title, "CardTitle", 14), Text(text, "Caption", 12).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 4, 0, 0); })), Star))
    };

    record Target(PhysicalDisk Disk)
    {
        public override string ToString() => $"Disco {Disk.Number} · {Disk.Title} · {Fmt.Size(Disk.Size)} · {Disk.Bus}" + (Disk.Volumes.Count > 0 ? "  (" + string.Join(", ", Disk.Volumes.Select(v => v.Letter)) + ")" : "");
    }

    void Refill()
    {
        var prev = (target.SelectedItem as Target)?.Disk.Number;
        var items = AppState.Disks.Where(d => !d.IsSystem && !d.IsBoot && d.Bus != "Virtual" && (showAll.IsChecked == true || d.IsRemovable)).Select(d => new Target(d)).ToList();
        target.ItemsSource = items;
        target.SelectedItem = items.FirstOrDefault(t => t.Disk.Number == prev) ?? items.FirstOrDefault();
        Update();
    }

    int inspectId;
    async void Inspect()
    {
        string p = isoPath.Text.Trim();
        iso = null;
        isoInfo.Children.Clear();
        if (!File.Exists(p)) { isoInfo.Children.Add(Text("Elige el archivo .iso que quieres grabar.", "Caption", 13)); Update(); return; }
        int id = ++inspectId;
        isoInfo.Children.Add(Text("Analizando la ISO…", "Caption", 13));
        IsoInfo info;
        try { info = await Task.Run(() => BootUsb.Inspect(p)); }
        catch (Exception ex) { if (id == inspectId) { isoInfo.Children.Clear(); isoInfo.Children.Add(Text("No se pudo leer la ISO: " + ex.Message, "Caption", 13).Also(t => t.Foreground = Brushes2.Bad)); } return; }
        if (id != inspectId) return;
        iso = info;
        isoInfo.Children.Clear();
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void chip(string t, bool on) => chips.Children.Add(Chip((on ? "✓ " : "✗ ") + t, on ? Brushes2.Good : null).Also(x => x.Margin = new Thickness(0, 0, 6, 4)));
        chip("Arranque UEFI", info.Efi); chip("Arranque BIOS", info.Bios); chip("ISO híbrida (DD)", info.Hybrid);
        isoInfo.Children.Add(Text($"{info.Kind} · {Fmt.Size(info.Size)}" + (info.Label.Length > 0 ? $" · «{info.Label}»" : ""), size: 14));
        isoInfo.Children.Add(chips);
        if (info.LargestFile >= BootUsb.Fat32Max) isoInfo.Children.Add(Text($"Archivo más grande: {info.LargestName} ({Fmt.Size(info.LargestFile)})" + (info.NeedsSplit ? " · se dividirá automáticamente" : ""), "Caption", 12));
        (info.Recommended switch { BootMode.Dd => mDd, BootMode.Uefi => mUefi, _ => mBoth }).IsChecked = true;
        label.Text = Formatter.CleanLabel(info.Label.Length > 0 ? info.Label : "USB", "FAT32");
        Update();
    }

    BootMode Mode => mDd.IsChecked == true ? BootMode.Dd : mUefi.IsChecked == true ? BootMode.Uefi : BootMode.UefiBios;

    void Update()
    {
        if (go == null) return;
        notes.Children.Clear();
        var t = target.SelectedItem as Target;
        label.IsEnabled = Mode != BootMode.Dd;
        warn.Text = t == null ? "Conecta un pendrive." : $"Se borrará TODO el contenido del disco {t.Disk.Number} «{t.Disk.Title}» ({Fmt.Size(t.Disk.Size) + (t.Disk.Volumes.Count > 0 ? ", unidades " + string.Join(", ", t.Disk.Volumes.Select(v => v.Letter)) : "")}).";
        var probs = iso != null && t != null ? BootUsb.Problems(iso, t.Disk, Mode) : [];
        foreach (var p in probs) note(Icons.Error, p, Brushes2.Bad);
        if (iso != null && Mode != BootMode.Dd && iso.NeedsSplit) note(Icons.Info, "install.wim ocupa más de 4 GB: se dividirá en partes .swm (tarda unos minutos más).", Brushes2.Accent);
        if (iso != null && Mode == BootMode.UefiBios && !iso.Windows) note(Icons.Info, "En ISOs que no son de Windows el arranque BIOS no está garantizado; en UEFI sí arrancará.", Brushes2.Accent);
        if (iso != null && Mode == BootMode.Dd && iso.Windows) note(Icons.Warning, "Las ISOs de Windows no son híbridas: escritas con DD no arrancan desde un USB.", Brushes2.Caution);
        go.IsEnabled = iso != null && t != null && probs.Count == 0;

        void note(string glyph, string text, Brush b) => notes.Children.Add(Row(Icon(glyph, 12, b, new Thickness(0, 0, 8, 0)), Text(text, "Caption", 12).Also(x => { x.TextTrimming = TextTrimming.None; x.TextWrapping = TextWrapping.Wrap; x.MaxWidth = 560; })).Also(r => r.Margin = new Thickness(0, 2, 0, 2)));
    }

    async void Start()
    {
        if (iso == null || target.SelectedItem is not Target t) return;
        if (!Native.IsAdmin)
        {
            if (await Dialogs.Confirm("Se necesita administrador", "Crear un USB de arranque requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin();
            return;
        }
        if (!await Dialogs.TypeToConfirm($"Crear USB de arranque en el disco {t.Disk.Number}", warn.Text + " No se puede deshacer.", $"BORRAR DISCO {t.Disk.Number}", "Crear")) return;
        TransferQueue.Enqueue(BootUsb.Job(iso, t.Disk, Mode, label.Text));
        Toast.Show("USB de arranque en cola", $"{Path.GetFileName(iso.Path)} → {t.Disk.Title}", Icons.Usb);
        MainWindow.Instance.Navigate("transfers");
    }
}
