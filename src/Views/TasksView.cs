using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Tareas de copia/sincronización guardadas, manuales o automáticas al conectar un disco.</summary>
public class TasksView : UserControl
{
    readonly StackPanel list = new();
    readonly StackPanel empty;

    public TasksView()
    {
        var header = Columns(
            (Col(Text("Tareas automáticas", "PageTitle"), Text("Copias de seguridad y sincronizaciones guardadas. Pueden ejecutarse solas al conectar un disco concreto.", "PageSubtitle")), Star),
            (Btn("Nueva tarea", Icons.Add, (_, _) => Edit(null), "PrimaryButton"), Auto));
        header.Margin = new Thickness(0, 0, 0, 20);

        var auto = new CheckBox { Content = "Ejecutar tareas automáticamente al conectar su disco", IsChecked = Settings.Current.AutoTasks, Margin = new Thickness(0, 0, 0, 16) };
        auto.Click += (_, _) => { Settings.Current.AutoTasks = auto.IsChecked == true; Settings.Save(); };

        empty = Col(Tile(Icons.Tasks, null, 64).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            Text("Aún no hay tareas", size: 18, weight: FontWeights.SemiBold, margin: new Thickness(0, 16, 0, 4)).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            Text("Ejemplo: «Cuando conecte mi disco USB de copias, actualiza la copia de Documentos».", "Caption", 13).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; }),
            Btn("Crear la primera tarea", Icons.Add, (_, _) => Edit(null), "PrimaryButton").Also(b => { b.HorizontalAlignment = HorizontalAlignment.Center; b.Margin = new Thickness(0, 16, 0, 0); }));
        empty.Margin = new Thickness(0, 60, 0, 0);

        var page = Col(header, auto, empty, list);
        page.Margin = new Thickness(36, 28, 36, 24);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutoTasks.Items.CollectionChanged += (_, _) => Render();
        AppState.Changed += Render;
        Render();
    }

    void Render()
    {
        list.Children.Clear();
        empty.Visibility = AutoTasks.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in AutoTasks.Items)
        {
            var tt = t;
            bool connected = string.IsNullOrEmpty(t.TriggerSerial) || AppState.Disks.Any(d => string.Equals(d.Serial, t.TriggerSerial, StringComparison.OrdinalIgnoreCase));
            var toggle = new CheckBox { IsChecked = t.Enabled, ToolTip = "Activada", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            toggle.Click += (_, _) => { tt.Enabled = toggle.IsChecked == true; AutoTasks.Save(); };
            var info = Col(
                Row(Text(t.Name, "CardTitle", 16), Chip(t.KindText).Also(c => c.Margin = new Thickness(10, 0, 0, 0)),
                    Chip(t.TriggerText, string.IsNullOrEmpty(t.TriggerSerial) ? null : (Brush)FindResource("BrandBrush2")),
                    !string.IsNullOrEmpty(t.TriggerSerial) ? Chip(connected ? "Disco conectado" : "Disco no conectado", connected ? Brushes2.Good : null) : null),
                Text($"{t.Source}  →  {t.Destination}", "Caption", 13, margin: new Thickness(0, 6, 0, 2)),
                Text($"Última ejecución: {t.LastRunText}" + (string.IsNullOrEmpty(t.Exclude) ? "" : $" · Excluye: {t.Exclude}") + (t.Verify ? " · Con verificación" : ""), "Caption", 12));
            info.Margin = new Thickness(14, 0, 0, 0);
            var actions = Row(
                Btn("Ejecutar", Icons.Play, (_, _) => Run(tt), "SecondaryButton"),
                IconBtn(Icons.Rename, "Editar", (_, _) => Edit(tt)).Also(b => b.Margin = new Thickness(6, 0, 0, 0)),
                IconBtn(Icons.Delete, "Eliminar", async (_, _) => { if (await Dialogs.Confirm("Eliminar tarea", $"¿Eliminar «{tt.Name}»?", "Eliminar", true)) { AutoTasks.Items.Remove(tt); AutoTasks.Save(); } }));
            actions.VerticalAlignment = VerticalAlignment.Center;
            list.Children.Add(Card(Columns((toggle, Auto), (Tile(Icons.Tasks, null, 42), Auto), (info, Star), (actions, Auto))));
        }
    }

    async void Run(AutoTask t)
    {
        string root = Path.GetPathRoot(t.Destination) ?? "";
        if (!Directory.Exists(t.Source)) { await Dialogs.Error("Origen no disponible", t.Source); return; }
        if (!Directory.Exists(root))
        {
            var disk = AppState.Disks.Find(d => string.Equals(d.Serial, t.TriggerSerial, StringComparison.OrdinalIgnoreCase));
            var v = disk?.Volumes.FirstOrDefault();
            if (v == null) { await Dialogs.Error("Destino no disponible", $"La unidad {root} no está conectada."); return; }
            TransferQueue.Enqueue(t.ToJob(v));
        }
        else TransferQueue.Enqueue(t.ToJob());
        Render();
        Toast.Show("Tarea en cola", t.Name, Icons.Tasks);
    }

    public async void Edit(AutoTask t)
    {
        bool isNew = t == null;
        t ??= new AutoTask();
        var name = new TextBox { Text = t.Name };
        var src = new TextBox { Text = t.Source }; var dst = new TextBox { Text = t.Destination };
        var kind = new ComboBox { ItemsSource = new[] { "Copiar todo (sin sobrescribir lo igual)", "Actualizar: solo lo nuevo o modificado", "Espejo: destino idéntico (borra sobrantes)", "Bidireccional: gana el más reciente" }, SelectedIndex = (int)t.Kind };
        var exclude = new TextBox { Text = t.Exclude }; var include = new TextBox { Text = t.Include };
        var verify = new CheckBox { Content = "Verificar al copiar", IsChecked = t.Verify, Margin = new Thickness(0, 4, 0, 10) };
        var disks = new List<(string label, PhysicalDisk d)> { ("Solo manual (sin disparador)", null) };
        disks.AddRange(AppState.Disks.Where(d => !string.IsNullOrEmpty(d.Serial)).Select(d => ($"Al conectar: {d.Title} · {Fmt.Size(d.Size)}" + (d.Volumes.Count > 0 ? $" ({string.Join(", ", d.Volumes.Select(v => v.Letter))})" : ""), d)));
        if (!string.IsNullOrEmpty(t.TriggerSerial) && !disks.Any(x => x.d?.Serial == t.TriggerSerial)) disks.Add(($"Al conectar: {t.TriggerName} (no conectado)", new PhysicalDisk { Serial = t.TriggerSerial, Model = t.TriggerName }));
        var trigger = new ComboBox { ItemsSource = disks.Select(x => x.label).ToList(), SelectedIndex = Math.Max(0, disks.FindIndex(x => x.d != null && x.d.Serial == t.TriggerSerial)) };
        Grid pick(TextBox tb) => Columns((tb, Star), (IconBtn(Icons.FolderOpen, "Examinar", (_, _) => { var p = Dialogs.PickFolder(tb.Text); if (p != null) tb.Text = p; }).Also(b => b.Margin = new Thickness(6, 0, 0, 0)), Auto));
        UIElement field(string l, UIElement c) => Col(Text(l, "Label"), c).Also(s => s.Margin = new Thickness(0, 0, 0, 10));
        var form = Col(field("Nombre", name), field("Origen", pick(src)), field("Destino", pick(dst)), field("Modo", kind), field("Disparador", trigger),
                       Columns((field("Incluir solo", include), Star), (null, Px(12)), (field("Excluir", exclude), Star)), verify,
                       Text("Si el disco disparador cambia de letra, Disk Studio Suite ajusta automáticamente la ruta que esté en él.", "Caption", 12));
        form.Width = 540;
        if (await Dialogs.Show(isNew ? "Nueva tarea" : "Editar tarea", null, ["Guardar", "Cancelar"], 0, extra: form, glyph: Icons.Tasks) != 0) return;
        if (string.IsNullOrWhiteSpace(src.Text) || string.IsNullOrWhiteSpace(dst.Text)) { await Dialogs.Error("Faltan datos", "Indica origen y destino."); return; }
        t.Name = name.Text.Trim(); t.Source = src.Text.Trim(); t.Destination = dst.Text.Trim(); t.Kind = (TaskKind)kind.SelectedIndex;
        t.Include = include.Text; t.Exclude = exclude.Text; t.Verify = verify.IsChecked == true;
        var sel = disks[Math.Max(0, trigger.SelectedIndex)].d;
        t.TriggerSerial = sel?.Serial ?? ""; t.TriggerName = sel?.Title ?? "";
        t.Refresh();
        if (isNew) AutoTasks.Items.Add(t);
        AutoTasks.Save();
        Render();
    }
}

