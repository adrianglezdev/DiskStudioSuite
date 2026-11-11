using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Tareas de copia/sincronización guardadas, manuales o automáticas al conectar un disco.</summary>
public partial class TasksView : UserControl
{
    readonly StackPanel list = new();
    readonly StackPanel empty;

    public TasksView()
    {
        var header = Columns(
            (Col(Text("Copias y tareas", "PageTitle"), Text("Copias de seguridad y sincronizaciones guardadas: a mano, al conectar un disco o a una hora fija, y con versiones.", "PageSubtitle")), Star),
            (Btn("Nueva tarea", Icons.Add, (_, _) => Edit(null), "PrimaryButton"), Auto));
        header.Margin = new Thickness(0, 0, 0, 20);

        var auto = new CheckBox { Content = "Ejecutar tareas automáticamente (al conectar su disco y a su hora programada)", IsChecked = Settings.Current.AutoTasks, Margin = new Thickness(0, 0, 0, 16) };
        auto.Click += (_, _) => { Settings.Current.AutoTasks = auto.IsChecked == true; Settings.Save(); };

        empty = Col(Tile(Icons.Tasks, null, 64).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            Text("Aún no hay tareas", size: 18, weight: FontWeights.SemiBold, margin: new Thickness(0, 16, 0, 4)).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            Text("Ejemplo: «Cada día a las 21:00, guarda una versión de Documentos en mi disco de copias».", "Caption", 13).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; }),
            Btn("Crear la primera tarea", Icons.Add, (_, _) => Edit(null), "PrimaryButton").Also(b => { b.HorizontalAlignment = HorizontalAlignment.Center; b.Margin = new Thickness(0, 16, 0, 0); }));
        empty.Margin = new Thickness(0, 60, 0, 0);

        var page = Col(header, auto, empty, list);
        page.Margin = new Thickness(36, 28, 36, 24);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutoTasks.Items.CollectionChanged += (_, _) => Render();
        AutoTasks.Changed += Render;
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
                Text($"Última ejecución: {t.LastRunText}" + (t.NextRunText.Length > 0 ? $" · Próxima: {t.NextRunText}" : "") + (string.IsNullOrEmpty(t.Exclude) ? "" : $" · Excluye: {t.Exclude}") + (t.Verify ? " · Con verificación" : ""), "Caption", 12));
            info.Margin = new Thickness(14, 0, 0, 0);
            var actions = Row(
                t.Kind == TaskKind.Versioned ? Btn("Versiones", Icons.History, (_, _) => ShowVersions(tt), "SecondaryButton").Also(b => b.Margin = new Thickness(0, 0, 6, 0)) : null,
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
            AutoTasks.Start(t, v);
        }
        else AutoTasks.Start(t);
        Render();
        Toast.Show("Tarea en cola", t.Name, Icons.Tasks);
    }

    public async void Edit(AutoTask t)
    {
        bool isNew = t == null;
        t ??= new AutoTask();
        var name = new TextBox { Text = t.Name };
        var src = new TextBox { Text = t.Source }; var dst = new TextBox { Text = t.Destination };
        var kind = new ComboBox { ItemsSource = new[] { "Copiar todo (sin sobrescribir lo igual)", "Actualizar: solo lo nuevo o modificado", "Espejo: destino idéntico (borra sobrantes)", "Bidireccional: gana el más reciente", "Por versiones: una copia completa con fecha en cada ejecución" }, SelectedIndex = (int)t.Kind };
        var keep = new ComboBox { ItemsSource = new[] { "3", "5", "10", "20", "30", "60", "Todas" }, Width = 110 };
        int[] keeps = [3, 5, 10, 20, 30, 60, 0];
        keep.SelectedIndex = Math.Max(0, Array.IndexOf(keeps, t.KeepVersions));
        var keepRow = Col(Row(Text("Versiones que se conservan", "Label"), InfoBtn("Copias por versiones", VersionsInfo)), keep).Also(c => c.Margin = new Thickness(0, 0, 0, 10));
        void kindChanged() => keepRow.Visibility = kind.SelectedIndex == (int)TaskKind.Versioned ? Visibility.Visible : Visibility.Collapsed;
        kind.SelectionChanged += (_, _) => kindChanged();
        kindChanged();
        var sched = new ComboBox { ItemsSource = new[] { "Sin horario", "Cada cierto número de horas", "Todos los días", "Algunos días de la semana" }, SelectedIndex = (int)t.Schedule, Width = 260 };
        var hours = new ComboBox { ItemsSource = new[] { "1", "2", "3", "4", "6", "8", "12", "24" }, Width = 80 };
        int[] hs = [1, 2, 3, 4, 6, 8, 12, 24];
        hours.SelectedIndex = Math.Max(0, Array.IndexOf(hs, t.EveryHours));
        var time = new TextBox { Text = t.Time, Width = 80 };
        var dayBoxes = new List<(DayOfWeek d, CheckBox cb)>();
        var es = L.Culture;
        var daysRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var cb = new CheckBox { Content = es.DateTimeFormat.GetAbbreviatedDayName(d), IsChecked = t.Days.Contains(d), Margin = new Thickness(0, 0, 10, 0), MinWidth = 0 };
            dayBoxes.Add((d, cb)); daysRow.Children.Add(cb);
        }
        var missed = new CheckBox { Content = "Si el equipo estaba apagado a esa hora, hacerla al abrir Disk Studio Suite", IsChecked = t.RunMissed, Margin = new Thickness(0, 8, 0, 0) };
        var hoursRow = Row(Text("Cada", "Caption", margin: new Thickness(0, 0, 8, 0)), hours, Text("horas", "Caption", margin: new Thickness(8, 0, 0, 0)));
        var timeRow = Row(Text("A las", "Caption", margin: new Thickness(0, 0, 8, 0)), time, Text("(hh:mm)", "Caption", margin: new Thickness(8, 0, 0, 0)));
        var schedDetail = Col(hoursRow, timeRow, daysRow, missed).Also(c => c.Margin = new Thickness(0, 8, 0, 0));
        void schedChanged()
        {
            int k = sched.SelectedIndex;
            schedDetail.Visibility = k == 0 ? Visibility.Collapsed : Visibility.Visible;
            hoursRow.Visibility = k == 1 ? Visibility.Visible : Visibility.Collapsed;
            timeRow.Visibility = k >= 2 ? Visibility.Visible : Visibility.Collapsed;
            daysRow.Visibility = k == 3 ? Visibility.Visible : Visibility.Collapsed;
        }
        sched.SelectionChanged += (_, _) => schedChanged();
        schedChanged();
        var exclude = new TextBox { Text = t.Exclude }; var include = new TextBox { Text = t.Include };
        var verify = new CheckBox { Content = "Verificar al copiar", IsChecked = t.Verify, Margin = new Thickness(0, 4, 0, 10) };
        var disks = new List<(string label, PhysicalDisk d)> { ("No (sin disparador)", null) };
        disks.AddRange(AppState.Disks.Where(d => !string.IsNullOrEmpty(d.Serial)).Select(d => ($"Al conectar: {d.Title} · {Fmt.Size(d.Size)}" + (d.Volumes.Count > 0 ? $" ({string.Join(", ", d.Volumes.Select(v => v.Letter))})" : ""), d)));
        if (!string.IsNullOrEmpty(t.TriggerSerial) && !disks.Any(x => x.d?.Serial == t.TriggerSerial)) disks.Add(($"Al conectar: {t.TriggerName} (no conectado)", new PhysicalDisk { Serial = t.TriggerSerial, Model = t.TriggerName }));
        var trigger = new ComboBox { ItemsSource = disks.Select(x => x.label).ToList(), SelectedIndex = Math.Max(0, disks.FindIndex(x => x.d != null && x.d.Serial == t.TriggerSerial)) };
        Grid pick(TextBox tb) => Columns((tb, Star), (IconBtn(Icons.FolderOpen, "Examinar", (_, _) => { var p = Dialogs.PickFolder(tb.Text); if (p != null) tb.Text = p; }).Also(b => b.Margin = new Thickness(6, 0, 0, 0)), Auto));
        UIElement field(string l, UIElement c) => Col(Text(l, "Label"), c).Also(s => s.Margin = new Thickness(0, 0, 0, 10));
        var form = Col(field("Nombre", name), field("Origen", pick(src)), field("Destino", pick(dst)), field("Modo", kind), keepRow,
                       field("Al conectar un disco", trigger), Col(Text("Horario", "Label"), sched, schedDetail).Also(c => c.Margin = new Thickness(0, 0, 0, 10)),
                       Columns((field("Incluir solo", include), Star), (null, Px(12)), (field("Excluir", exclude), Star)), verify,
                       Text("Si el disco disparador cambia de letra, Disk Studio Suite ajusta automáticamente la ruta que esté en él.", "Caption", 12));
        form.Width = 540;
        var scroll = new ScrollViewer { Content = form, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (await Dialogs.Show(isNew ? "Nueva tarea" : "Editar tarea", null, ["Guardar", "Cancelar"], 0, extra: scroll, glyph: Icons.Tasks) != 0) return;
        if (sched.SelectedIndex >= 2 && !TimeSpan.TryParse(time.Text.Trim(), out _)) { await Dialogs.Error("Hora no válida", "Escribe la hora como hh:mm, por ejemplo 21:30."); return; }
        if (string.IsNullOrWhiteSpace(src.Text) || string.IsNullOrWhiteSpace(dst.Text)) { await Dialogs.Error("Faltan datos", "Indica origen y destino."); return; }
        t.Name = name.Text.Trim(); t.Source = src.Text.Trim(); t.Destination = dst.Text.Trim(); t.Kind = (TaskKind)kind.SelectedIndex;
        t.Include = include.Text; t.Exclude = exclude.Text; t.Verify = verify.IsChecked == true;
        var sel = disks[Math.Max(0, trigger.SelectedIndex)].d;
        t.TriggerSerial = sel?.Serial ?? ""; t.TriggerName = sel?.Title ?? "";
        t.KeepVersions = keeps[Math.Max(0, keep.SelectedIndex)];
        t.Schedule = (ScheduleKind)Math.Max(0, sched.SelectedIndex);
        t.EveryHours = hs[Math.Max(0, hours.SelectedIndex)];
        if (TimeSpan.TryParse(time.Text.Trim(), out var tm)) t.Time = $"{tm.Hours:D2}:{tm.Minutes:D2}";
        t.Days = dayBoxes.Where(x => x.cb.IsChecked == true).Select(x => x.d).ToList();
        t.RunMissed = missed.IsChecked == true;
        t.PlanNext(DateTime.Now);
        t.Refresh();
        if (isNew) AutoTasks.Items.Add(t);
        AutoTasks.Save();
        Render();
    }
}

