using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DiskStudio.Core;
using Microsoft.VisualBasic.FileIO;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

public class ToolsView : UserControl, ISnapshotDemo
{
    public async Task Demo(string action)
    {
        string arg = Environment.GetEnvironmentVariable("DSS_DEMO_PATH") ?? AppContext.BaseDirectory;
        Open(action, arg, action is "hash" or "rename" ? Directory.GetFiles(arg).Take(8).ToArray() : null);
        await Task.Delay(action == "bench" ? 500 : 6000);
    }

    readonly ContentControl host = new();
    readonly Dictionary<string, FrameworkElement> cache = [];
    string presetPath; string[] presetFiles;

    static readonly (string key, string glyph, string title, string desc, Color color)[] ToolList =
    [
        ("space", Icons.Pie, "Analizador de espacio", "Descubre qué carpetas y archivos ocupan más con un mapa visual.", Color.FromRgb(0x20, 0x87, 0x6C)),
        ("dups", Icons.Duplicate, "Buscar duplicados", "Encuentra archivos idénticos por contenido y libera espacio.", Color.FromRgb(0x1B, 0xB8, 0x98)),
        ("search", Icons.Search, "Búsqueda avanzada", "Busca por nombre, contenido, tamaño o fecha en cualquier disco.", Color.FromRgb(0x6B, 0xC3, 0x99)),
        ("bench", Icons.Speed, "Velocidad del disco", "Mide lectura y escritura secuencial y aleatoria (MB/s e IOPS).", Color.FromRgb(0xE8, 0xF3, 0x2C)),
        ("hash", Icons.Fingerprint, "Hash y verificación", "Calcula y comprueba SHA-256, SHA-1, MD5, CRC32…", Color.FromRgb(0x64, 0x74, 0x71)),
        ("rename", Icons.Rename, "Renombrado masivo", "Renombra muchos archivos con patrones, contadores y fechas.", Color.FromRgb(0x14, 0x5E, 0x4C)),
        ("wipe", Icons.Wipe, "Borrado seguro", "Elimina archivos de forma irrecuperable o limpia el espacio libre.", Color.FromRgb(220, 38, 38)),
    ];

    public ToolsView() { ShowHub(); }

    public void Open(string tool, string path, string[] files = null)
    {
        presetPath = path; presetFiles = files;
        cache.Remove(tool);
        ShowTool(tool);
    }

