using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

public class CloneView : UserControl, ISnapshotDemo
{
    public Task Demo(string action)
    {
        var d0 = AppState.Disks.FirstOrDefault(d => d.IsSystem) ?? AppState.Disks.FirstOrDefault();
        if (action == "gpt") Prepare("gpt", AppState.Disks.LastOrDefault());
        else Prepare("disk", d0);
        if (action == "disk" && body.Content is FrameworkElement fe)
            foreach (var cb in FindAll<ComboBox>(fe).Where(c => c.Items.Count == 3 && c.Items[0] is string s && s.StartsWith("Igual"))) cb.SelectedIndex = 1;
        if (action == "info") _ = Dialogs.Explain("Optimizar para SSD", SsdInfo);
        return Task.CompletedTask;
    }

    static IEnumerable<T> FindAll<T>(DependencyObject d) where T : DependencyObject
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (c is T t) yield return t;
            foreach (var x in FindAll<T>(c)) yield return x;
        }
    }

    readonly ContentControl body = new();
    readonly Dictionary<string, RadioButton> tabs = [];
    readonly Dictionary<string, Func<UIElement>> builders;
    PhysicalDisk preset;
    string presetSrc, presetDst;

    record DiskOption(PhysicalDisk Disk)
    {
        public override string ToString() => $"Disco {Disk.Number} · {Disk.Title} · {Fmt.Size(Disk.Size)} · {Disk.Bus}" + (Disk.IsSystem ? " · SISTEMA" : "") +
            (Disk.Volumes.Count > 0 ? "  (" + string.Join(", ", Disk.Volumes.Select(v => v.Letter)) + ")" : "");
    }

    public CloneView()
    {
        builders = new()
        {
            ["files"] = FilesPanel, ["image"] = ImagePanel, ["restore"] = RestorePanel,
            ["disk"] = DiskPanel, ["gpt"] = GptPanel, ["verify"] = VerifyPanel, ["surface"] = SurfacePanel
        };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, glyph, text) in new[] { ("files", Icons.Folder, "Clonar archivos"), ("image", Icons.Image, "Crear imagen"), ("restore", Icons.Download, "Restaurar imagen"),
                                                   ("disk", Icons.Clone, "Clonar disco"), ("gpt", Icons.Swap, "MBR ↔ GPT"), ("verify", Icons.Check, "Verificar imagen"), ("surface", Icons.Grid, "Superficie") })
        {
            var rb = new RadioButton { Style = S("SegmentItem"), Tag = glyph, Content = text, GroupName = "clone" };
            string k = key;
            rb.Checked += (_, _) => Show(k);
            tabs[key] = rb;
            seg.Children.Add(rb);
        }
        var segBorder = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Margin = new Thickness(0, 0, 0, 18), HorizontalAlignment = HorizontalAlignment.Left, Child = seg };
        segBorder.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

        var root = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        var header = Col(Text("Clonar e imágenes", "PageTitle"),
            Text("Copias exactas de carpetas o discos completos, imágenes sector a sector y comprobación de superficie.", "PageSubtitle").Also(t => t.Margin = new Thickness(0, 4, 0, 18)));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(segBorder, Dock.Top); root.Children.Add(segBorder);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        // Reconstruir el formulario solo si cambian los discos conectados (no en cada reescaneo)
        string sig = "";
        AppState.Changed += () =>
        {
            string now = string.Join("|", AppState.Disks.Select(d => d.Number + d.Serial)) + string.Join("|", AppState.Volumes.Select(v => v.Letter));
            if (now != sig && sig != "" && current != null && current != "surface") Show(current, true);
            sig = now;
        };
        tabs["files"].IsChecked = true;
    }

    string current;
    void Show(string key, bool force = false)
    {
        if (current == key && !force) return;
        current = key;
        body.Content = builders[key]();
        preset = null; presetSrc = presetDst = null;
    }

    public void Prepare(string mode, PhysicalDisk d)
    {
        preset = d;
        var p = preset;
        if (tabs[mode].IsChecked == true) Show(mode, true); else tabs[mode].IsChecked = true;
        preset = p;
        Show(mode, true);
    }

    public void PrepareFiles(string src, string dst)
    {
        presetSrc = src; presetDst = dst;
        var (s, d) = (src, dst);
        if (tabs["files"].IsChecked != true) tabs["files"].IsChecked = true;
        presetSrc = s; presetDst = d;
        Show("files", true);
    }

    // ───────────── Utilidades de formulario ─────────────
    static UIElement Field(string label, UIElement control, string hint = null) =>
        Col(Text(label, "Label"), control, hint == null ? null : Text(hint, "Caption", margin: new Thickness(0, 4, 0, 0))).Also(sp => sp.Margin = new Thickness(0, 0, 0, 14));

    static Grid PathPicker(TextBox tb, Func<string> pick)
    {
        var b = IconBtn(Icons.FolderOpen, "Examinar…", (_, _) => { var p = pick(); if (p != null) tb.Text = p; });
        return Columns((tb, Star), (b.Also(x => x.Margin = new Thickness(6, 0, 0, 0)), Auto));
    }

    ComboBox DiskCombo(Func<PhysicalDisk, bool> filter = null)
    {
        var cb = new ComboBox { ItemsSource = AppState.Disks.Where(d => filter == null || filter(d)).Select(d => new DiskOption(d)).ToList(), MinWidth = 420 };
        var list = (List<DiskOption>)cb.ItemsSource;
        cb.SelectedItem = list.Find(o => o.Disk == preset) ?? list.FirstOrDefault();
        return cb;
    }

    static UIElement Warning(string text, bool danger = false)
    {
        var brush = danger ? Brushes2.Bad : Brushes2.Caution;
        var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1), BorderBrush = brush };
        var c = ((SolidColorBrush)brush).Color;
        b.Background = new SolidColorBrush(Color.FromArgb(24, c.R, c.G, c.B));
        b.Child = Columns((Icon(Icons.Warning, 16, brush, new Thickness(0, 0, 12, 0)), Auto), (Text(text, size: 13).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }), Star));
        return b;
    }

    static UIElement AdminNotice()
    {
        if (Native.IsAdmin) return null;
        return Col(Warning("Esta función lee o escribe el disco a bajo nivel y requiere ejecutar Disk Studio Suite como administrador."),
                   Btn("Reiniciar como administrador", Icons.Admin, (_, _) => Native.RestartAsAdmin(), "PrimaryButton").Also(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 0, 0, 14); }));
    }

    static UIElement Panel(string glyph, string title, string desc, params UIElement[] content)
    {
        var head = Columns((Tile(glyph, null, 44), Auto), (Col(Text(title, "CardTitle", 17), Text(desc, "Caption", 13).Also(t => t.Margin = new Thickness(0, 2, 0, 0))).Also(c => c.Margin = new Thickness(14, 0, 0, 0)), Star));
        head.Margin = new Thickness(0, 0, 0, 20);
        var sp = Col(head);
        foreach (var c in content) if (c != null) sp.Children.Add(c);
        return Card(sp, new Thickness(0, 0, 0, 0), 24).Also(b => b.MaxWidth = 860).Also(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.VerticalAlignment = VerticalAlignment.Top; });
    }

    // ───────────── Clonar archivos ─────────────
    UIElement FilesPanel()
    {
        var src = new ComboBox { IsEditable = true, ItemsSource = AppState.Volumes.Where(v => v.Ready).Select(v => v.Root).ToList(), Text = presetSrc ?? "" };
        var srcRow = Columns((src, Star), (IconBtn(Icons.FolderOpen, "Elegir carpeta", (_, _) => { var p = Dialogs.PickFolder(src.Text); if (p != null) src.Text = p; }).Also(b => b.Margin = new Thickness(6, 0, 0, 0)), Auto));
        var dst = new TextBox { Text = presetDst ?? "" };
        var mirror = new RadioButton { Content = "Espejo: el destino queda idéntico (borra lo que sobra)", GroupName = "fm", Margin = new Thickness(0, 0, 0, 6) };
        var update = new RadioButton { Content = "Incremental: copia solo lo nuevo o modificado, sin borrar", GroupName = "fm", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        var verify = new CheckBox { Content = "Verificar cada archivo tras copiarlo", Margin = new Thickness(0, 8, 0, 6) };
        var hidden = new CheckBox { Content = "Incluir archivos ocultos y de sistema", IsChecked = true, Margin = new Thickness(0, 0, 0, 14) };
        var exclude = new TextBox();
        var info = Text("", "Caption", margin: new Thickness(0, 0, 0, 14));
        void upd()
        {
            try
            {
                string s = src.Text, d = dst.Text;
                string t = "";
                if (Directory.Exists(s) && Path.GetPathRoot(s) == s && AppState.VolumeOf(s) is VolumeInfo sv) t += $"Usado en origen: {Fmt.Size(sv.Total - sv.Free)}. ";
                if (d.Length >= 3 && AppState.VolumeOf(d) is VolumeInfo dv) t += $"Libre en destino ({dv.Letter}): {Fmt.Size(dv.Free)}.";
                info.Text = t;
            }
            catch { }
        }
        src.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            upd();
            if (string.IsNullOrEmpty(dst.Text) || dst.Tag is "auto")
            {
                string s = src.Text.Trim();
                var ext = AppState.Volumes.FirstOrDefault(v => v.Ready && v.IsRemovable && !s.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase));
                if (ext != null && s.Length >= 2)
                {
                    string name = Path.GetFileName(s.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(name)) name = "Clon_" + s[0] + (AppState.VolumeOf(s) is { Label.Length: > 0 } v ? "_" + v.Label : "");
                    dst.Text = Path.Combine(ext.Root, name); dst.Tag = "auto";
                }
            }
        }));
        dst.TextChanged += (_, _) => { upd(); };
        dst.PreviewKeyDown += (_, _) => dst.Tag = null;
        upd();

        var go = Btn("Clonar", Icons.Clone, async (_, _) =>
        {
            string s = src.Text.Trim(), d = dst.Text.Trim();
            if (!Directory.Exists(s)) { await Dialogs.Error("Origen no válido", "Elige una unidad o carpeta existente."); return; }
            if (d.Length < 3 || !Path.IsPathRooted(d)) { await Dialogs.Error("Destino no válido", "Indica una carpeta de destino."); return; }
            if (FileOps.IsInside(d, s) || FileOps.IsInside(s, d)) { await Dialogs.Error("Carpetas anidadas", "El destino no puede estar dentro del origen ni al revés."); return; }
            if (mirror.IsChecked == true && Directory.Exists(d) && Directory.EnumerateFileSystemEntries(d).Any() &&
                !await Dialogs.Confirm("Modo espejo", $"Se BORRARÁN de\n{d}\nlos archivos que no existan en el origen.", "Clonar", danger: true)) return;
            var opt = new CopyOptions { Verify = verify.IsChecked == true, Filter = new FileFilter { Exclude = exclude.Text, IncludeHidden = hidden.IsChecked == true } };
            TransferQueue.Enqueue(FolderSync.SyncJob($"Clonar {s}", s, d, mirror.IsChecked == true ? SyncMode.Mirror : SyncMode.Update, opt, false, Icons.Clone));
            MainWindow.Instance.Navigate("transfers");
        }, "PrimaryButton");
        go.HorizontalAlignment = HorizontalAlignment.Left;

        return Panel(Icons.Folder, "Clonar archivos", "Copia exacta de una unidad o carpeta conservando fechas y atributos. Ideal para copias de seguridad.",
            Field("Origen (unidad o carpeta)", srcRow),
            Field("Destino", PathPicker(dst, () => Dialogs.PickFolder(dst.Text, "Carpeta de destino"))),
            info,
            Col(update, mirror, verify, hidden),
            Field("Excluir (opcional)", exclude, "Separados por ; — por ejemplo: *.tmp; node_modules; Thumbs.db"),
            Text("Es un clonado a nivel de archivos: no copia el sector de arranque. Para un disco de sistema arrancable usa «Clonar disco».", "Caption", margin: new Thickness(0, 0, 0, 16)).Also(t => t.TextWrapping = TextWrapping.Wrap),
            go);
    }

    // ───────────── Crear imagen ─────────────
    UIElement ImagePanel()
    {
        var disk = DiskCombo();
        var file = new TextBox();
        var compress = new CheckBox { Content = "Comprimir (.img.gz) — ocupa menos, sobre todo si el disco tiene mucho espacio vacío", Margin = new Thickness(0, 0, 0, 14) };
        void suggest()
        {
            if (disk.SelectedItem is not DiskOption o) return;
            var ext = AppState.Volumes.FirstOrDefault(v => v.Ready && v.DiskNumber != o.Disk.Number && v.Free > 0 && !v.IsSystem) ?? AppState.Volumes.First(v => v.Ready);
            string name = $"Disco{o.Disk.Number}_{new string(o.Disk.Title.Where(char.IsLetterOrDigit).Take(24).ToArray())}_{DateTime.Now:yyyyMMdd}.img" + (compress.IsChecked == true ? ".gz" : "");
            file.Text = Path.Combine(ext.Root, name);
        }
        disk.SelectionChanged += (_, _) => suggest();
        compress.Checked += (_, _) => { if (!file.Text.EndsWith(".gz")) file.Text += ".gz"; };
        compress.Unchecked += (_, _) => { if (file.Text.EndsWith(".gz")) file.Text = file.Text[..^3]; };
        suggest();
        var go = Btn("Crear imagen", Icons.Image, async (_, _) =>
        {
            if (!Native.IsAdmin) { Native.RestartAsAdmin(); return; }
            if (disk.SelectedItem is not DiskOption o) return;
            string f = file.Text.Trim();
            if (o.Disk.Volumes.Any(v => f.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase)))
            { await Dialogs.Error("Destino no válido", "No puedes guardar la imagen dentro del mismo disco que estás copiando."); return; }
            var vol = AppState.VolumeOf(f);
            if (vol != null && compress.IsChecked != true && vol.Free < o.Disk.Size)
                if (!await Dialogs.Confirm("Poco espacio", $"El destino tiene {Fmt.Size(vol.Free)} libres y la imagen ocupará {Fmt.Size(o.Disk.Size)}. ¿Continuar igualmente?", "Continuar", danger: true)) return;
            if (vol?.FileSystem == "FAT32" && o.Disk.Size > uint.MaxValue)
            { await Dialogs.Error("FAT32 no admite archivos de más de 4 GB", "Elige un destino NTFS o exFAT."); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            TransferQueue.Enqueue(DiskImage.CreateImageJob(o.Disk, f, compress.IsChecked == true));
            MainWindow.Instance.Navigate("transfers");
        }, "PrimaryButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);

        return Panel(Icons.Image, "Crear imagen de disco", "Copia sector a sector de un disco completo en un archivo .img (con su hash SHA-256). Solo lee el disco de origen.",
            AdminNotice(),
            Field("Disco de origen", disk),
            Field("Archivo de imagen", PathPicker(file, () => Dialogs.SaveFile("Imagen de disco|*.img;*.img.gz", Path.GetFileName(file.Text), Path.GetDirectoryName(file.Text)))),
            compress, go);
    }

    // ───────────── Restaurar imagen ─────────────
    UIElement RestorePanel()
    {
        var file = new TextBox();
        var disk = DiskCombo(d => !d.IsSystem && !d.IsBoot);
        var go = Btn("Restaurar imagen", Icons.Download, async (_, _) =>
        {
            if (!Native.IsAdmin) { Native.RestartAsAdmin(); return; }
            if (disk.SelectedItem is not DiskOption o) return;
            string f = file.Text.Trim();
            if (!File.Exists(f)) { await Dialogs.Error("Imagen no encontrada", f); return; }
            if (o.Disk.Volumes.Any(v => f.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase))) { await Dialogs.Error("No permitido", "La imagen está dentro del disco de destino."); return; }
            long len = new FileInfo(f).Length;
            if (!f.EndsWith(".gz") && len > o.Disk.Size) { await Dialogs.Error("Disco demasiado pequeño", $"La imagen ocupa {Fmt.Size(len)} y el disco {Fmt.Size(o.Disk.Size)}."); return; }
            if (!await Dialogs.TypeToConfirm("Se borrará el disco de destino",
                $"Todo el contenido de «{o.Disk.Title}» ({Fmt.Size(o.Disk.Size)}" + (o.Disk.Volumes.Count > 0 ? ", unidades " + string.Join(", ", o.Disk.Volumes.Select(v => v.Letter)) : "") + ") será sustituido por la imagen. No se puede deshacer.",
                $"BORRAR DISCO {o.Disk.Number}", "Restaurar")) return;
            TransferQueue.Enqueue(DiskImage.RestoreJob(f, o.Disk));
            MainWindow.Instance.Navigate("transfers");
        }, "DangerButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);

        return Panel(Icons.Download, "Restaurar imagen", "Escribe una imagen .img o .img.gz en un disco, sector a sector.",
            AdminNotice(),
            Warning("Esta operación BORRA por completo el disco de destino. El disco del sistema no aparece en la lista.", true),
            Field("Archivo de imagen", PathPicker(file, () => Dialogs.PickFile("Imagen de disco|*.img;*.gz;*.bin;*.iso|Todos|*.*"))),
            Field("Disco de destino", disk), go);
    }

    // ───────────── Clonar disco ─────────────
    static readonly (string, string)[] SsdInfo =
    [
        ("", "Al clonar a un SSD, Disk Studio Suite aplica solo lo que de verdad mejora el resultado del clonado:"),
        ("1 · Alinea las particiones a 1 MiB", "Cada partición empieza en un múltiplo exacto de 1 MiB. Si quedara desalineada (algo típico en discos de la época de Windows XP), cada escritura afectaría a dos bloques internos del SSD: rinde menos y se desgasta más."),
        ("2 · Copia solo los bloques en uso", "En las particiones NTFS lee el mapa del sistema de archivos y no copia el espacio vacío. El clonado es mucho más rápido y el SSD no queda lleno de bloques «ocupados» que en realidad están libres."),
        ("3 · Aplica TRIM al terminar", "Avisa al SSD de qué bloques están libres para que mantenga su velocidad desde el primer día."),
        ("Lo que no hace", "No desactiva el archivo de paginación, la hibernación, Prefetch/SysMain ni la indexación, y no desfragmenta. Hoy no aportan nada o perjudican: Windows ya trata correctamente los SSD."),
        ("Ten en cuenta", "Al recolocar las particiones el clon deja de ser una copia sector a sector exacta, aunque el contenido de cada partición sí es idéntico. Si el disco tiene Windows, el arranque se reconstruye automáticamente. También sirve para discos duros normales: solo te ahorras el TRIM."),
    ];

    static readonly (string, string)[] StyleInfo =
    [
        ("Igual que el origen", "Se mantiene el formato actual (MBR o GPT)."),
        ("GPT · arranque UEFI", "El estándar actual. Admite discos de más de 2 TB y hasta 128 particiones. Es obligatorio para Windows 11 y para el Arranque seguro."),
        ("MBR · BIOS heredado", "Solo para equipos antiguos sin UEFI. Admite como máximo 2 TB y 4 particiones principales."),
        ("Si el disco tiene Windows y cambias el formato", "• MBR → GPT: se crea una partición EFI (260 MB) y otra MSR (16 MB), se quita la «Reservada para el sistema» y se rehace el arranque UEFI con bcdboot.\n• GPT → MBR: la partición EFI pasa a ser la partición activa y se rehace el arranque BIOS.\nDespués tendrás que cambiar el modo de arranque en la BIOS/UEFI del equipo (UEFI, o Legacy/CSM)."),
        ("Seguridad", "El disco de origen no se modifica nunca. Solo se escribe en el disco de destino."),
    ];

    static readonly (string, string)[] SnapInfo =
    [
        ("Instantánea VSS", "Antes de copiar, Disk Studio Suite congela una «foto» de cada partición NTFS con el Servicio de instantáneas de volumen de Windows, y copia desde esa foto."),
        ("Para qué sirve", "El clon es coherente aunque Windows o tus programas estén escribiendo en el disco mientras se copia; por ejemplo, al clonar el disco del sistema que estás usando. La instantánea se borra al terminar."),
    ];

    UIElement DiskPanel()
    {
        var src = DiskCombo();
        var p = preset; preset = null;
        var dst = DiskCombo(d => !d.IsSystem && !d.IsBoot);
        if (p != null) { var l = (List<DiskOption>)dst.ItemsSource; dst.SelectedItem = l.FirstOrDefault(o => o.Disk != p); }
        var style = new ComboBox { ItemsSource = new[] { "Igual que el origen", "GPT — arranque UEFI (recomendado)", "MBR — BIOS heredado" }, SelectedIndex = 0, Width = 300 };
        var ssd = new CheckBox { Content = "Optimizar para SSD", VerticalAlignment = VerticalAlignment.Center };
        var snap = new CheckBox { Content = "Copiar desde una instantánea (recomendado si el disco está en uso)", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        var info = Text("", "Caption", margin: new Thickness(0, 0, 0, 8)).Also(t => t.TextTrimming = TextTrimming.None);
        var preview = new PartitionMap { Margin = new Thickness(0, 6, 0, 8) };
        var previewTitle = Text("Así quedará el disco destino", "Label");
        var notes = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        bool userTouchedSsd = false;
        ssd.Click += (_, _) => userTouchedSsd = true;

        SmartCloneOptions opts() => new()
        {
            Style = style.SelectedIndex switch { 1 => TableStyle.Gpt, 2 => TableStyle.Mbr, _ => null },
            OptimizeSsd = ssd.IsChecked == true, UseSnapshot = snap.IsChecked == true
        };
        bool smart() => style.SelectedIndex != 0 || ssd.IsChecked == true;

        void upd()
        {
            notes.Children.Clear();
            if (src.SelectedItem is not DiskOption a || dst.SelectedItem is not DiskOption b) return;
            if (!userTouchedSsd) ssd.IsChecked = b.Disk.Media == "SSD";
            snap.IsEnabled = smart();
            if (a.Disk == b.Disk) { info.Text = "Origen y destino no pueden ser el mismo disco."; preview.Visibility = previewTitle.Visibility = Visibility.Collapsed; return; }
            if (!smart())
            {
                info.Text = b.Disk.Size < a.Disk.Size
                    ? $"⚠ El destino ({Fmt.Size(b.Disk.Size)}) es más pequeño que el origen ({Fmt.Size(a.Disk.Size)}). Activa «Optimizar para SSD» o cambia el formato para copiar partición a partición."
                    : $"Copia exacta sector a sector de {Fmt.Size(a.Disk.Size)}, conservando el formato {a.Disk.PartitionStyle}. El espacio extra del destino quedará sin asignar.";
                preview.Visibility = previewTitle.Visibility = Visibility.Collapsed;
                return;
            }
            var plan = SmartClone.Preview(a.Disk, b.Disk, opts());
            long used = plan.Target.Parts.Where(x => x.Source >= 0).Sum(x => x.Length);
            info.Text = $"Copia partición a partición en formato {plan.Target.Style.ToString().ToUpperInvariant()}. Las particiones ocupan {Fmt.Size(used)} y el destino tiene {Fmt.Size(b.Disk.Size)}.";
            var fake = new PhysicalDisk { Size = b.Disk.Size, Partitions = plan.Target.Parts.Select((x, i) => new PartitionInfo
            {
                Offset = x.Offset, Size = x.Length, Number = i + 1,
                Type = x.Role == "EFI" ? "EFI" : x.Role == "MSR" ? "Reserved" : x.Role == "Recuperación" ? "Recovery" : "Basic",
                IsSystem = x.Role == "EFI",
                Letter = x.Source >= 0 ? a.Disk.Partitions.OrderBy(q => q.Offset).ElementAtOrDefault(x.Source)?.Letter ?? "" : "",
                Label = x.Source >= 0 ? a.Disk.Partitions.OrderBy(q => q.Offset).ElementAtOrDefault(x.Source)?.Label ?? "" : "",
                Free = x.Source >= 0 ? a.Disk.Partitions.OrderBy(q => q.Offset).ElementAtOrDefault(x.Source)?.Free ?? 0 : 0,
                FileSystem = x.FormatAs ?? (x.Source >= 0 ? a.Disk.Partitions.OrderBy(q => q.Offset).ElementAtOrDefault(x.Source)?.FileSystem ?? "" : "")
            }).ToList() };
            preview.SetDisk(fake);
            preview.Visibility = previewTitle.Visibility = Visibility.Visible;
            foreach (var n in plan.Notes) notes.Children.Add(Row(Icon(Icons.Info, 12, Brushes2.Accent, new Thickness(0, 0, 8, 0)), Text(n, "Caption", 12).Also(t => t.TextTrimming = TextTrimming.None)).Also(r => r.Margin = new Thickness(0, 2, 0, 2)));
            foreach (var e in plan.Errors.Distinct()) notes.Children.Add(Row(Icon(Icons.Error, 12, Brushes2.Bad, new Thickness(0, 0, 8, 0)), Text(e, size: 12, fg: Brushes2.Bad).Also(t => t.TextTrimming = TextTrimming.None)).Also(r => r.Margin = new Thickness(0, 2, 0, 2)));
        }
        src.SelectionChanged += (_, _) => upd(); dst.SelectionChanged += (_, _) => { userTouchedSsd = false; upd(); };
        style.SelectionChanged += (_, _) => upd(); ssd.Click += (_, _) => upd(); snap.Click += (_, _) => upd();
        upd();

        var go = Btn("Clonar disco", Icons.Clone, async (_, _) =>
        {
            if (!Native.IsAdmin) { Native.RestartAsAdmin(); return; }
            if (src.SelectedItem is not DiskOption a || dst.SelectedItem is not DiskOption b || a.Disk == b.Disk) { upd(); return; }
            if (smart())
            {
                var plan = SmartClone.Preview(a.Disk, b.Disk, opts());
                if (!plan.Ok) { await Dialogs.Error("No se puede clonar así", string.Join("\n", plan.Errors.Distinct())); return; }
            }
            else if (b.Disk.Size < a.Disk.Size) { upd(); return; }
            if (!await Dialogs.TypeToConfirm("Se borrará el disco de destino",
                $"«{b.Disk.Title}» ({Fmt.Size(b.Disk.Size)}) se sobrescribirá con una copia de «{a.Disk.Title}». No se puede deshacer." +
                (a.Disk.IsSystem && !smart() ? "\n\nEstás clonando el disco del sistema en uso sin instantánea: cierra los programas abiertos para que la copia sea coherente." : ""),
                $"BORRAR DISCO {b.Disk.Number}", "Clonar")) return;
            TransferQueue.Enqueue(smart() ? SmartClone.Job(a.Disk, b.Disk, opts()) : DiskImage.CloneDiskJob(a.Disk, b.Disk));
            MainWindow.Instance.Navigate("transfers");
        }, "DangerButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);

        return Panel(Icons.Clone, "Clonar disco", "Copia de un disco a otro, incluidas las particiones y el arranque. Ideal para pasar a un SSD nuevo.",
            AdminNotice(),
            Warning("El disco de destino se borrará por completo. El disco del sistema nunca se puede usar como destino.", true),
            Field("Disco de origen", src), Field("Disco de destino", dst),
            Field("Formato del disco destino", Row(style, InfoBtn("Formato del disco destino: MBR o GPT", StyleInfo))),
            Row(ssd, InfoBtn("Optimizar para SSD", SsdInfo)).Also(r => r.Margin = new Thickness(0, 0, 0, 8)),
            Row(snap, InfoBtn("Copiar desde una instantánea", SnapInfo)).Also(r => r.Margin = new Thickness(0, 0, 0, 14)),
            info, previewTitle, preview, notes, go);
    }

    // ───────────── Convertir MBR ↔ GPT ─────────────
    static readonly (string, string)[] ConvertInfo =
    [
        ("Qué es", "MBR y GPT son dos formas de guardar la tabla de particiones de un disco. GPT es el formato moderno (UEFI, discos de más de 2 TB, hasta 128 particiones); MBR es el antiguo (BIOS heredado, máximo 2 TB y 4 particiones principales)."),
        ("Cómo se convierte sin perder datos", "Solo se reescribe la tabla de particiones: cada partición se queda exactamente en la misma posición y sus datos no se tocan. Antes de escribir nada se guarda una copia de seguridad de la tabla, que puedes restaurar desde aquí mismo."),
        ("Disco del sistema", "Para el disco donde está Windows se usa MBR2GPT, la herramienta oficial de Microsoft, que además crea el arranque UEFI. Solo funciona de MBR a GPT. Después hay que poner la BIOS/firmware en modo UEFI antes de reiniciar. Para pasar un sistema de GPT a MBR, usa «Clonar disco» eligiendo formato MBR."),
        ("Ajustes automáticos", "• GPT necesita unos 17 KB libres al final del disco para la copia de su tabla: si la última partición llega hasta el final (y es NTFS), se reduce 1 MB.\n• Al pasar a MBR se omite la partición reservada de Microsoft (MSR), que no contiene datos."),
        ("Limitaciones", "No se admiten discos dinámicos. Para pasar a MBR, el disco debe tener como máximo 4 particiones y no superar los 2 TB."),
    ];

    UIElement GptPanel()
    {
        var disk = DiskCombo();
        var status = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var backups = new StackPanel();
        Button go = null;
        TableStyle to = TableStyle.Gpt;
        bool viaMbr2Gpt = false;

        void check(bool ok, string text, bool warn = false) => status.Children.Add(Row(
            Icon(ok ? (warn ? Icons.Warning : Icons.Check) : Icons.Close, 13, ok ? (warn ? Brushes2.Caution : Brushes2.Good) : Brushes2.Bad, new Thickness(0, 0, 10, 0)),
            Text(text, size: 13).Also(t => t.TextTrimming = TextTrimming.None)).Also(r => r.Margin = new Thickness(0, 3, 0, 3)));

        void upd()
        {
            status.Children.Clear(); backups.Children.Clear();
            if (disk.SelectedItem is not DiskOption o) return;
            var d = o.Disk;
            var cur = PartitionTable.FromWmi(d);
            if (cur.Style == TableStyle.Raw) { check(false, "El disco no está inicializado: no hay tabla que convertir."); go.IsEnabled = false; return; }
            to = cur.Style == TableStyle.Mbr ? TableStyle.Gpt : TableStyle.Mbr;
            go.Content = $"Convertir a {to.ToString().ToUpperInvariant()}";
            status.Children.Add(Row(Chip("Ahora: " + cur.Style.ToString().ToUpperInvariant(), Brushes2.Accent), Text("→", size: 14, margin: new Thickness(4, 0, 10, 0)), Chip("Después: " + to.ToString().ToUpperInvariant(), Brushes2.Good))
                .Also(r => r.Margin = new Thickness(0, 0, 0, 10)));
            bool ok = true;
            viaMbr2Gpt = d.IsSystem || d.IsBoot;
            if (viaMbr2Gpt)
            {
                if (to == TableStyle.Mbr) { check(false, "Es el disco del sistema y está en GPT: Windows no permite pasarlo a MBR en uso. Usa «Clonar disco» con formato MBR."); ok = false; }
                else
                {
                    check(true, "Es el disco del sistema: se usará MBR2GPT, la herramienta oficial de Microsoft.");
                    int primaries = d.Partitions.Count;
                    check(primaries <= 3, primaries <= 3 ? $"{primaries} particiones (MBR2GPT admite hasta 3)." : $"Tiene {primaries} particiones y MBR2GPT admite como máximo 3.");
                    ok &= primaries <= 3;
                    check(true, "Antes de reiniciar tendrás que poner la BIOS en modo UEFI (sin CSM/Legacy).", true);
                }
            }
            else
            {
                var errs = new List<string>();
                var conv = PartitionTable.Convert(cur, to, i => d.Partitions.OrderBy(q => q.Offset).ElementAtOrDefault(i)?.FileSystem, errs);
                bool shrink = errs.RemoveAll(e => e.Contains("final del disco")) > 0;
                var lastFs = d.Partitions.OrderBy(q => q.Offset + q.Size).LastOrDefault()?.FileSystem;
                if (shrink && lastFs != "NTFS") { errs.Add("La última partición llega al final del disco y no es NTFS: no se puede reducir para hacer sitio a la tabla GPT."); shrink = false; }
                foreach (var e in errs.Distinct()) check(false, e);
                if (errs.Count == 0) check(true, $"{conv.Parts.Count} particiones compatibles: se conservan en la misma posición, sin mover datos.");
                if (shrink) check(true, "La última partición se reducirá 1 MB para hacer sitio a la copia de la tabla GPT.", true);
                if (to == TableStyle.Mbr && d.Partitions.Any(q => q.Type == "Reserved")) check(true, "Se omitirá la partición reservada de Microsoft (MSR), que no tiene datos.", true);
                check(true, "Se guardará una copia de seguridad de la tabla antes de escribir.");
                ok = errs.Count == 0;
            }
            go.IsEnabled = ok;

            var list = PartitionTable.ListBackups(d.Serial);
            if (list.Count == 0) backups.Children.Add(Text("No hay copias de la tabla de este disco.", "Caption"));
            foreach (var b in list.Take(6))
            {
                var bb = b;
                backups.Children.Add(new Border
                {
                    Style = S("SubtleCard"), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(12, 8, 8, 8),
                    Child = Columns((Icon(Icons.History, 14, margin: new Thickness(0, 0, 10, 0)), Auto),
                        (Text($"{b.Date:dd/MM/yyyy HH:mm} · tabla {b.Style.ToString().ToUpperInvariant()} · {b.Model}", size: 13), Star),
                        (Btn("Restaurar", Icons.Undo, async (_, _) =>
                        {
                            if (!Native.IsAdmin) { Native.RestartAsAdmin(); return; }
                            if (d.IsSystem || d.IsBoot) { await Dialogs.Error("No permitido", "No se puede restaurar la tabla del disco del sistema en uso."); return; }
                            if (!await Dialogs.TypeToConfirm("Restaurar tabla de particiones", $"Se volverá a la tabla {bb.Style} guardada el {bb.Date:dd/MM/yyyy HH:mm}.", $"RESTAURAR DISCO {d.Number}", "Restaurar")) return;
                            TransferQueue.Enqueue(PartitionTable.RestoreJob(d, bb.File));
                        }, "SecondaryButton"), Auto))
                });
            }
        }

        go = Btn("Convertir", Icons.Swap, async (_, _) =>
        {
            if (!Native.IsAdmin) { Native.RestartAsAdmin(); return; }
            if (disk.SelectedItem is not DiskOption o) return;
            var d = o.Disk;
            string extra = viaMbr2Gpt ? "\n\nIMPORTANTE: después de convertir, entra en la BIOS/UEFI y activa el modo UEFI (desactiva CSM/Legacy) o Windows no arrancará." : "";
            if (!await Dialogs.TypeToConfirm($"Convertir disco {d.Number} a {to.ToString().ToUpperInvariant()}",
                $"«{d.Title}» ({Fmt.Size(d.Size)}). Los datos se conservan, pero haz una copia de seguridad de lo importante antes de tocar la tabla de particiones.{extra}",
                $"CONVERTIR DISCO {d.Number}", "Convertir")) return;
            var job = viaMbr2Gpt ? PartitionTable.Mbr2GptJob(d) : PartitionTable.ConvertJob(d, to);
            TransferQueue.Enqueue(job);
            MainWindow.Instance.Navigate("transfers");
        }, "DangerButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);
        disk.SelectionChanged += (_, _) => upd();
        upd();

        return Panel(Icons.Swap, "Convertir MBR ↔ GPT", "Cambia el formato de la tabla de particiones sin borrar los datos del disco.",
            AdminNotice(),
            Field("Disco", Row(disk, InfoBtn("Convertir entre MBR y GPT", ConvertInfo))),
            status, go.Also(b => b.Margin = new Thickness(0, 0, 0, 22)),
            Text("Copias de seguridad de la tabla", "SectionTitle"), backups);
    }

    // ───────────── Verificar imagen ─────────────
    UIElement VerifyPanel()
    {
        var file = new TextBox();
        var disk = DiskCombo();
        var hashResult = Text("", "Caption", margin: new Thickness(0, 0, 0, 14));
        var go = Btn("Comparar con el disco", Icons.Check, async (_, _) =>
        {
            if (!Native.IsAdmin) { Native.RestartAsAdmin(); return; }
            if (!File.Exists(file.Text) || disk.SelectedItem is not DiskOption o) { await Dialogs.Error("Faltan datos", "Elige la imagen y el disco."); return; }
            TransferQueue.Enqueue(DiskImage.VerifyImageJob(file.Text, o.Disk));
            MainWindow.Instance.Navigate("transfers");
        }, "PrimaryButton");
        var check = Btn("Comprobar hash SHA-256", Icons.Fingerprint, async (_, _) =>
        {
            string f = file.Text;
            if (!File.Exists(f + ".sha256")) { await Dialogs.Info("Sin archivo .sha256", "No hay un archivo .sha256 junto a la imagen."); return; }
            if (f.EndsWith(".gz")) { await Dialogs.Info("Imagen comprimida", "El hash se calculó sobre los datos sin comprimir. Usa «Comparar con el disco»."); return; }
            string expected = File.ReadAllText(f + ".sha256").Split(' ')[0].Trim();
            hashResult.Text = "Calculando…";
            string got = await Task.Run(() => FileHash.Compute(f, "SHA-256", CancellationToken.None, null));
            hashResult.Text = got == expected ? "✓ El hash coincide: la imagen está íntegra." : $"✗ El hash NO coincide.\nEsperado: {expected}\nObtenido: {got}";
            hashResult.Foreground = got == expected ? Brushes2.Good : Brushes2.Bad;
        });
        return Panel(Icons.Check, "Verificar imagen", "Comprueba que una imagen coincide byte a byte con un disco, o que su hash SHA-256 es correcto.",
            Field("Archivo de imagen", PathPicker(file, () => Dialogs.PickFile("Imagen de disco|*.img;*.gz|Todos|*.*"))),
            Field("Disco a comparar", disk), hashResult, Row(go, check.Also(b => b.Margin = new Thickness(8, 0, 0, 0))));
    }

    // ───────────── Análisis de superficie ─────────────
    UIElement SurfacePanel()
    {
        var disk = DiskCombo();
        var map = new SurfaceMap { Height = 260, Margin = new Thickness(0, 4, 0, 12) };
        var legend = Row(
            new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Brushes2.Good, Margin = new Thickness(0, 0, 6, 0) }, Text("Correcto", "Caption", margin: new Thickness(0, 0, 16, 0)),
            new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Brushes2.Caution, Margin = new Thickness(0, 0, 6, 0) }, Text("Lento", "Caption", margin: new Thickness(0, 0, 16, 0)),
            new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Brushes2.Bad, Margin = new Thickness(0, 0, 6, 0) }, Text("Error de lectura", "Caption"));
        var stats = Text("", "Caption", 13, margin: new Thickness(0, 8, 0, 14));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            map.InvalidateVisual();
            lock (map.Blocks)
                stats.Text = $"{map.Blocks.Count:N0} de {map.TotalBlocks:N0} bloques de 8 MB · {map.Blocks.Count(b => b.State == 1)} lentos · {map.Blocks.Count(b => b.State == 2)} con error";
        };
        var go = Btn("Iniciar análisis", Icons.Play, (_, _) =>
        {
            if (!Native.IsAdmin) { Native.RestartAsAdmin(); return; }
            if (disk.SelectedItem is not DiskOption o) return;
            lock (map.Blocks) map.Blocks.Clear();
            map.TotalBlocks = (o.Disk.Size + 8 * 1048576 - 1) / (8 * 1048576);
            var job = DiskImage.SurfaceScanJob(o.Disk, map.Blocks, null);
            job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TransferJob.State) && job.IsFinished) Dispatcher.BeginInvoke(() => { timer.Stop(); map.InvalidateVisual(); timer.Start(); timer.Stop(); }); };
            TransferQueue.Enqueue(job);
            timer.Start();
        }, "PrimaryButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);
        return Panel(Icons.Grid, "Análisis de superficie", "Lee todo el disco (sin modificar nada) y marca los bloques lentos o ilegibles, señal de sectores dañados.",
            AdminNotice(), Field("Disco", disk), go.Also(b => b.Margin = new Thickness(0, 0, 0, 16)), legend, map, stats);
    }
}
