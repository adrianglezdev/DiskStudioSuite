using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

public partial class DisksView : UserControl
{
    public DisksView()
    {
        InitializeComponent();
        AdminBar.Visibility = Native.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        AppState.Changed += Render;
        Unloaded += (_, _) => { };
        Render();
    }

    void OnRefresh(object s, RoutedEventArgs e) { Subtitle.Text = "Buscando discos…"; AppState.Rescan(); }
    void OnAdmin(object s, RoutedEventArgs e) => Native.RestartAsAdmin();

    void Render()
    {
        var disks = AppState.Disks;
        var vols = AppState.Volumes;
        if (vols.Count == 0) return;
        long total = disks.Sum(d => d.Size), free = vols.Where(v => v.Ready && v.Type != DriveType.Network).Sum(v => v.Free);
        int warn = disks.Count(d => d.Health is HealthLevel.Caution or HealthLevel.Bad);
        Subtitle.Text = $"{disks.Count} discos físicos · {vols.Count(v => v.Ready)} volúmenes · actualizado a las {DateTime.Now:HH:mm:ss}";
        StatDisks.Text = disks.Count.ToString();
        StatCapacity.Text = Fmt.Size(total);
        StatFree.Text = Fmt.Size(free);
        StatHealth.Text = warn == 0 ? (disks.All(d => d.Health == HealthLevel.Unknown) ? "Sin datos" : "Todo bien") : $"{warn} con avisos";
        StatHealthTile.Background = warn == 0 ? Brushes2.Good : disks.Any(d => d.Health == HealthLevel.Bad) ? Brushes2.Bad : Brushes2.Caution;

        DiskList.Children.Clear();
        foreach (var d in disks) DiskList.Children.Add(DiskCard(d));

        var others = vols.Where(v => v.DiskNumber < 0).ToList();
        OtherTitle.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherList.Children.Clear();
        if (others.Count > 0)
        {
            var sp = new StackPanel();
            foreach (var v in others) sp.Children.Add(VolumeRow(v, null));
            OtherList.Children.Add(Card(sp));
        }
    }