    void ShowHub()
    {
        var wrap = new WrapPanel();
        foreach (var t in ToolList)
        {
            var tt = t;
            var b = new Button { Style = S("ActionTile"), Width = 300 };
            b.Content = Col(
                Tile(t.glyph, new SolidColorBrush(t.color), 44).Also(x => x.HorizontalAlignment = HorizontalAlignment.Left),
                Text(t.title, "CardTitle", 16, margin: new Thickness(0, 14, 0, 4)),
                Text(t.desc, "Caption", 13).Also(x => { x.TextTrimming = TextTrimming.None; x.Height = 38; }));
            b.Click += (_, _) => { presetPath = null; presetFiles = null; ShowTool(tt.key); };
            wrap.Children.Add(b);
        }
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 24) };
        page.Children.Add(Text("Herramientas", "PageTitle"));
        page.Children.Add(Text("Utilidades para analizar, limpiar y mantener tus discos.", "PageSubtitle").Also(x => x.Margin = new Thickness(0, 4, 0, 22)));
        page.Children.Add(wrap);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    void ShowTool(string key)
    {
        var t = ToolList.First(x => x.key == key);
        if (!cache.TryGetValue(key, out var body))
        {
            body = key switch
            {
                "space" => SpaceTool(), "dups" => DupsTool(), "search" => SearchTool(), "bench" => BenchTool(),
                "hash" => HashTool(), "rename" => RenameTool(), _ => WipeTool()
            };
            cache[key] = body;
        }
        var back = IconBtn(Icons.Back, "Volver a Herramientas", (_, _) => ShowHub());
        var head = Columns((back.Also(b => b.Margin = new Thickness(0, 0, 12, 0)), Auto), (Tile(t.glyph, new SolidColorBrush(t.color), 40), Auto),
            (Col(Text(t.title, "PageTitle", 24), Text(t.desc, "Caption", 13)).Also(c => c.Margin = new Thickness(14, 0, 0, 0)), Star));
        head.Margin = new Thickness(0, 0, 0, 18);
        var dock = new DockPanel { Margin = new Thickness(28, 24, 36, 24) };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        if (body.Parent is Panel p) p.Children.Remove(body);
        dock.Children.Add(body);
        Content = dock;
    }

    static TextBox PathBox(string text = "") => new() { Text = text ?? "" };
    static Grid Picker(TextBox tb, string title = "Seleccionar carpeta") =>
        Columns((tb, Star), (IconBtn(Icons.FolderOpen, "Examinar…", (_, _) => { var p = Dialogs.PickFolder(tb.Text, title); if (p != null) tb.Text = p; }).Also(b => b.Margin = new Thickness(6, 0, 0, 0)), Auto));
    static DataGrid Grid(bool readOnly = true) => new()
    {
        AutoGenerateColumns = false, IsReadOnly = readOnly, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None,
        CanUserAddRows = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), RowHeight = 28, SelectionMode = DataGridSelectionMode.Extended
    };
    static DataGridTextColumn TCol(string header, string path, double width = double.NaN, bool star = false) =>
        new() { Header = header, Binding = new Binding(path), ElementStyle = S("CellText"), Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : double.IsNaN(width) ? DataGridLength.Auto : new DataGridLength(width) };

    // ═════════════ Analizador de espacio ═════════════
    FrameworkElement SpaceTool()
    {
        var path = PathBox(presetPath ?? AppState.Volumes.FirstOrDefault(v => v.Ready)?.Root);
        var status = Text("", "Caption");
        var bar = new ProgressBar { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var crumb = Text("", size: 14, weight: FontWeights.SemiBold);
        var tree = new Treemap();
        var folders = new StackPanel(); var files = new StackPanel(); var types = new StackPanel();
        SpaceNode root = null, cur = null;
        CancellationTokenSource cts = null;
        Button upBtn = null;

        void show(SpaceNode n)
        {
            cur = n;
            tree.SetRoot(n);
            crumb.Text = $"{n.Path}  ·  {Fmt.Size(n.Size)}  ·  {n.Files:N0} archivos  ·  {n.Folders:N0} carpetas";
            upBtn.IsEnabled = n.Parent != null;
            folders.Children.Clear(); files.Children.Clear(); types.Children.Clear();
            foreach (var c in n.Children.Take(14))
            {
                var cc = c;
                var row = BarRow(c.IsDir ? Icons.Folder : Icons.File, c.Name, c.Size, n.Size);
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonUp += (_, _) => { if (cc.IsDir) show(cc); else Native.ShowInExplorer(cc.Path); };
                row.ContextMenu = Menu(("Abrir en el explorador", Icons.FolderOpen, () => MainWindow.Instance.Navigate<ExplorerView>("explorer").Open(cc.IsDir ? cc.Path : Path.GetDirectoryName(cc.Path))),
                                       ("Mostrar en Windows", Icons.External, () => Native.ShowInExplorer(cc.Path)),
                                       ("Enviar a la Papelera", Icons.Delete, () => DeleteNode(cc, () => show(cur))));
                folders.Children.Add(row);
            }
            foreach (var f in SpaceAnalyzer.AllFiles(n).OrderByDescending(f => f.Size).Take(12))
            {
                var ff = f;
                var row = BarRow(Icons.File, f.Name, f.Size, n.Size, Path.GetDirectoryName(f.Path));
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonUp += (_, _) => Native.ShowInExplorer(ff.Path);
                files.Children.Add(row);
            }
            foreach (var (ext, size, count) in SpaceAnalyzer.ByExtension(n).Take(10))
                types.Children.Add(BarRow(Icons.Tag, $"{ext}  ({count:N0})", size, n.Size));
        }

        async void scan()
        {
            if (cts != null) { cts.Cancel(); return; }
            string p = path.Text.Trim();
            if (!Directory.Exists(p)) { await Dialogs.Error("Carpeta no válida", p); return; }
            cts = new CancellationTokenSource(); var tk = cts.Token;
            bar.Visibility = Visibility.Visible; status.Text = "Analizando…";
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                root = await Task.Run(() => SpaceAnalyzer.Scan(p, tk, (d, c) => Dispatcher.BeginInvoke(() => status.Text = $"{c:N0} elementos · {d}")));
                status.Text = $"Análisis completado en {Fmt.Time(sw.Elapsed)}. Haz clic en una carpeta del mapa para entrar.";
                show(root);
            }
            catch (OperationCanceledException) { status.Text = "Análisis cancelado"; }
            finally { bar.Visibility = Visibility.Collapsed; cts = null; }
        }

        tree.NodeClicked += n => { if (n.IsDir) show(n); };
        upBtn = IconBtn(Icons.Up, "Subir", (_, _) => { if (cur?.Parent != null) show(cur.Parent); });
        upBtn.IsEnabled = false;

        var top = Card(Col(
            Columns((Picker(path), Star), (Btn("Analizar", Icons.Pie, (_, _) => scan(), "PrimaryButton").Also(b => b.Margin = new Thickness(10, 0, 0, 0)), Auto)),
            bar, status.Also(s => s.Margin = new Thickness(0, 8, 0, 0))), new Thickness(0, 0, 0, 14), 14);

        var mapCard = Card(new DockPanel().Also(d =>
        {
            var h = Columns((upBtn, Auto), (crumb.Also(c => c.Margin = new Thickness(8, 0, 0, 0)), Star));
            h.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(h, Dock.Top); d.Children.Add(h);
            d.Children.Add(tree);
        }), new Thickness(0, 0, 14, 0), 14);

        var side = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Col(Card(Col(Text("Contenido", "SectionTitle"), folders), padding: 14), Card(Col(Text("Archivos más grandes", "SectionTitle"), files), padding: 14),
                          Card(Col(Text("Por tipo de archivo", "SectionTitle"), types), padding: 14))
        };
        var g = Columns((mapCard, Star), (side, Px(380)));
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top); dock.Children.Add(g);
        if (presetPath != null) Dispatcher.BeginInvoke(scan);
        return dock;
    }

    static FrameworkElement BarRow(string glyph, string name, long size, long total, string sub = null)
    {
        var bar = new UsageBar { Value = total > 0 ? (double)size / total : 0, Height = 4, Fill = Brushes2.Accent, Margin = new Thickness(0, 4, 0, 0) };
        var g = Columns((Icon(glyph, 13, B("TextFillColorSecondaryBrush"), new Thickness(0, 0, 10, 0)), Auto),
            (Col(Text(name, size: 13), sub == null ? null : Text(sub, "Caption", 11).Also(t => t.TextWrapping = TextWrapping.NoWrap), bar), Star),
            (Text(Fmt.Size(size), "Caption", 12, margin: new Thickness(10, 0, 0, 0)), Auto));
        return new Border { Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Child = g }
            .Also(b => { b.MouseEnter += (_, _) => b.Background = B("SubtleFillColorSecondaryBrush"); b.MouseLeave += (_, _) => b.Background = Brushes.Transparent; });
    }

    async void DeleteNode(SpaceNode n, Action after)
    {
        if (!await Dialogs.Confirm("Enviar a la Papelera", $"{n.Path}\n{Fmt.Size(n.Size)}", "Eliminar", true)) return;
        try
        {
            await Task.Run(() => { if (n.IsDir) FileSystem.DeleteDirectory(n.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); else FileSystem.DeleteFile(n.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); });
            for (var p = n.Parent; p != null; p = p.Parent) { p.Size -= n.Size; p.Files -= n.IsDir ? n.Files : 1; }
            n.Parent?.Children.Remove(n);
            Log.Info("Eliminado (Papelera): " + n.Path);
            after();
        }
        catch (Exception ex) { await Dialogs.Error("No se pudo eliminar", ex.Message); }
    }

    // ═════════════ Duplicados ═════════════
    FrameworkElement DupsTool()
    {
        var roots = new ObservableCollection<string>();
        if (presetPath != null) roots.Add(presetPath);
        var rootList = new ItemsControl { ItemsSource = roots };
        var minSize = new ComboBox { ItemsSource = new[] { "Todos", "> 100 KB", "> 1 MB", "> 10 MB", "> 100 MB" }, SelectedIndex = 2, Width = 120 };
        var exclude = new TextBox { Text = "*.tmp; Thumbs.db; desktop.ini", Width = 240 };
        var status = Text("Añade una o varias carpetas y pulsa Buscar.", "Caption");
        var bar = new ProgressBar { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var results = new StackPanel();
        List<DupGroup> groups = [];
        CancellationTokenSource cts = null;

        void renderRoots()
        {
            rootList.ItemsSource = null;
            rootList.ItemsSource = roots.Select(r => (UIElement)Row(Chip(r), IconBtn(Icons.Close, "Quitar", (_, _) => { roots.Remove(r); renderRoots(); }))).ToList();
        }
        renderRoots();

        void render()
        {
            results.Children.Clear();
            long wasted = groups.Sum(g => g.Wasted);
            status.Text = groups.Count == 0 ? "No se han encontrado duplicados." : $"{groups.Count:N0} grupos de duplicados · {Fmt.Size(wasted)} recuperables · {groups.Sum(g => g.Files.Count(f => f.Marked))} marcados";
            foreach (var g in groups.Take(300))
            {
                var sp = new StackPanel();
                sp.Children.Add(Row(Icon(Icons.Duplicate, 13, B("AccentTextFillColorPrimaryBrush"), new Thickness(0, 0, 8, 0)), Text($"{g.Files.Count} copias · {Fmt.Size(g.Size)} cada una · {Fmt.Size(g.Wasted)} recuperables", size: 13, weight: FontWeights.SemiBold)));
                foreach (var f in g.Files)
                {
                    var cb = new CheckBox { Margin = new Thickness(0, 6, 0, 0), DataContext = f };
                    cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("Marked") { Mode = BindingMode.TwoWay });
                    cb.Content = Columns((Text(f.Name, size: 13), Px(260)), (Text(f.Folder, "Caption", 12), Star), (Text(f.ModifiedText, "Caption", 12, margin: new Thickness(10, 0, 0, 0)), Auto));
                    cb.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    var ff = f;
                    cb.MouseDoubleClick += (_, _) => Native.ShowInExplorer(ff.Path);
                    cb.Checked += (_, _) => status.Text = $"{groups.Count:N0} grupos · {Fmt.Size(wasted)} recuperables · {groups.Sum(x => x.Files.Count(y => y.Marked))} marcados";
                    sp.Children.Add(cb);
                }
                results.Children.Add(new Border { Style = S("SubtleCard"), Margin = new Thickness(0, 0, 0, 8), Child = sp });
            }
            if (groups.Count > 300) results.Children.Add(Text($"… y {groups.Count - 300} grupos más (se muestran los 300 con más espacio).", "Caption"));
        }

        async void find()
        {
            if (cts != null) { cts.Cancel(); return; }
            if (roots.Count == 0) { await Dialogs.Info("Sin carpetas", "Añade al menos una carpeta."); return; }
            long min = minSize.SelectedIndex switch { 1 => 100 * 1024, 2 => 1048576, 3 => 10 * 1048576L, 4 => 100 * 1048576L, _ => 1 };
            var filter = new FileFilter { Exclude = exclude.Text };
            var rs = roots.ToList();
            cts = new CancellationTokenSource(); var tk = cts.Token;
            bar.Visibility = Visibility.Visible;
            try
            {
                groups = await Task.Run(() => DuplicateFinder.Find(rs, min, filter, tk, s => Dispatcher.BeginInvoke(() => status.Text = s)));
                render();
                Log.Info($"Duplicados: {groups.Count} grupos, {Fmt.Size(groups.Sum(g => g.Wasted))} recuperables");
            }
            catch (OperationCanceledException) { status.Text = "Búsqueda cancelada"; }
            finally { bar.Visibility = Visibility.Collapsed; cts = null; }
        }

        void mark(int mode)
        {
            foreach (var g in groups)
                for (int i = 0; i < g.Files.Count; i++)
                    g.Files[i].Marked = mode switch { 0 => i != 0, 1 => i != g.Files.Count - 1, _ => false };
            render();
        }

        async void deleteMarked(bool moveTo)
        {
            var marked = groups.SelectMany(g => g.Files.Where(f => f.Marked)).ToList();
            if (marked.Count == 0) return;
            if (groups.Any(g => g.Files.All(f => f.Marked)) && !await Dialogs.Confirm("Atención", "En algún grupo están marcadas TODAS las copias. Se perdería el archivo.", "Continuar igualmente", true)) return;
            string dest = null;
            if (moveTo) { dest = Dialogs.PickFolder(null, "Mover duplicados a…"); if (dest == null) return; }
            else if (!await Dialogs.Confirm("Eliminar duplicados", $"Se enviarán {marked.Count} archivos ({Fmt.Size(marked.Sum(f => f.Group.Size))}) a la Papelera.", "Eliminar", true)) return;
            if (moveTo) { TransferQueue.Enqueue(FileOps.CopyJob(marked.Select(m => m.Path).ToList(), dest, true, new CopyOptions { Conflict = ConflictPolicy.KeepBoth })); }
            else await Task.Run(() => { foreach (var f in marked) try { FileSystem.DeleteFile(f.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); } catch (Exception ex) { Log.Error(ex.Message); } });
            foreach (var g in groups) g.Files.RemoveAll(f => f.Marked);
            groups.RemoveAll(g => g.Files.Count < 2);
            render();
        }

        var top = Card(Col(
            Row(Btn("Añadir carpeta", Icons.Add, (_, _) => { var p = Dialogs.PickFolder(null); if (p != null && !roots.Contains(p)) { roots.Add(p); renderRoots(); } }, "SecondaryButton"),
                Text("Tamaño mínimo", "Caption", margin: new Thickness(18, 0, 8, 0)), minSize,
                Text("Excluir", "Caption", margin: new Thickness(18, 0, 8, 0)), exclude,
                Btn("Buscar", Icons.Search, (_, _) => find(), "PrimaryButton").Also(b => b.Margin = new Thickness(18, 0, 0, 0))),
            rootList.Also(r => r.Margin = new Thickness(0, 10, 0, 0)), bar, status.Also(s => s.Margin = new Thickness(0, 8, 0, 0))), new Thickness(0, 0, 0, 14), 14);
        var actions = Row(Btn("Marcar todos salvo el más antiguo", Icons.Check, (_, _) => mark(0)), Btn("Salvo el más reciente", Icons.Check, (_, _) => mark(1)),
                          Btn("Desmarcar", Icons.Close, (_, _) => mark(2)), Btn("Mover marcados…", Icons.Move, (_, _) => deleteMarked(true)),
                          Btn("Eliminar marcados", Icons.Delete, (_, _) => deleteMarked(false)));
        actions.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        DockPanel.SetDock(actions, Dock.Top); dock.Children.Add(actions);
        dock.Children.Add(new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (presetPath != null) Dispatcher.BeginInvoke(find);
        return dock;
    }

    // ═════════════ Búsqueda ═════════════
    FrameworkElement SearchTool()
    {
        var root = PathBox(presetPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var name = new TextBox(); var text = new TextBox();
        var min = new TextBox { Width = 80 }; var max = new TextBox { Width = 80 };
        var after = new DatePicker { Width = 140 };
        var hidden = new CheckBox { Content = "Incluir ocultos", Margin = new Thickness(0, 0, 16, 0) };
        var folders = new CheckBox { Content = "Incluir carpetas" };
        var status = Text("", "Caption");
        var results = new ObservableCollection<FileItem>();
        var grid = Grid();
        grid.ItemsSource = results;
        grid.Columns.Add(TCol("Nombre", "Name", 260));
        grid.Columns.Add(TCol("Carpeta", "Type", star: true));
        grid.Columns.Add(TCol("Tamaño", "SizeText", 90));
        grid.Columns.Add(TCol("Modificado", "ModifiedText", 130));
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is FileItem f) MainWindow.Instance.Navigate<ExplorerView>("explorer").Open(f.FullPath); };
        grid.ContextMenu = new ContextMenu();
        grid.ContextMenuOpening += (_, _) =>
        {
            var f = grid.SelectedItem as FileItem;
            if (f == null) return;
            grid.ContextMenu = Menu(("Abrir", Icons.FolderOpen, () => Native.Open(f.FullPath)), ("Ir en el explorador", Icons.Folder, () => MainWindow.Instance.Navigate<ExplorerView>("explorer").Open(f.FullPath)),
                                    ("Mostrar en Windows", Icons.External, () => Native.ShowInExplorer(f.FullPath)), ("Copiar ruta", Icons.Copy, () => Clipboard.SetText(f.FullPath)));
        };
        CancellationTokenSource cts = null;
        Button go = null;

        async void run()
        {
            if (cts != null) { cts.Cancel(); return; }
            if (!Directory.Exists(root.Text)) { await Dialogs.Error("Carpeta no válida", root.Text); return; }
            results.Clear();
            var q = new SearchQuery
            {
                Root = root.Text, Name = name.Text.Trim(), Text = text.Text, IncludeHidden = hidden.IsChecked == true, Folders = folders.IsChecked == true,
                MinSize = double.TryParse(min.Text, out var a) ? (long)(a * 1048576) : -1, MaxSize = double.TryParse(max.Text, out var b) ? (long)(b * 1048576) : -1,
                After = after.SelectedDate
            };
            cts = new CancellationTokenSource(); var tk = cts.Token;
            go.Content = "Detener";
            var pending = new List<FileItem>();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            string where = "";
            timer.Tick += (_, _) =>
            {
                lock (pending) { foreach (var p in pending) results.Add(p); pending.Clear(); }
                status.Text = $"{results.Count:N0} resultados · {where}";
            };
            timer.Start();
            try
            {
                await Task.Run(() => FileSearch.Run(q, tk, fsi =>
                {
                    var it = new FileItem { Name = fsi.Name, FullPath = fsi.FullName, IsDir = fsi is DirectoryInfo, Size = fsi is FileInfo fi ? fi.Length : 0, Modified = fsi.LastWriteTime, Type = Path.GetDirectoryName(fsi.FullName) ?? "" };
                    lock (pending) pending.Add(it);
                    if (results.Count + pending.Count > 20000) cts.Cancel();
                }, d => where = d));
                where = "búsqueda completada";
            }
            catch (OperationCanceledException) { where = results.Count >= 20000 ? "límite de 20.000 resultados" : "detenida"; }
            finally
            {
                timer.Stop();
                lock (pending) { foreach (var p in pending) results.Add(p); pending.Clear(); }
                status.Text = $"{results.Count:N0} resultados · {where}";
                go.Content = "Buscar"; cts = null;
            }
        }
        go = Btn("Buscar", Icons.Search, (_, _) => run(), "PrimaryButton");
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) run(); };
        text.KeyDown += (_, e) => { if (e.Key == Key.Enter) run(); };

        var form = Card(Col(
            Columns((Col(Text("Buscar en", "Label"), Picker(root)), Star), (Col(Text("Nombre (admite * ? y varios con ;)", "Label"), name).Also(c => c.Margin = new Thickness(14, 0, 0, 0)), Star),
                    (Col(Text("Contiene el texto", "Label"), text).Also(c => c.Margin = new Thickness(14, 0, 0, 0)), Star)),
            Row(Text("Tamaño (MB) entre", "Caption", margin: new Thickness(0, 0, 8, 0)), min, Text("y", "Caption", margin: new Thickness(8, 0, 8, 0)), max,
                Text("Modificado desde", "Caption", margin: new Thickness(18, 0, 8, 0)), after,
                hidden.Also(h => h.Margin = new Thickness(18, 0, 16, 0)), folders, go.Also(g => g.Margin = new Thickness(18, 0, 0, 0))).Also(r => r.Margin = new Thickness(0, 14, 0, 0)),
            status.Also(s => s.Margin = new Thickness(0, 10, 0, 0))), new Thickness(0, 0, 0, 14), 14);
        var dock = new DockPanel();
        DockPanel.SetDock(form, Dock.Top); dock.Children.Add(form);
        dock.Children.Add(Card(grid, new Thickness(0), 0));
        return dock;
    }

    // ═════════════ Benchmark ═════════════
    FrameworkElement BenchTool()
    {
        var vols = AppState.Volumes.Where(v => v.Ready && v.Type is DriveType.Fixed or DriveType.Removable).ToList();
        var vol = new ComboBox { ItemsSource = vols, MinWidth = 260 };
        vol.SelectedItem = vols.Find(v => v.Root == presetPath) ?? vols.FirstOrDefault();
        var size = new ComboBox { ItemsSource = new[] { "256 MB", "1 GB", "4 GB" }, SelectedIndex = 1, Width = 110 };
        var bar = new ProgressBar { Maximum = 1, Height = 6, Margin = new Thickness(0, 12, 0, 4) };
        var phase = Text("Prueba sin caché del sistema (lectura/escritura directa).", "Caption");
        var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 14, 0, 0) };
        var history = new StackPanel();
        CancellationTokenSource cts = null;
        Button go = null;

        UIElement ResultCard(string title, string value, string unit, string sub, double frac, Brush color) => Card(Col(
            Text(title, "Caption", 13),
            Row(Text(value, size: 30, weight: FontWeights.SemiBold), Text(" " + unit, "Caption", 14).Also(t => t.VerticalAlignment = VerticalAlignment.Bottom).Also(t => t.Margin = new Thickness(4, 0, 0, 6))),
            new UsageBar { Value = frac, Fill = color, Height = 6, Margin = new Thickness(0, 8, 0, 6) },
            Text(sub, "Caption", 12)), new Thickness(0, 0, 12, 0), 16);

        void showResults(List<BenchResult> r, VolumeInfo v)
        {
            cards.Children.Clear();
            var seq = r[0]; var rnd = r[1];
            double maxSeq = Math.Max(600, Math.Max(seq.ReadMBs, seq.WriteMBs)), maxRnd = Math.Max(60, Math.Max(rnd.ReadMBs, rnd.WriteMBs));
            var blue = (Brush)FindResource("BrandBrush"); var cyan = (Brush)FindResource("BrandBrush2");
            cards.Children.Add(ResultCard("Lectura secuencial", seq.ReadMBs.ToString("N0"), "MB/s", "Bloques de 1 MiB", seq.ReadMBs / maxSeq, blue));
            cards.Children.Add(ResultCard("Escritura secuencial", seq.WriteMBs.ToString("N0"), "MB/s", "Bloques de 1 MiB", seq.WriteMBs / maxSeq, cyan));
            cards.Children.Add(ResultCard("Lectura aleatoria 4K", rnd.ReadMBs.ToString("N1"), "MB/s", $"{rnd.ReadIops:N0} IOPS", rnd.ReadMBs / maxRnd, blue));
            cards.Children.Add(ResultCard("Escritura aleatoria 4K", rnd.WriteMBs.ToString("N1"), "MB/s", $"{rnd.WriteIops:N0} IOPS", rnd.WriteMBs / maxRnd, cyan));
            string verdict = seq.ReadMBs > 1500 ? "Muy rápido (NVMe)" : seq.ReadMBs > 400 ? "Rápido (SSD SATA)" : seq.ReadMBs > 120 ? "Normal (HDD o USB 3)" : "Lento (USB 2, tarjeta o disco con problemas)";
            history.Children.Insert(0, Text($"{DateTime.Now:HH:mm}  {v.Letter} {v.Name} · {v.DiskModel} — lectura {seq.ReadMBs:N0} MB/s, escritura {seq.WriteMBs:N0} MB/s, 4K {rnd.ReadIops:N0}/{rnd.WriteIops:N0} IOPS · {verdict}", "Caption", 13, margin: new Thickness(0, 3, 0, 3)));
            phase.Text = verdict;
            Log.Ok($"Benchmark {v.Letter}: lectura {seq.ReadMBs:N0} MB/s, escritura {seq.WriteMBs:N0} MB/s, 4K {rnd.ReadIops:N0}/{rnd.WriteIops:N0} IOPS");
        }

        async void run()
        {
            if (cts != null) { cts.Cancel(); return; }
            if (vol.SelectedItem is not VolumeInfo v) return;
            long sz = size.SelectedIndex switch { 0 => 256L << 20, 2 => 4L << 30, _ => 1L << 30 };
            if (v.Free < sz + (512L << 20)) { await Dialogs.Error("Espacio insuficiente", $"Se necesitan {Fmt.Size(sz)} libres en {v.Letter}."); return; }
            if (TransferQueue.Active != null && !await Dialogs.Confirm("Hay copias en curso", "Los resultados serán menos precisos mientras haya transferencias.", "Medir igualmente")) return;
            cts = new CancellationTokenSource(); var tk = cts.Token;
            go.Content = "Detener";
            try
            {
                var r = await Task.Run(() => Benchmark.Run(v.Root, sz, tk, (ph, p) => Dispatcher.BeginInvoke(() => { phase.Text = ph + "…"; bar.Value = p; })));
                showResults(r, v);
            }
            catch (OperationCanceledException) { phase.Text = "Prueba cancelada"; }
            catch (Exception ex) { phase.Text = "Error: " + ex.Message; }
            finally { go.Content = "Iniciar prueba"; cts = null; bar.Value = 0; }
        }
        go = Btn("Iniciar prueba", Icons.Play, (_, _) => run(), "PrimaryButton");
        var top = Card(Col(Row(Text("Unidad", "Caption", margin: new Thickness(0, 0, 8, 0)), vol, Text("Tamaño de prueba", "Caption", margin: new Thickness(18, 0, 8, 0)), size, go.Also(g => g.Margin = new Thickness(18, 0, 0, 0))), bar, phase), new Thickness(0, 0, 0, 0), 16);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Col(top, cards, Text("Historial", "SectionTitle", margin: new Thickness(0, 22, 0, 6)), history) };
    }

    // ═════════════ Hash ═════════════
    class HashRow : Notifier
    {
        public string Path { get; init; } = "";
        public string Name => System.IO.Path.GetFileName(Path);
        string hash = ""; public string Hash { get => hash; set => Set(ref hash, value); }
        string state = ""; public string State { get => state; set => Set(ref state, value); }
    }

    FrameworkElement HashTool()
    {
        var rows = new ObservableCollection<HashRow>((presetFiles ?? []).Select(f => new HashRow { Path = f }));
        var alg = new ComboBox { ItemsSource = FileHash.Algorithms, SelectedIndex = 0, Width = 120 };
        var expected = new TextBox { Width = 420 };
        var status = Text("Arrastra archivos aquí o añádelos con el botón.", "Caption");
        var grid = Grid();
        grid.ItemsSource = rows;
        grid.Columns.Add(TCol("Archivo", "Name", 220));
        grid.Columns.Add(TCol("Hash", "Hash", star: true).Also(c => c.FontFamily = new FontFamily("Cascadia Mono, Consolas")));
        grid.Columns.Add(TCol("Estado", "State", 150));
        grid.AllowDrop = true;
        grid.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] fs) foreach (var f in fs.Where(File.Exists)) rows.Add(new HashRow { Path = f }); };
        grid.ContextMenu = Menu(("Copiar hash", Icons.Copy, () => { if (grid.SelectedItem is HashRow r) Clipboard.SetText(r.Hash); }),
                                ("Copiar todo", Icons.Copy, () => Clipboard.SetText(string.Join("\n", rows.Select(r => $"{r.Hash} *{r.Name}")))),
                                ("Quitar", Icons.Delete, () => { foreach (var r in grid.SelectedItems.Cast<HashRow>().ToList()) rows.Remove(r); }));
        CancellationTokenSource cts = null;

        async void compute()
        {
            if (cts != null) { cts.Cancel(); return; }
            string a = (string)alg.SelectedItem; string exp = expected.Text.Trim().ToLowerInvariant();
            cts = new CancellationTokenSource(); var tk = cts.Token;
            try
            {
                foreach (var r in rows)
                {
                    long len = new FileInfo(r.Path).Length;
                    r.State = "Calculando…";
                    r.Hash = await Task.Run(() => FileHash.Compute(r.Path, a, tk, done => Dispatcher.BeginInvoke(() => r.State = len > 0 ? $"{done * 100 / len}%" : "")));
                    r.State = exp.Length > 0 ? (r.Hash == exp ? "✓ Coincide" : "✗ No coincide") : a;
                }
                status.Text = $"{rows.Count} archivo(s) calculados con {a}.";
            }
            catch (OperationCanceledException) { status.Text = "Cancelado"; }
            catch (Exception ex) { status.Text = "Error: " + ex.Message; }
            finally { cts = null; }
        }

        async void verifyFile()
        {
            var f = Dialogs.PickFile("Sumas de verificación|*.sha256;*.sha1;*.md5;*.sfv;*.txt|Todos|*.*");
            if (f == null) return;
            var entries = FileHash.ParseChecksumFile(f);
            if (entries.Count == 0) { await Dialogs.Info("Archivo vacío", "No se encontraron sumas en el archivo."); return; }
            rows.Clear();
            string dir = Path.GetDirectoryName(f)!;
            int ok = 0, bad = 0, missing = 0;
            foreach (var (hash, name) in entries)
            {
                var r = new HashRow { Path = Path.Combine(dir, name) };
                rows.Add(r);
                if (!File.Exists(r.Path)) { r.State = "✗ No existe"; missing++; continue; }
                string a = FileHash.GuessAlgorithm(hash);
                r.State = "Calculando…";
                r.Hash = await Task.Run(() => FileHash.Compute(r.Path, a, CancellationToken.None, null));
                if (r.Hash == hash) { r.State = "✓ Correcto"; ok++; } else { r.State = "✗ Dañado"; bad++; }
            }
            status.Text = $"Verificación: {ok} correctos, {bad} dañados, {missing} no encontrados.";
            (bad + missing == 0 ? (Action<string>)Log.Ok : Log.Warn)($"Verificación de {Path.GetFileName(f)}: {ok} OK, {bad} dañados, {missing} no encontrados");
        }

        void save()
        {
            if (rows.Count == 0 || rows.Any(r => r.Hash == "")) return;
            string ext = ((string)alg.SelectedItem).Replace("-", "").ToLowerInvariant();
            var f = Dialogs.SaveFile($"Sumas|*.{ext}", "sumas." + ext, Path.GetDirectoryName(rows[0].Path));
            if (f == null) return;
            string dir = Path.GetDirectoryName(f)!;
            File.WriteAllLines(f, rows.Select(r => $"{r.Hash} *{Path.GetRelativePath(dir, r.Path)}"));
            Toast.Show("Guardado", f, Icons.Save);
        }

        var top = Card(Col(
            Row(Btn("Añadir archivos", Icons.Add, (_, _) => { var fs = Dialogs.PickFiles(); if (fs != null) foreach (var f in fs) rows.Add(new HashRow { Path = f }); }, "SecondaryButton"),
                Text("Algoritmo", "Caption", margin: new Thickness(18, 0, 8, 0)), alg,
                Btn("Calcular", Icons.Fingerprint, (_, _) => compute(), "PrimaryButton").Also(b => b.Margin = new Thickness(18, 0, 0, 0)),
                Btn("Verificar archivo de sumas…", Icons.Check, (_, _) => verifyFile()).Also(b => b.Margin = new Thickness(8, 0, 0, 0)),
                Btn("Guardar sumas…", Icons.Save, (_, _) => save()),
                Btn("Limpiar", Icons.Close, (_, _) => rows.Clear())),
            Row(Text("Hash esperado (opcional)", "Caption", margin: new Thickness(0, 0, 8, 0)), expected).Also(r => r.Margin = new Thickness(0, 12, 0, 0)),
            status.Also(s => s.Margin = new Thickness(0, 10, 0, 0))), new Thickness(0, 0, 0, 14), 14);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        dock.Children.Add(Card(grid, new Thickness(0), 0));
        if (rows.Count > 0) Dispatcher.BeginInvoke(compute);
        return dock;
    }

    // ═════════════ Renombrado masivo ═════════════
    FrameworkElement RenameTool()
    {
        var items = new ObservableCollection<RenameItem>((presetFiles ?? []).Select(f => new RenameItem { Path = f }));
        var pattern = new TextBox { Text = "[N]" };
        var find = new TextBox(); var replace = new TextBox();
        var regex = new CheckBox { Content = "Expresión regular", VerticalAlignment = VerticalAlignment.Center };
        var caseBox = new ComboBox { ItemsSource = new[] { "Sin cambios", "minúsculas", "MAYÚSCULAS", "Tipo Título" }, SelectedIndex = 0, Width = 130 };
        var start = new TextBox { Text = "1", Width = 50 }; var step = new TextBox { Text = "1", Width = 50 }; var digits = new TextBox { Text = "2", Width = 50 };
        var status = Text("", "Caption");
        List<(string from, string to)> undo = null;
        Button undoBtn = null;
        var grid = Grid();
        grid.ItemsSource = items;
        grid.Columns.Add(TCol("Nombre actual", "OldName", star: true));
        grid.Columns.Add(TCol("Nombre nuevo", "NewName", star: true));
        grid.Columns.Add(TCol("Aviso", "Problem", 150));

        void preview()
        {
            var rule = new RenameRule
            {
                Pattern = pattern.Text, Find = find.Text, Replace = replace.Text, Regex = regex.IsChecked == true, Case = caseBox.SelectedIndex,
                Start = int.TryParse(start.Text, out var s) ? s : 1, Step = int.TryParse(step.Text, out var st) ? st : 1, Digits = int.TryParse(digits.Text, out var d) ? Math.Clamp(d, 1, 9) : 2
            };
            BatchRename.Preview(items, rule);
            int changes = items.Count(i => i.Changed && i.Problem == ""), problems = items.Count(i => i.Problem != "");
            status.Text = $"{items.Count} elementos · {changes} cambiarán" + (problems > 0 ? $" · {problems} con problemas (no se renombrarán)" : "");
        }
        foreach (var tb in new[] { pattern, find, replace, start, step, digits }) tb.TextChanged += (_, _) => preview();
        regex.Click += (_, _) => preview();
        caseBox.SelectionChanged += (_, _) => preview();

        UIElement token(string t, string tip) => Btn(t, null, (_, _) => { int c = pattern.CaretIndex; pattern.Text = pattern.Text.Insert(c, t); pattern.CaretIndex = c + t.Length; pattern.Focus(); }, "SecondaryButton", tip)
            .Also(b => { b.Padding = new Thickness(8, 3, 8, 3); b.Margin = new Thickness(0, 0, 6, 0); });

        async void apply()
        {
            int n = items.Count(i => i.Changed && i.Problem == "");
            if (n == 0) return;
            if (!await Dialogs.Confirm("Renombrar", $"Se renombrarán {n} elementos. Podrás deshacerlo después.", "Renombrar")) return;
            undo = await Task.Run(() => BatchRename.Apply(items));
            Log.Ok($"Renombrado masivo: {undo.Count} elementos");
            var updated = items.Select(i => undo.FirstOrDefault(u => u.from == i.Path) is var u && u.to != null ? u.to : i.Path).ToList();
            items.Clear(); foreach (var p in updated) items.Add(new RenameItem { Path = p });
            preview();
            undoBtn.IsEnabled = true;
        }

        void doUndo()
        {
            if (undo == null) return;
            int ok = 0;
            foreach (var (from, to) in undo.AsEnumerable().Reverse()) try { BatchRename.MoveAny(to, from); ok++; } catch (Exception ex) { Log.Error(ex.Message); }
            var restored = items.Select(i => undo.FirstOrDefault(u => u.to == i.Path) is var u && u.from != null ? u.from : i.Path).ToList();
            items.Clear(); foreach (var p in restored) items.Add(new RenameItem { Path = p });
            undo = null; undoBtn.IsEnabled = false;
            Log.Info($"Deshecho el renombrado de {ok} elementos");
            preview();
        }

        void addFolder()
        {
            var p = Dialogs.PickFolder(null, "Carpeta con archivos a renombrar");
            if (p == null) return;
            foreach (var f in Directory.GetFiles(p).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) items.Add(new RenameItem { Path = f });
            preview();
        }
        undoBtn = Btn("Deshacer", Icons.Undo, (_, _) => doUndo()).Also(b => b.IsEnabled = false);

        var form = Card(Col(
            Row(Btn("Añadir archivos", Icons.Add, (_, _) => { var fs = Dialogs.PickFiles(); if (fs != null) { foreach (var f in fs) items.Add(new RenameItem { Path = f }); preview(); } }, "SecondaryButton"),
                Btn("Añadir carpeta", Icons.Folder, (_, _) => addFolder(), "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0)),
                Btn("Limpiar lista", Icons.Close, (_, _) => { items.Clear(); preview(); })),
            Columns((Col(Text("Patrón del nombre", "Label"), pattern), Star), (Col(Text("Buscar", "Label"), find).Also(c => c.Margin = new Thickness(14, 0, 0, 0)), Star),
                    (Col(Text("Reemplazar por", "Label"), replace).Also(c => c.Margin = new Thickness(14, 0, 0, 0)), Star)).Also(g => g.Margin = new Thickness(0, 14, 0, 8)),
            Row(token("[N]", "Nombre original"), token("[C]", "Contador"), token("[D]", "Fecha de modificación (yyyy-MM-dd)"), token("[D:yyyyMMdd_HHmm]", "Fecha con formato"),
                token("[P]", "Carpeta contenedora"), token("[E]", "Extensión"),
                regex.Also(r => r.Margin = new Thickness(12, 0, 16, 0)), Text("Mayúsculas", "Caption", margin: new Thickness(0, 0, 8, 0)), caseBox,
                Text("Contador: inicio", "Caption", margin: new Thickness(16, 0, 6, 0)), start, Text("paso", "Caption", margin: new Thickness(8, 0, 6, 0)), step,
                Text("dígitos", "Caption", margin: new Thickness(8, 0, 6, 0)), digits),
            Columns((status, Star), (undoBtn, Auto), (Btn("Renombrar", Icons.Rename, (_, _) => apply(), "PrimaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0)), Auto)).Also(g => g.Margin = new Thickness(0, 14, 0, 0))),
            new Thickness(0, 0, 0, 14), 14);
        var dock = new DockPanel();
        DockPanel.SetDock(form, Dock.Top); dock.Children.Add(form);
        dock.Children.Add(Card(grid, new Thickness(0), 0));
        preview();
        return dock;
    }

    // ═════════════ Borrado seguro ═════════════
    FrameworkElement WipeTool()
    {
        var items = new ObservableCollection<string>(presetFiles ?? []);
        var list = new ListBox { ItemsSource = items, MinHeight = 160, MaxHeight = 300, AllowDrop = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        list.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] fs) foreach (var f in fs) items.Add(f); };
        var passes = new ComboBox { ItemsSource = new[] { "1 pasada (aleatoria) — rápido", "3 pasadas (ceros, unos, aleatoria)", "7 pasadas — máxima" }, SelectedIndex = 0, Width = 280 };
        var vols = AppState.Volumes.Where(v => v.Ready && v.Type is DriveType.Fixed or DriveType.Removable).ToList();
        var vol = new ComboBox { ItemsSource = vols, SelectedIndex = 0, MinWidth = 260 };

        async void shred()
        {
            if (items.Count == 0) return;
            if (items.Any(p => p.Length <= 3 || string.Equals(p.TrimEnd('\\'), Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)))
            { await Dialogs.Error("No permitido", "No se puede triturar una unidad completa ni la carpeta de Windows."); return; }
            if (!await Dialogs.TypeToConfirm("Borrado irrecuperable", $"Se destruirán {items.Count} elemento(s). Ningún programa de recuperación podrá restaurarlos.", "BORRAR", "Destruir")) return;
            int n = passes.SelectedIndex switch { 1 => 3, 2 => 7, _ => 1 };
            TransferQueue.Enqueue(Shredder.ShredJob(items.ToList(), n));
            items.Clear();
            MainWindow.Instance.Navigate("transfers");
        }

        async void wipeFree()
        {
            if (vol.SelectedItem is not VolumeInfo v) return;
            if (await Dialogs.Confirm("Limpiar espacio libre", $"Se sobrescribirá el espacio libre de {v.Letter} ({Fmt.Size(v.Free)}) para que no se puedan recuperar archivos borrados. Tus archivos no se tocan. Durante el proceso el disco aparecerá lleno.", "Limpiar"))
            { TransferQueue.Enqueue(Shredder.WipeFreeSpaceJob(v.Root)); MainWindow.Instance.Navigate("transfers"); }
        }

        var c1 = Card(Col(
            Text("Triturar archivos y carpetas", "SectionTitle"),
            Text("Sobrescribe el contenido antes de borrarlo. En SSD, por cómo funcionan internamente, se recomienda además «Limpiar espacio libre» o el borrado seguro del fabricante.", "Caption", 13).Also(t => { t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 0, 0, 12); }),
            Row(Btn("Añadir archivos", Icons.Add, (_, _) => { var fs = Dialogs.PickFiles(); if (fs != null) foreach (var f in fs) items.Add(f); }, "SecondaryButton"),
                Btn("Añadir carpeta", Icons.Folder, (_, _) => { var p = Dialogs.PickFolder(); if (p != null) items.Add(p); }, "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0)),
                Btn("Quitar", Icons.Close, (_, _) => { if (list.SelectedItem is string s) items.Remove(s); })),
            new Border { Style = S("SubtleCard"), Margin = new Thickness(0, 12, 0, 12), Child = list },
            Row(Text("Método", "Caption", margin: new Thickness(0, 0, 8, 0)), passes, Btn("Destruir", Icons.Wipe, (_, _) => shred(), "DangerButton").Also(b => b.Margin = new Thickness(16, 0, 0, 0)))));
        var c2 = Card(Col(
            Text("Limpiar espacio libre", "SectionTitle"),
            Text("Sobrescribe solo el espacio vacío del volumen. Útil antes de vender o regalar un disco sin formatearlo.", "Caption", 13).Also(t => { t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 0, 0, 12); }),
            Row(Text("Volumen", "Caption", margin: new Thickness(0, 0, 8, 0)), vol, Btn("Limpiar", Icons.Wipe, (_, _) => wipeFree(), "PrimaryButton").Also(b => b.Margin = new Thickness(16, 0, 0, 0)))));
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Col(c1, c2).Also(c => c.MaxWidth = 900).Also(c => c.HorizontalAlignment = HorizontalAlignment.Left) };
    }
}