public class LogView : UserControl
{
    public LogView()
    {
        var filter = new TextBox { Width = 260 };
        var kind = new ComboBox { ItemsSource = new[] { "Todo", "Errores", "Avisos", "Correctos" }, SelectedIndex = 0, Width = 130, Margin = new Thickness(8, 0, 0, 0) };
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Log.Entries);
        view.Filter = o =>
        {
            var e = (LogEntry)o;
            if (kind.SelectedIndex == 1 && e.Kind != LogKind.Error) return false;
            if (kind.SelectedIndex == 2 && e.Kind != LogKind.Warning) return false;
            if (kind.SelectedIndex == 3 && e.Kind != LogKind.Success) return false;
            return filter.Text.Length == 0 || e.Text.Contains(filter.Text, StringComparison.OrdinalIgnoreCase);
        };
        filter.TextChanged += (_, _) => view.Refresh();
        kind.SelectionChanged += (_, _) => view.Refresh();

        var lb = new ListBox { ItemsSource = view, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        var f = new FrameworkElementFactory(typeof(Grid));
        var c0 = new FrameworkElementFactory(typeof(ColumnDefinition)); c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(70));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(28));
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
        f.AppendChild(c0); f.AppendChild(c1); f.AppendChild(c2);
        var t0 = new FrameworkElementFactory(typeof(TextBlock)); t0.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("TimeText")); t0.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var t1 = new FrameworkElementFactory(typeof(TextBlock)); t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Glyph")); t1.SetValue(TextBlock.FontFamilyProperty, Application.Current.FindResource("IconFont")); t1.SetValue(Grid.ColumnProperty, 1);
        t1.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Kind") { Converter = new KindBrush() });
        var t2 = new FrameworkElementFactory(typeof(TextBlock)); t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")); t2.SetValue(Grid.ColumnProperty, 2); t2.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        t2.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        f.AppendChild(t0); f.AppendChild(t1); f.AppendChild(t2);
        lb.ItemTemplate = new DataTemplate { VisualTree = f };
        lb.ContextMenu = Menu(("Copiar", Icons.Copy, () => Clipboard.SetText(string.Join("\n", lb.SelectedItems.Cast<LogEntry>().Select(e => $"{e.TimeText} {e.Text}")))));

        var header = Columns((Col(Text("Registro", "PageTitle"), Text("Historial de todo lo que ha hecho Disk Studio Suite en esta sesión.", "PageSubtitle")), Star),
            (Row(filter, kind,
                Btn("Exportar…", Icons.Save, (_, _) => { var p = Dialogs.SaveFile("Texto|*.txt", $"dss-{DateTime.Now:yyyyMMdd-HHmm}.txt"); if (p != null) { Log.Export(p); Toast.Show("Registro exportado", p, Icons.Save); } }, "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0)),
                Btn("Vaciar", Icons.Delete, (_, _) => Log.Entries.Clear(), "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0))), Auto));
        header.Margin = new Thickness(0, 0, 0, 18);
        var dock = new DockPanel { Margin = new Thickness(36, 28, 36, 24) };
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        dock.Children.Add(Card(lb, new Thickness(0), 12));
        Content = dock;
    }

    class KindBrush : System.Windows.Data.IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v switch
        {
            LogKind.Error => Brushes2.Bad, LogKind.Warning => Brushes2.Caution, LogKind.Success => Brushes2.Good, _ => Brushes2.Accent
        };
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}

public class SettingsView : UserControl
{
    public SettingsView()
    {
        var s = Settings.Current;
        UIElement row(string glyph, string title, string desc, UIElement control) => new Border
        {
            Style = S("Card"), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 6),
            Child = Columns((Icon(glyph, 18, B("AccentTextFillColorPrimaryBrush"), new Thickness(0, 0, 16, 0)), Auto),
                (Col(Text(title, size: 14), Text(desc, "Caption", 12)), Star), (control, Auto))
        };
        CheckBox toggle(bool v, Action<bool> set) { var c = new CheckBox { IsChecked = v }; c.Click += (_, _) => { set(c.IsChecked == true); Settings.Save(); }; return c; }

        var theme = new ComboBox { ItemsSource = new[] { "Según Windows", "Claro", "Oscuro" }, SelectedIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 }, Width = 160 };
        theme.SelectionChanged += (_, _) => { s.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" }; Settings.Save(); App.ApplyTheme(s.Theme); };
        var conflict = new ComboBox { ItemsSource = new[] { "Preguntar", "Reemplazar siempre", "Omitir existentes", "Reemplazar si es más reciente", "Reemplazar si es diferente", "Conservar ambos" }, SelectedIndex = (int)s.Conflict, Width = 220 };
        conflict.SelectionChanged += (_, _) => { s.Conflict = (ConflictPolicy)conflict.SelectedIndex; Settings.Save(); };
        var buffer = new ComboBox { ItemsSource = new[] { "1 MB", "4 MB", "8 MB", "16 MB", "32 MB" }, Width = 110 };
        int[] bufs = [1, 4, 8, 16, 32];
        buffer.SelectedIndex = Math.Max(0, Array.IndexOf(bufs, s.BufferMB));
        buffer.SelectionChanged += (_, _) => { s.BufferMB = bufs[buffer.SelectedIndex]; Settings.Save(); };

        var page = Col(
            Text("Ajustes", "PageTitle"), Text("Preferencias de Disk Studio Suite.", "PageSubtitle").Also(t => t.Margin = new Thickness(0, 4, 0, 20)),
            Text("Apariencia", "SectionTitle"),
            row(Icons.Eye, "Tema", "Claro, oscuro o el mismo que Windows.", theme),
            Text("Copias", "SectionTitle", margin: new Thickness(0, 18, 0, 8)),
            row(Icons.Copy, "Si el archivo ya existe", "Comportamiento por defecto al copiar o mover.", conflict),
            row(Icons.Check, "Verificar siempre", "Comprueba cada archivo copiado (más seguro, algo más lento).", toggle(s.VerifyByDefault, v => s.VerifyByDefault = v)),
            row(Icons.Speed, "Tamaño del búfer", "Bloques más grandes suelen ir mejor en discos rápidos.", buffer),
            row(Icons.Delete, "Confirmar al eliminar", "Pide confirmación antes de enviar a la Papelera.", toggle(s.ConfirmDelete, v => s.ConfirmDelete = v)),
            Text("Discos y tareas", "SectionTitle", margin: new Thickness(0, 18, 0, 8)),
            row(Icons.Info, "Notificaciones", "Avisos al conectar discos y al terminar operaciones.", toggle(s.Notifications, v => s.Notifications = v)),
            row(Icons.Tasks, "Tareas automáticas", "Ejecuta las tareas al conectar su disco.", toggle(s.AutoTasks, v => s.AutoTasks = v)),
            row(Icons.Eye, "Mostrar archivos ocultos", "En el explorador de doble panel.", toggle(s.ShowHidden, v => s.ShowHidden = v)),
            row(Icons.Health, "SMART completo", SmartCtl.Available ? "smartmontools detectado: se usa para leer todos los atributos (como administrador)." : "Instala smartmontools para ver todos los atributos SMART.",
                Native.IsAdmin ? Chip("Administrador", Brushes2.Good) : Btn("Reiniciar como admin", Icons.Admin, (_, _) => Native.RestartAsAdmin(), "SecondaryButton")),
            Text("Datos", "SectionTitle", margin: new Thickness(0, 18, 0, 8)),
            row(Icons.Folder, "Carpeta de configuración", Settings.DataDir, Btn("Abrir", Icons.FolderOpen, (_, _) => Native.ShowInExplorer(Settings.DataDir), "SecondaryButton")),
            Text($"Disk Studio Suite {typeof(SettingsView).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version}", "Caption", margin: new Thickness(0, 20, 0, 0)));
        page.Margin = new Thickness(36, 28, 36, 24);
        page.MaxWidth = 900;
        page.HorizontalAlignment = HorizontalAlignment.Left;
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