public partial class TasksView
{
    static readonly (string, string)[] VersionsInfo =
    [
        ("Cómo funciona", "Cada vez que se ejecuta la tarea se crea en el destino una carpeta con la fecha y la hora (por ejemplo 2026-11-11_210000) con la copia completa del origen."),
        ("Ocupa poco", "Los archivos que no han cambiado no se vuelven a copiar: se enlazan con la versión anterior (enlaces duros de NTFS). Cada versión parece completa, pero solo ocupa lo que cambió. En discos exFAT o FAT32 no hay enlaces y se copia todo."),
        ("Recuperar", "Pulsa «Versiones» para abrir cualquier fecha en el explorador o restaurarla. Borrar una versión antigua no afecta a las demás."),
    ];

    async void ShowVersions(AutoTask t)
    {
        var list = VersionedBackup.List(t.Destination);
        var sp = new StackPanel { MinWidth = 560 };
        if (list.Count == 0) sp.Children.Add(Text("Todavía no hay versiones en " + t.Destination, "Caption", 13));
        foreach (var v in list.Take(60))
        {
            var vv = v;
            sp.Children.Add(new Border
            {
                Style = S("SubtleCard"), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6),
                Child = Columns((Icon(Icons.History, 16, B("AccentTextFillColorPrimaryBrush"), new Thickness(0, 0, 12, 0)), Auto),
                    (Col(Text(v.DateText, size: 14), Text(v.Name, "Caption", 11)), Star),
                    (Row(Btn("Abrir", Icons.FolderOpen, (_, _) => MainWindow.Instance.Navigate<ExplorerView>("explorer").Open(vv.Path), "SecondaryButton"),
                         Btn("Restaurar…", Icons.Undo, (_, _) => Restore(t, vv), "SecondaryButton").Also(b => b.Margin = new Thickness(6, 0, 0, 0))), Auto))
            });
        }
        var sv = new ScrollViewer { Content = sp, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        await Dialogs.Show($"Versiones de «{t.Name}»", list.Count > 0 ? $"{list.Count} versiones en {t.Destination}" : null, ["Cerrar"], 0, extra: sv, glyph: Icons.History, cancel: 0);
    }