    UIElement DiskCard(PhysicalDisk d)
    {
        var root = new StackPanel();

        // Cabecera
        var chips = Row(
            Chip("Disco " + d.Number),
            Chip(d.Bus, d.IsRemovable ? (Brush)FindResource("BrandBrush2") : null),
            string.IsNullOrEmpty(d.Media) ? null : Chip(d.Media, d.Media == "SSD" ? (Brush)FindResource("BrandBrush") : null),
            Chip(d.PartitionStyle),
            d.IsSystem || d.IsBoot ? Chip("Sistema", Brushes2.Caution) : null,
            d.IsRemovable ? Chip("Extraíble") : null);
        var title = Col(
            Text(d.Title, "CardTitle", 17),
            Row(Text($"{Fmt.Size(d.Size)}", "Caption", 13, margin: new Thickness(0, 2, 10, 6)),
                string.IsNullOrEmpty(d.Serial) ? null : Text("S/N " + d.Serial, "Caption", 12, margin: new Thickness(0, 2, 10, 6)),
                string.IsNullOrEmpty(d.Firmware) ? null : Text("FW " + d.Firmware, "Caption", 12, margin: new Thickness(0, 2, 0, 6))),
            chips);
        title.Margin = new Thickness(16, 0, 0, 0);

        // Salud
        var ring = new RingGauge { Width = 64, Height = 64, Value = (d.HealthPercent ?? 0) / 100.0, Stroke = Brushes2.ForHealth(d.Health) };
        var ringGrid = new Grid { Width = 64, Height = 64 };
        ringGrid.Children.Add(ring);
        ringGrid.Children.Add(Text(d.HealthPercent is int p ? p + "%" : "?", size: 15, weight: FontWeights.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        var stats = Col(
            Text(d.HealthText, size: 15, weight: FontWeights.SemiBold, fg: Brushes2.ForHealth(d.Health)),
            d.Temperature is int t ? Row(Icon(Icons.Thermometer, 13, t > 55 ? Brushes2.Caution : null, new Thickness(0, 0, 6, 0)), Text($"{t} °C", "Caption", 13)) : null,
            d.PowerOnHours is long h ? Row(Icon(Icons.Clock, 13, margin: new Thickness(0, 0, 6, 0)), Text($"{h:N0} h encendido", "Caption", 13)) : null,
            d.Wear is int w ? Row(Icon(Icons.Ssd, 13, margin: new Thickness(0, 0, 6, 0)), Text($"Desgaste {w}%", "Caption", 13)) : null,
            d.Health == HealthLevel.Unknown || (d.Temperature == null && !Native.IsAdmin) ? Text(Native.IsAdmin ? "Sin datos SMART" : "Más datos como admin", "Caption", 12) : null);
        stats.Margin = new Thickness(14, 0, 0, 0);
        stats.VerticalAlignment = VerticalAlignment.Center;
        var health = Row(ringGrid, stats);

        root.Children.Add(Columns((Tile(d.Glyph, d.IsRemovable ? (Brush)FindResource("BrandBrush2") : null, 48), Auto), (title, Star), (health, Auto)));

        // Mapa de particiones
        var map = new PartitionMap { Margin = new Thickness(0, 18, 0, 14) };
        map.SetDisk(d);
        root.Children.Add(map);

        // Volúmenes
        foreach (var v in d.Volumes.OrderBy(v => v.Letter)) root.Children.Add(VolumeRow(v, d));

        // Acciones del disco
        // Acciones como botones independientes (sin línea separadora)
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button act(string text, string glyph, Action a) => Btn(text, glyph, (_, _) => a(), "SecondaryButton")
            .Also(b => { b.Padding = new Thickness(12, 6, 12, 6); b.Margin = new Thickness(0, 0, 8, 0); });
        actions.Children.Add(act("Detalles SMART", Icons.Health, () => ShowSmart(d)));
        actions.Children.Add(act("Crear imagen", Icons.Image, () => MainWindow.Instance.Navigate<CloneView>("clone").Prepare("image", d)));
        actions.Children.Add(act("Clonar disco", Icons.Clone, () => MainWindow.Instance.Navigate<CloneView>("clone").Prepare("disk", d)));
        actions.Children.Add(act("Analizar superficie", Icons.Grid, () => MainWindow.Instance.Navigate<CloneView>("clone").Prepare("surface", d)));
        if (!d.IsSystem && !d.IsBoot)
            actions.Children.Add(act("Formatear", Icons.Drive, () => MainWindow.Instance.Navigate<FormatView>("format").PrepareDisk(d)));
        if (d.PartitionStyle is "MBR" or "GPT")
            actions.Children.Add(act(d.PartitionStyle == "MBR" ? "Convertir a GPT" : "Convertir a MBR", Icons.Swap, () => MainWindow.Instance.Navigate<CloneView>("clone").Prepare("gpt", d)));
        root.Children.Add(actions);

        return Card(root);
    }

    UIElement VolumeRow(VolumeInfo v, PhysicalDisk d)
    {
        var bar = new UsageBar { Value = v.Used, Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        var info = Col(
            Row(Text(v.Letter, size: 14, weight: FontWeights.SemiBold, margin: new Thickness(0, 0, 8, 0)),
                Text(v.Name, size: 14),
                v.Ready && !string.IsNullOrEmpty(v.FileSystem) ? Text("  ·  " + v.FileSystem + (v.ClusterSize > 0 ? $" · clúster {Fmt.Size(v.ClusterSize)}" : ""), "Caption", 12) : null),
            bar,
            Text(v.Ready ? $"{Fmt.Size(v.Free)} libres de {Fmt.Size(v.Total)}  ({v.Used:P0} usado)" : "No disponible", "Caption", 12, margin: new Thickness(0, 4, 0, 0)));
        info.Margin = new Thickness(12, 0, 16, 0);

        var buttons = Row(
            IconBtn(Icons.FolderOpen, "Abrir en el explorador", (_, _) => MainWindow.Instance.Navigate<ExplorerView>("explorer").Open(v.Root)),
            IconBtn(Icons.Pie, "Analizar espacio", (_, _) => MainWindow.Instance.Navigate<ToolsView>("tools").Open("space", v.Root)),
            IconBtn(Icons.Speed, "Medir velocidad", (_, _) => MainWindow.Instance.Navigate<ToolsView>("tools").Open("bench", v.Root)),
            v.IsRemovable && !v.IsSystem ? IconBtn(Icons.Eject, "Expulsar de forma segura", (_, _) => Eject(v)) : null);
        var more = IconBtn(Icons.More, "Más acciones", null);
        more.Click += (_, _) =>
        {
            more.ContextMenu = Menu(
                ("Abrir en el Explorador de Windows", Icons.External, () => Native.ShowInExplorer(v.Root)),
                ("Cambiar nombre del volumen…", Icons.Rename, () => Relabel(v)),
                ("Propiedades de Windows", Icons.Properties, () => Native.ShowProperties(v.Root)),
                ("-", null, null),
                ("Limpiar espacio libre (seguro)…", Icons.Wipe, () => WipeFree(v)),
                ("Formatear…", Icons.Drive, () => MainWindow.Instance.Navigate<FormatView>("format").Prepare(v)));
            more.ContextMenu.IsOpen = true;
        };
        buttons.Children.Add(more);
        buttons.VerticalAlignment = VerticalAlignment.Center;

        var row = Columns((Tile(v.Glyph, v.IsSystem ? (Brush)FindResource("BrandBrush") : (Brush)FindResource("BrandSlateBrush"), 36), Auto), (info, Star), (buttons, Auto));
        return new Border { Style = S("SubtleCard"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 10, 8, 10), Child = row };
    }

    async void Eject(VolumeInfo v)
    {
        if (TransferQueue.Active != null && (TransferQueue.Active.Subtitle.Contains(v.Root) || TransferQueue.Active.Title.Contains(v.Letter)))
        { await Dialogs.Info("Disco en uso", "Hay una operación en curso con este disco. Espera a que termine."); return; }
        try
        {
            if (Native.Eject(v.Root)) { Log.Info($"Expulsión solicitada para {v.Letter}"); Toast.Show("Expulsando", $"{v.Letter} {v.Name}. Windows avisará cuando sea seguro retirarlo.", Icons.Eject); }
        }
        catch (Exception ex) { await Dialogs.Error("No se pudo expulsar", ex.Message); }
    }

    async void Relabel(VolumeInfo v)
    {
        string name = await Dialogs.Prompt("Cambiar nombre del volumen", $"Nuevo nombre para {v.Letter}:", v.Label);
        if (name == null) return;
        if (Native.SetVolumeLabel(v.Root, name)) { Log.Ok($"Volumen {v.Letter} renombrado a \"{name}\""); AppState.Rescan(false); }
        else await Dialogs.Error("No se pudo cambiar el nombre", Native.IsAdmin ? "Windows rechazó el cambio." : "Puede que necesites ejecutar Disk Studio Suite como administrador.");
    }

    async void WipeFree(VolumeInfo v)
    {
        if (await Dialogs.Confirm("Limpiar espacio libre", $"Se llenará temporalmente el espacio libre de {v.Letter} ({Fmt.Size(v.Free)}) con datos aleatorios para que los archivos borrados no se puedan recuperar. Tus archivos no se tocan.\n\nPuede tardar bastante.", "Limpiar"))
            TransferQueue.Enqueue(Shredder.WipeFreeSpaceJob(v.Root));
    }

    async void Format(VolumeInfo v)
    {
        if (v.IsSystem) { await Dialogs.Error("No permitido", "No se puede formatear la unidad del sistema."); return; }
        if (!Native.IsAdmin) { if (await Dialogs.Confirm("Se necesita administrador", "Formatear requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin(); return; }
        var fs = new ComboBox { ItemsSource = new[] { "NTFS", "exFAT", "FAT32" }, SelectedIndex = v.Total > 32L * 1024 * 1024 * 1024 ? 0 : 1, Margin = new Thickness(0, 0, 0, 10) };
        var label = new TextBox { Text = v.Label, Margin = new Thickness(0, 0, 0, 10) };
        var quick = new CheckBox { Content = "Formato rápido", IsChecked = true };
        var panel = Col(Text("Sistema de archivos", "Label"), fs, Text("Nombre del volumen", "Label"), label, quick);
        if (await Dialogs.Show($"Formatear {v.Letter} {v.Name}", $"Se BORRARÁN todos los datos de {v.Letter} ({Fmt.Size(v.Total - v.Free)} usados).", ["Continuar", "Cancelar"], 0, true, panel, Icons.Warning, Brushes2.Bad) != 0) return;
        if (!await Dialogs.TypeToConfirm("Confirmar formateo", $"Esta acción no se puede deshacer.", $"FORMATEAR {v.Letter}", "Formatear")) return;
        string f = (string)fs.SelectedItem, l = label.Text; bool q = quick.IsChecked == true;
        Toast.Show("Formateando", $"{v.Letter} como {f}…", Icons.Warning);
        try { await Task.Run(() => DiskImage.Format(v.Letter, f, l, q)); Log.Ok($"{v.Letter} formateado como {f}"); Toast.Show("Formateo completado", $"{v.Letter} {l}", Icons.Success); }
        catch (Exception ex) { Log.Error("Formateo: " + ex.Message); await Dialogs.Error("No se pudo formatear", ex.Message); }
        AppState.Rescan(false);
    }

    async void ShowSmart(PhysicalDisk d)
    {
        var sp = new StackPanel { MinWidth = 520 };
        void kv(string k, string val) => sp.Children.Add(Columns((Text(k, "Caption", 13), Px(170)), (Text(val, size: 13), Star)).Also(g => g.Margin = new Thickness(0, 2, 0, 2)));
        kv("Modelo", d.Title); kv("Nº de serie", d.Serial); kv("Firmware", d.Firmware); kv("Interfaz", d.Bus); kv("Tipo", d.Media);
        kv("Capacidad", $"{Fmt.Size(d.Size)} ({d.Size:N0} bytes)"); kv("Estado", d.HealthText + (d.HealthPercent is int p ? $" ({p}%)" : ""));
        if (d.Temperature is int t) kv("Temperatura", t + " °C");
        if (d.PowerOnHours is long h) kv("Horas encendido", $"{h:N0} h ({h / 24.0:N0} días)");
        if (d.PowerCycles is long c) kv("Ciclos de encendido", c.ToString("N0"));
        if (d.Wear is int w) kv("Desgaste", w + "%");
        if (d.ReadErrors is long re) kv("Errores de lectura", re.ToString("N0"));
        if (d.WriteErrors is long we) kv("Errores de escritura", we.ToString("N0"));
        if (d.Attributes.Count > 0)
        {
            sp.Children.Add(Text("Atributos SMART", "SectionTitle", margin: new Thickness(0, 14, 0, 6)));
            var grid = new DataGrid
            {
                ItemsSource = d.Attributes, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
                MaxHeight = 320, GridLinesVisibility = DataGridGridLinesVisibility.None, CanUserResizeRows = false
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new System.Windows.Data.Binding("Id"), Width = 46 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Atributo", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Valor", Binding = new System.Windows.Data.Binding("Value"), Width = 60 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Peor", Binding = new System.Windows.Data.Binding("Worst"), Width = 56 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Umbral", Binding = new System.Windows.Data.Binding("Threshold"), Width = 66 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Bruto", Binding = new System.Windows.Data.Binding("Raw"), Width = 110 });
            var style = new Style(typeof(DataGridRow));
            var trig = new DataTrigger { Binding = new System.Windows.Data.Binding("Warn"), Value = true };
            trig.Setters.Add(new Setter(Control.ForegroundProperty, Brushes2.Caution));
            style.Triggers.Add(trig);
            grid.RowStyle = style;
            sp.Children.Add(grid);
        }
        else if (!Native.IsAdmin)
            sp.Children.Add(Text("Ejecuta como administrador para leer los atributos SMART completos.", "Caption", margin: new Thickness(0, 12, 0, 0)));
        else if (!SmartCtl.Available)
            sp.Children.Add(Text("Instala smartmontools para ver todos los atributos SMART.", "Caption", margin: new Thickness(0, 12, 0, 0)));
        await Dialogs.Show($"Salud del disco {d.Number}", null, ["Cerrar"], 0, extra: sp, glyph: Icons.Health, glyphBrush: Brushes2.ForHealth(d.Health));
    }
}