    async void Restore(AutoTask t, BackupVersion v)
    {
        int r = await Dialogs.Show("Restaurar versión", $"¿Dónde quieres restaurar la versión del {v.DateText}?", ["En su ubicación original", "En otra carpeta…", "Cancelar"], 0, glyph: Icons.Undo);
        string target = r switch { 0 => t.Source, 1 => Dialogs.PickFolder(null, "Carpeta donde restaurar"), _ => null };
        if (string.IsNullOrEmpty(target)) return;
        if (r == 0 && !await Dialogs.Confirm("Restaurar en el origen", $"Los archivos de {target} se sustituirán por los de esa versión. Los archivos nuevos que no existían entonces se conservan.", "Restaurar", true)) return;
        TransferQueue.Enqueue(FolderSync.SyncJob($"Restaurar versión {v.Name}", v.Path, target, SyncMode.Update, new CopyOptions { Conflict = ConflictPolicy.Overwrite }, false, Icons.Undo));
        MainWindow.Instance.Navigate("transfers");
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

public class SettingsView : UserControl, ISnapshotDemo
{
    public Task Demo(string action)
    {
        if (action == "update")
        {
            string notes = "## Disk Studio Suite v0.3.1\n\nVersión de demostración para las capturas.\n\n### Novedades\n- **Instalador**: se instala por usuario, sin administrador.\n- **Actualización desde la app**, con descarga verificada por SHA-256.\n\n### Corregido\n- Montar VHD y VHDX ya muestra la letra de la unidad.\n\n---\n\nDescarga…";
            _ = UpdateUi.Offer(new UpdateInfo(new Version(0, 3, 1), "v0.3.1", "https://example.invalid", notes, false,
                [new ReleaseAsset(Updater.PortableName, "x", 63_500_000), new ReleaseAsset("DiskStudioSuite-Setup-0.3.1.exe", "x", 58_500_000), new ReleaseAsset(Updater.SumsName, "x", 200)]), true);
        }
        return Task.CompletedTask;
    }

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

        var lang = new ComboBox { ItemsSource = new[] { "Según Windows", "Español", "English" }, SelectedIndex = s.Language switch { "es" => 1, "en" => 2, _ => 0 }, Width = 160 };
        var langNote = Text("", "Caption", 12).Also(t => t.Foreground = Brushes2.Caution);
        lang.SelectionChanged += async (_, _) =>
        {
            s.Language = lang.SelectedIndex switch { 1 => "es", 2 => "en", _ => "auto" };
            Settings.Save();
            if (L.ResolveEnglish(s.Language) == L.English) return;
            if (await Dialogs.Confirm("Cambiar idioma", "El idioma se aplica al reiniciar Disk Studio Suite. ¿Reiniciar ahora?", "Reiniciar"))
            {
                if (TransferQueue.PendingCount > 0) { await Dialogs.Info("Operaciones en curso", "Espera a que termine la cola antes de reiniciar."); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--reinicio --sin-presentacion") { UseShellExecute = true });
                MainWindow.Instance.ExitApp();
            }
        };

        // Segundo plano
        var minutes = new ComboBox { ItemsSource = new[] { "5 min", "15 min", "30 min", "1 h", "3 h" }, Width = 110 };
        int[] mins = [5, 15, 30, 60, 180];
        minutes.SelectedIndex = Math.Max(0, Array.IndexOf(mins, s.MonitorMinutes));
        minutes.SelectionChanged += (_, _) => { s.MonitorMinutes = mins[minutes.SelectedIndex]; Settings.Save(); DiskMonitor.Restart(); };
        var temp = new ComboBox { ItemsSource = new[] { "45 °C", "50 °C", "55 °C", "60 °C", "65 °C", "70 °C" }, Width = 110 };
        int[] temps = [45, 50, 55, 60, 65, 70];
        temp.SelectedIndex = Math.Max(0, Array.IndexOf(temps, s.TempLimit));
        temp.SelectionChanged += (_, _) => { s.TempLimit = temps[temp.SelectedIndex]; Settings.Save(); };
        CheckBox autostart = null;
        autostart = toggle(s.StartWithWindows, v =>
        {
            s.StartWithWindows = v;
            try { ShellIntegration.SetAutostart(v); if (v && !s.CloseToTray) { s.CloseToTray = true; TrayIcon.Ensure(); } }
            catch (Exception ex) { _ = Dialogs.Error("No se pudo cambiar", ex.Message); }
        });
        var explorer = toggle(s.ExplorerMenu, v =>
        {
            s.ExplorerMenu = v;
            try { ShellIntegration.SetExplorerMenu(v); Toast.Show(v ? "Menú del Explorador activado" : "Menú del Explorador desactivado", v ? "En Windows 11 aparece en «Mostrar más opciones»." : "", Icons.External); }
            catch (Exception ex) { _ = Dialogs.Error("No se pudo cambiar", ex.Message); }
        });

        // Actualizaciones
        string kindText = Updater.Kind == InstallKind.Installed ? "Versión instalada" : "Versión portable";
        var updText = Text(kindText + " · " + (Updates.Available != null ? $"Disponible: {Updates.Available.Tag}" : s.LastUpdateCheck is DateTime lc ? $"Última comprobación: {lc:dd/MM/yyyy HH:mm}" : "Consulta las versiones publicadas en GitHub (no envía datos del equipo)."), "Caption", 12);
        Button download = null;
        download = Btn("Actualizar", Icons.Download, async (_, _) => await UpdateUi.Offer(Updates.Available, manual: true), "PrimaryButton").Also(b => { b.Visibility = Updates.Available != null ? Visibility.Visible : Visibility.Collapsed; b.Margin = new Thickness(0, 0, 8, 0); });
        var check = Btn("Buscar ahora", Icons.Update, async (_, _) =>
        {
            updText.Text = L.T("Comprobando…");
            try
            {
                var u = await Updates.Check();
                updText.Text = L.T(kindText) + " · " + L.T(u != null ? $"Hay una versión nueva: {u.Tag}" : $"Tienes la última versión ({Updates.Current.ToString(3)}).");
                download.Visibility = u != null ? Visibility.Visible : Visibility.Collapsed;
                if (u != null) await UpdateUi.Offer(u, manual: true);
            }
            catch (Exception ex) { updText.Text = L.T("No se pudo comprobar: " + ex.Message); }
        }, "SecondaryButton");
        var pre = toggle(Updates.IncludePrerelease, v => { s.UpdatePrerelease = v; });
        var auto = toggle(s.AutoInstallUpdates, v => s.AutoInstallUpdates = v);

        var page = Col(
            Text("Ajustes", "PageTitle"), Text("Preferencias de Disk Studio Suite.", "PageSubtitle").Also(t => t.Margin = new Thickness(0, 4, 0, 20)),
            Text("Apariencia", "SectionTitle"),
            row(Icons.Eye, "Tema", "Claro, oscuro o el mismo que Windows.", theme),
            row(Icons.Globe, "Idioma", "Idioma de la interfaz. Se aplica al reiniciar.", lang),
            Text("Copias", "SectionTitle", margin: new Thickness(0, 18, 0, 8)),
            row(Icons.Copy, "Si el archivo ya existe", "Comportamiento por defecto al copiar o mover.", conflict),
            row(Icons.Check, "Verificar siempre", "Comprueba cada archivo copiado (más seguro, algo más lento).", toggle(s.VerifyByDefault, v => s.VerifyByDefault = v)),
            row(Icons.Speed, "Tamaño del búfer", "Bloques más grandes suelen ir mejor en discos rápidos.", buffer),
            row(Icons.Delete, "Confirmar al eliminar", "Pide confirmación antes de enviar a la Papelera.", toggle(s.ConfirmDelete, v => s.ConfirmDelete = v)),
            Text("Discos y tareas", "SectionTitle", margin: new Thickness(0, 18, 0, 8)),
            row(Icons.Info, "Notificaciones", "Avisos al conectar discos y al terminar operaciones.", toggle(s.Notifications, v => s.Notifications = v)),
            row(Icons.Tasks, "Tareas automáticas", "Ejecuta las tareas al conectar su disco.", toggle(s.AutoTasks, v => s.AutoTasks = v)),
            row(Icons.Eye, "Mostrar archivos ocultos", "En el explorador de doble panel.", toggle(s.ShowHidden, v => s.ShowHidden = v)),
            Text("Segundo plano y vigilancia", "SectionTitle", margin: new Thickness(0, 18, 0, 8)),
            row(Icons.Bell, "Vigilar la salud de los discos", "Avisa si un disco empeora, aparecen sectores dañados o se calienta demasiado. Guarda un historial.", toggle(s.Monitor, v => { s.Monitor = v; DiskMonitor.Restart(); })),
            row(Icons.Clock, "Revisar cada", "Cada cuánto se leen los datos SMART.", minutes),
            row(Icons.Thermometer, "Avisar a partir de", "Temperatura de aviso.", temp),
            row(Icons.Moon, "Al cerrar, seguir en segundo plano", "La ventana se oculta y queda un icono junto al reloj: siguen la vigilancia y las copias programadas.", toggle(s.CloseToTray, v => { s.CloseToTray = v; if (v) TrayIcon.Ensure(); else TrayIcon.Remove(); })),
            row(Icons.Power, "Iniciar con Windows", "Se abre en segundo plano al iniciar sesión.", autostart),
            Text("Integración", "SectionTitle", margin: new Thickness(0, 18, 0, 8)),
            row(Icons.External, "Menú del Explorador de Windows", "Añade «Copiar con…», «Calcular hash con…» y «Analizar espacio con Disk Studio Suite» al clic derecho.", explorer),
            row(Icons.Update, "Buscar actualizaciones al iniciar", "Una vez al día como máximo.", toggle(s.CheckUpdates, v => s.CheckUpdates = v)),
            row(Icons.Download, "Instalar las actualizaciones automáticamente", "Al encontrar una versión nueva, la descarga, la comprueba y reinicia la app sin preguntar (nunca con operaciones en curso).", auto),
            row(Icons.Warning, "Incluir versiones en pruebas", "Recibe también las versiones previas (pre-release), que pueden tener fallos.", pre),
            new Border { Style = S("Card"), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 6),
                Child = Columns((Icon(Icons.Download, 18, B("AccentTextFillColorPrimaryBrush"), new Thickness(0, 0, 16, 0)), Auto), (Col(Text("Actualizaciones", size: 14), updText), Star), (Row(download, check), Auto)) },
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
