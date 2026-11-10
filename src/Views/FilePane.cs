using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

public class FileItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDir { get; init; }
    public bool IsParent { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Type { get; init; } = "";
    public ImageSource Icon { get; set; }
    public bool Hidden { get; init; }
    public double Opacity => Hidden ? 0.55 : 1;
    public string SizeText => IsParent ? "" : IsDir ? "" : Fmt.Size(Size);
    public string ModifiedText => IsParent ? "" : Modified.ToString("dd/MM/yyyy HH:mm");
}

public class PaneTab
{
    public string Path;
    public readonly Stack<string> Back = new(), Forward = new();
    public string Title => System.IO.Path.GetFileName(Path?.TrimEnd('\\')) is { Length: > 0 } n ? n : Path ?? "";
}

/// <summary>Panel de archivos con pestañas.</summary>
public class FilePane : Border
{
    public readonly DataGrid Grid = new();
    readonly StackPanel tabStrip = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel crumbs = new() { Orientation = Orientation.Horizontal };
    readonly TextBox pathBox = new() { Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };
    readonly Border crumbHost = new();
    ScrollViewer crumbScroll;
    readonly TextBox filterBox = new() { Width = 150 };
    readonly TextBlock status = new();
    readonly ComboBox driveBox = new() { Width = 64 };
    readonly Button backBtn, fwdBtn, upBtn;
    readonly List<PaneTab> tabs = [];
    PaneTab tab;
    ObservableCollection<FileItem> items = [];
    ListCollectionView view;
    FileSystemWatcher watcher;
    readonly DispatcherTimer watchTimer;
    Point dragStart;
    bool dragArmed;
    string sortProp = "Name";
    ListSortDirection sortDir = ListSortDirection.Ascending;

    public event Action<FilePane> Activated;
    public event Action<FilePane, string[], string, DragDropEffects> Dropped;   // panel, archivos, carpeta destino, efecto
    public event Action<FilePane, KeyEventArgs> Command;
    public Func<FilePane, ContextMenu> MenuFactory;

    public bool IsActive { get; private set; }
    public string CurrentPath => tab?.Path;
    public IEnumerable<string> TabPaths => tabs.Select(t => t.Path);

    public FilePane()
    {
        Style = S("Card");
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        ClipToBounds = true;

        backBtn = IconBtn(Icons.Back, "Atrás (Alt+←)", (_, _) => GoBack());
        fwdBtn = IconBtn(Icons.Forward, "Adelante (Alt+→)", (_, _) => GoForward());
        upBtn = IconBtn(Icons.Up, "Subir (Retroceso)", (_, _) => GoUp());
        var refresh = IconBtn(Icons.Refresh, "Actualizar (Ctrl+R)", (_, _) => Reload());
        var favBtn = IconBtn(Icons.Star, "Favoritos", null);
        favBtn.Click += (_, _) => ShowFavorites(favBtn);

        driveBox.SelectionChanged += (_, _) =>
        {
            if (suppressDrive || driveBox.SelectedItem is not VolumeInfo v) return;
            Navigate(v.Root);
        };
        driveBox.DisplayMemberPath = "Letter";
        driveBox.ToolTip = "Unidad";

        // Barra de ruta (migas de pan) editable
        crumbHost.CornerRadius = new CornerRadius(6);
        crumbHost.Padding = new Thickness(4, 0, 4, 0);
        crumbHost.Height = 32;
        crumbHost.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        crumbHost.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        crumbHost.BorderThickness = new Thickness(1);
        crumbHost.Cursor = Cursors.IBeam;
        crumbScroll = new ScrollViewer { Content = crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var crumbGrid = new Grid();
        crumbGrid.Children.Add(crumbScroll);
        crumbGrid.Children.Add(pathBox);
        crumbHost.Child = crumbGrid;
        crumbHost.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not TextBlock { Tag: "crumb" }) EditPath(); };
        pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { EndEditPath(); Navigate(pathBox.Text); e.Handled = true; }
            if (e.Key == Key.Escape) { EndEditPath(); e.Handled = true; }
        };
        pathBox.LostKeyboardFocus += (_, _) => EndEditPath();
        pathBox.BorderThickness = new Thickness(0);
        pathBox.Background = Brushes.Transparent;

        filterBox.ToolTip = "Filtrar (Ctrl+F) · admite comodines * ?";
        filterBox.TextChanged += (_, _) => view?.Refresh();
        filterBox.KeyDown += (_, e) => { if (e.Key == Key.Escape) { filterBox.Text = ""; Grid.Focus(); } if (e.Key == Key.Down) { Grid.Focus(); e.Handled = true; } };
        var filterWrap = new Grid { Margin = new Thickness(6, 0, 0, 0) };
        filterWrap.Children.Add(filterBox);
        var ph = Text("Filtrar…", "Caption", 13).Also(t => { t.IsHitTestVisible = false; t.Margin = new Thickness(10, 0, 0, 0); });
        filterBox.TextChanged += (_, _) => ph.Visibility = filterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        filterWrap.Children.Add(ph);

        var addTab = IconBtn(Icons.Add, "Nueva pestaña (Ctrl+T)", (_, _) => NewTab(CurrentPath));
        var tabRow = Columns((new ScrollViewer { Content = tabStrip, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, Auto), (addTab, Auto), (null, Star));
        tabRow.Margin = new Thickness(8, 8, 8, 0);

        var navRow = Columns((Row(backBtn, fwdBtn, upBtn, refresh), Auto), (driveBox.Also(d => d.Margin = new Thickness(4, 0, 6, 0)), Auto), (crumbHost, Star), (filterWrap, Auto), (favBtn, Auto));
        navRow.Margin = new Thickness(8, 6, 8, 8);

        // Lista de archivos
        Grid.AutoGenerateColumns = false;
        Grid.IsReadOnly = true;
        Grid.SelectionMode = DataGridSelectionMode.Extended;
        Grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        Grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        Grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        Grid.CanUserResizeRows = false;
        Grid.CanUserReorderColumns = false;
        Grid.AllowDrop = true;
        Grid.BorderThickness = new Thickness(0);
        Grid.Background = Brushes.Transparent;
        Grid.RowBackground = Brushes.Transparent;
        Grid.AlternatingRowBackground = Brushes.Transparent;
        Grid.EnableRowVirtualization = true;
        VirtualizingPanel.SetIsVirtualizing(Grid, true);
        VirtualizingPanel.SetVirtualizationMode(Grid, VirtualizationMode.Recycling);
        Grid.RowHeight = 30;

        var nameCol = new DataGridTemplateColumn { Header = "Nombre", Width = new DataGridLength(1, DataGridLengthUnitType.Star), SortMemberPath = "Name", CanUserSort = true };
        var f = new FrameworkElementFactory(typeof(StackPanel));
        f.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        f.SetBinding(UIElement.OpacityProperty, new Binding("Opacity"));
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new Binding("Icon"));
        img.SetValue(WidthProperty, 16.0); img.SetValue(HeightProperty, 16.0);
        img.SetValue(MarginProperty, new Thickness(4, 0, 10, 0));
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        var txt = new FrameworkElementFactory(typeof(TextBlock));
        txt.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        txt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        f.AppendChild(img); f.AppendChild(txt);
        nameCol.CellTemplate = new DataTemplate { VisualTree = f };
        Grid.Columns.Add(nameCol);
        var right = new Style(typeof(TextBlock)); right.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right)); right.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center)); right.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0, 0, 12, 0)));
        var secondary = new Style(typeof(TextBlock)); secondary.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center)); secondary.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"))); secondary.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        Grid.Columns.Add(new DataGridTextColumn { Header = "Tamaño", Binding = new Binding("SizeText"), SortMemberPath = "Size", Width = 90, ElementStyle = right });
        Grid.Columns.Add(new DataGridTextColumn { Header = "Tipo", Binding = new Binding("Type"), SortMemberPath = "Type", Width = 130, ElementStyle = secondary });
        Grid.Columns.Add(new DataGridTextColumn { Header = "Modificado", Binding = new Binding("ModifiedText"), SortMemberPath = "Modified", Width = 128, ElementStyle = secondary });

        Grid.Sorting += OnSorting;
        Grid.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) != null) OpenSelected(); };
        Grid.PreviewKeyDown += OnKey;
        Grid.SelectionChanged += (_, _) => UpdateStatus();
        Grid.GotKeyboardFocus += (_, _) => SetActive();
        Grid.PreviewMouseDown += (_, _) => SetActive();
        Grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(null);
            dragArmed = e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) is DataGridRow r && r.IsSelected;
        };
        Grid.PreviewMouseMove += OnDragMove;
        Grid.DragOver += OnDragOver;
        Grid.DragEnter += OnDragOver;
        Grid.Drop += OnDrop;
        Grid.ContextMenuOpening += (_, e) =>
        {
            SetActive();
            if (MenuFactory != null) Grid.ContextMenu = MenuFactory(this);
        };
        Grid.ContextMenu = new ContextMenu();

        status.Margin = new Thickness(14, 6, 14, 8);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        status.FontSize = 12;

        var dock = new DockPanel();
        DockPanel.SetDock(tabRow, Dock.Top); dock.Children.Add(tabRow);
        DockPanel.SetDock(navRow, Dock.Top); dock.Children.Add(navRow);
        DockPanel.SetDock(status, Dock.Bottom); dock.Children.Add(status);
        dock.Children.Add(Grid);
        Child = dock;

        watchTimer = AppState.Debounce(TimeSpan.FromMilliseconds(600), () => Reload(true));
        AppState.Changed += RefreshDrives;
        RefreshDrives();
    }

    // ───────────── Estado ─────────────
    public void SetActive()
    {
        if (IsActive) return;
        IsActive = true;
        SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
        BorderThickness = new Thickness(1.5);
        Activated?.Invoke(this);
    }

    public void SetInactive()
    {
        IsActive = false;
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        BorderThickness = new Thickness(1);
    }

    bool suppressDrive;
    void RefreshDrives()
    {
        suppressDrive = true;
        driveBox.ItemsSource = AppState.Volumes.Where(v => v.Ready).ToList();
        SyncDrive();
        suppressDrive = false;
        if (CurrentPath != null && !Directory.Exists(CurrentPath)) Reload();
    }

    void SyncDrive()
    {
        suppressDrive = true;
        var root = Path.GetPathRoot(CurrentPath ?? "");
        driveBox.SelectedItem = (driveBox.ItemsSource as List<VolumeInfo>)?.Find(v => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase));
        suppressDrive = false;
    }

    // ───────────── Pestañas ─────────────
    public void NewTab(string path)
    {
        var t = new PaneTab { Path = path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        tabs.Add(t);
        SelectTab(t);
    }

    public void CloseTab(PaneTab t = null)
    {
        t ??= tab;
        if (tabs.Count <= 1) return;
        int i = tabs.IndexOf(t);
        tabs.Remove(t);
        if (t == tab) SelectTab(tabs[Math.Min(i, tabs.Count - 1)]); else RenderTabs();
    }

    void SelectTab(PaneTab t)
    {
        tab = t;
        string p = t.Path;
        if (!Directory.Exists(p)) p = FirstExisting(p);
        t.Path = p;
        Load(p, null);
        RenderTabs();
    }

    void RenderTabs()
    {
        tabStrip.Children.Clear();
        foreach (var t in tabs)
        {
            var tt = t;
            bool sel = t == tab;
            var b = new Border { CornerRadius = new CornerRadius(6, 6, 6, 6), Padding = new Thickness(10, 5, 4, 5), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand, MaxWidth = 200 };
            b.SetResourceReference(Border.BackgroundProperty, sel ? "ControlFillColorDefaultBrush" : "SubtleFillColorTransparentBrush");
            if (sel) { b.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush"); b.BorderThickness = new Thickness(1); }
            var close = new Button { Style = S("IconButton"), Tag = Icons.Close, Padding = new Thickness(4), FontSize = 9, Margin = new Thickness(4, 0, 0, 0), Visibility = tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed };
            close.Click += (_, _) => CloseTab(tt);
            b.Child = Row(Icon(Icons.Folder, 12, sel ? B("AccentTextFillColorPrimaryBrush") : null, new Thickness(0, 0, 6, 0)), Text(t.Title, size: 12, weight: sel ? FontWeights.SemiBold : FontWeights.Normal), close);
            b.ToolTip = t.Path;
            b.MouseLeftButtonUp += (_, _) => { if (tab != tt) SelectTab(tt); SetActive(); };
            b.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tt); };
            tabStrip.Children.Add(b);
        }
    }

    // ───────────── Navegación ─────────────
    public void Navigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (path.Length == 2 && path[1] == ':') path += "\\";
            path = Path.GetFullPath(path);
            if (File.Exists(path)) { string file = path; path = Path.GetDirectoryName(path)!; Go(path, file); return; }
            if (!Directory.Exists(path)) { _ = Dialogs.Error("No existe", path); return; }
            Directory.GetFileSystemEntries(path).Take(1).ToArray();
        }
        catch (Exception ex) { _ = Dialogs.Error("No se puede abrir la carpeta", ex.Message); return; }
        Go(path, null);
    }

    void Go(string path, string select)
    {
        if (tab == null) { NewTab(path); return; }
        if (!string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            if (tab.Path != null) tab.Back.Push(tab.Path);
            tab.Forward.Clear();
        }
        string prev = tab.Path;
        tab.Path = path;
        Load(path, select ?? prev);
        RenderTabs();
    }

    public void GoBack() { if (tab?.Back.Count > 0) { tab.Forward.Push(tab.Path); string prev = tab.Path; tab.Path = tab.Back.Pop(); Load(tab.Path, prev); RenderTabs(); } }
    public void GoForward() { if (tab?.Forward.Count > 0) { tab.Back.Push(tab.Path); tab.Path = tab.Forward.Pop(); Load(tab.Path, null); RenderTabs(); } }
    public void GoUp() { var p = Directory.GetParent(CurrentPath ?? "")?.FullName; if (p != null) Go(p, CurrentPath); }

    public void Reload(bool keepSelection = true)
    {
        if (CurrentPath == null) return;
        if (!Directory.Exists(CurrentPath)) { tab.Path = FirstExisting(CurrentPath); Load(tab.Path, null); RenderTabs(); return; }
        var sel = keepSelection ? SelectedPaths().ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        Load(CurrentPath, null, sel);
    }

    static string FirstExisting(string p)
    {
        while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
        return string.IsNullOrEmpty(p) ? Path.GetPathRoot(Environment.SystemDirectory)! : p;
    }

    int loadVersion;
    async void Load(string path, string select, HashSet<string> reselect = null)
    {
        int ver = ++loadVersion;
        backBtn.IsEnabled = tab?.Back.Count > 0;
        fwdBtn.IsEnabled = tab?.Forward.Count > 0;
        upBtn.IsEnabled = Directory.GetParent(path) != null;
        RenderCrumbs(path);
        SyncDrive();
        bool hidden = Settings.Current.ShowHidden;
        List<FileItem> list;
        try { list = await Task.Run(() => Enumerate(path, hidden)); }
        catch (Exception ex) { list = []; status.Text = "Error: " + ex.Message; }
        if (ver != loadVersion) return;
        foreach (var it in list) it.Icon = ShellIcons.Get(it.IsParent ? path : it.FullPath, it.IsDir);   // en el hilo de interfaz (STA)

        items = new ObservableCollection<FileItem>(list);
        view = (ListCollectionView)CollectionViewSource.GetDefaultView(items);
        view.Filter = FilterItem;
        view.CustomSort = new Sorter(sortProp, sortDir);
        Grid.ItemsSource = view;
        foreach (var c in Grid.Columns) c.SortDirection = c.SortMemberPath == sortProp ? sortDir : null;

        FileItem focus = null;
        if (reselect != null && reselect.Count > 0)
        {
            foreach (var it in list.Where(i => reselect.Contains(i.FullPath))) Grid.SelectedItems.Add(it);
            focus = Grid.SelectedItems.Count > 0 ? (FileItem)Grid.SelectedItems[0] : null;
        }
        else if (select != null)
        {
            focus = list.Find(i => string.Equals(i.FullPath, select.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (focus != null) Grid.SelectedItem = focus;
        }
        if (focus != null) Grid.ScrollIntoView(focus);
        UpdateStatus();
        Watch(path);
    }

    static List<FileItem> Enumerate(string path, bool hidden)
    {
        var list = new List<FileItem>();
        var di = new DirectoryInfo(path);
        if (di.Parent != null) list.Add(new FileItem { Name = "..", FullPath = di.Parent.FullName, IsDir = true, IsParent = true });
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = hidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
        foreach (var e in di.EnumerateFileSystemInfos("*", opts))
        {
            bool isDir = e is DirectoryInfo;
            if (isDir && e.Name is "System Volume Information" or "$RECYCLE.BIN") continue;
            list.Add(new FileItem
            {
                Name = e.Name, FullPath = e.FullName, IsDir = isDir,
                Size = e is FileInfo f ? f.Length : 0, Modified = e.LastWriteTime,
                Type = isDir ? "Carpeta" : ShellIcons.TypeName(e.Extension),
                Hidden = (e.Attributes & FileAttributes.Hidden) != 0
            });
        }
        return list;
    }

    void Watch(string path)
    {
        watcher?.Dispose();
        watcher = null;
        try
        {
            watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { watchTimer.Stop(); watchTimer.Start(); });
            watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
            watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { watchTimer.Stop(); watchTimer.Start(); });
            watcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    bool FilterItem(object o)
    {
        var it = (FileItem)o;
        string f = filterBox.Text.Trim();
        if (f.Length == 0 || it.IsParent) return true;
        if (f.Contains('*') || f.Contains('?'))
            return System.Text.RegularExpressions.Regex.IsMatch(it.Name, "^" + System.Text.RegularExpressions.Regex.Escape(f).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return it.Name.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    void RenderCrumbs(string path)
    {
        crumbs.Children.Clear();
        var parts = new List<string>();
        for (var d = new DirectoryInfo(path); d != null; d = d.Parent) parts.Insert(0, d.FullName);
        for (int i = 0; i < parts.Count; i++)
        {
            string p = parts[i];
            string name = i == 0 ? p.TrimEnd('\\') : Path.GetFileName(p);
            if (i == 0 && AppState.VolumeOf(p) is VolumeInfo v && !string.IsNullOrEmpty(v.Label)) name = $"{v.Label} ({v.Letter})";
            var t = new TextBlock { Text = name, Tag = "crumb", Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, FontSize = 13 };
            t.SetResourceReference(TextBlock.ForegroundProperty, i == parts.Count - 1 ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            if (i == parts.Count - 1) t.FontWeight = FontWeights.SemiBold;
            t.MouseEnter += (_, _) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (_, _) => t.TextDecorations = null;
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; Go(p, null); };
            crumbs.Children.Add(t);
            if (i < parts.Count - 1) crumbs.Children.Add(Icon(Icons.Chevron, 9, B("TextFillColorTertiaryBrush")));
        }
        Dispatcher.BeginInvoke(() => crumbScroll?.ScrollToRightEnd(), DispatcherPriority.Loaded);
    }

    void EditPath()
    {
        pathBox.Text = CurrentPath;
        pathBox.Visibility = Visibility.Visible;
        crumbs.Visibility = Visibility.Hidden;
        pathBox.Focus(); pathBox.SelectAll();
    }

    void EndEditPath()
    {
        pathBox.Visibility = Visibility.Collapsed;
        crumbs.Visibility = Visibility.Visible;
    }

    public void FocusFilter() { filterBox.Focus(); filterBox.SelectAll(); }

    void UpdateStatus()
    {
        int dirs = items.Count(i => i.IsDir && !i.IsParent), files = items.Count(i => !i.IsDir);
        var sel = SelectedItems();
        string free = "";
        if (AppState.VolumeOf(CurrentPath) is VolumeInfo v) free = $"   ·   {Fmt.Size(v.Free)} libres en {v.Letter}";
        status.Text = sel.Count > 0
            ? $"{sel.Count} seleccionados · {Fmt.Size(sel.Where(i => !i.IsDir).Sum(i => i.Size))}{free}"
            : $"{dirs} carpetas, {files} archivos · {Fmt.Size(items.Where(i => !i.IsDir).Sum(i => i.Size))}{free}";
    }

    void ShowFavorites(Button anchor)
    {
        var favs = Settings.Current.Favorites;
        var m = new ContextMenu { PlacementTarget = anchor };
        var add = new MenuItem { Header = "Añadir esta carpeta a favoritos", Icon = Icon(Icons.Add, 14) };
        add.Click += (_, _) => { if (!favs.Contains(CurrentPath)) { favs.Add(CurrentPath); Settings.Save(); } };
        m.Items.Add(add);
        if (favs.Count > 0) m.Items.Add(new Separator());
        foreach (var f in favs.ToList())
        {
            var mi = new MenuItem { Header = f, Icon = Icon(Icons.Star, 14) };
            mi.Click += (_, _) => Navigate(f);
            var rm = new MenuItem { Header = "Quitar de favoritos" };
            rm.Click += (_, _) => { favs.Remove(f); Settings.Save(); };
            mi.ContextMenu = new ContextMenu { Items = { rm } };
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        foreach (var (name, folder) in new[] { ("Escritorio", Environment.SpecialFolder.Desktop), ("Documentos", Environment.SpecialFolder.MyDocuments), ("Imágenes", Environment.SpecialFolder.MyPictures), ("Usuario", Environment.SpecialFolder.UserProfile) })
        {
            string p = Environment.GetFolderPath(folder);
            var mi = new MenuItem { Header = name, Icon = Icon(Icons.Folder, 14) };
            mi.Click += (_, _) => Navigate(p);
            m.Items.Add(mi);
        }
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dl = new MenuItem { Header = "Descargas", Icon = Icon(Icons.Download, 14) };
        dl.Click += (_, _) => Navigate(downloads);
        m.Items.Add(dl);
        m.IsOpen = true;
    }

    // ───────────── Selección y acciones ─────────────
    public List<FileItem> SelectedItems() => Grid.SelectedItems.Cast<FileItem>().Where(i => !i.IsParent).ToList();
    public string[] SelectedPaths() => SelectedItems().Select(i => i.FullPath).ToArray();
    public FileItem FocusedItem => Grid.SelectedItem as FileItem;

    public void OpenSelected()
    {
        var it = FocusedItem;
        if (it == null) return;
        if (it.IsParent) { GoUp(); return; }
        if (it.IsDir) { Go(it.FullPath, null); return; }
        if (it.FullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && Keyboard.Modifiers == ModifierKeys.Control) return;
        Native.Open(it.FullPath);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var mod = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Enter && mod == ModifierKeys.None) { OpenSelected(); e.Handled = true; return; }
        if (key == Key.Back) { GoUp(); e.Handled = true; return; }
        if (mod == ModifierKeys.Alt && key == Key.Left) { GoBack(); e.Handled = true; return; }
        if (mod == ModifierKeys.Alt && key == Key.Right) { GoForward(); e.Handled = true; return; }
        if (mod == ModifierKeys.Alt && key == Key.Up) { GoUp(); e.Handled = true; return; }
        if (mod == ModifierKeys.Control && key == Key.T) { NewTab(CurrentPath); e.Handled = true; return; }
        if (mod == ModifierKeys.Control && key == Key.W) { CloseTab(); e.Handled = true; return; }
        if (mod == ModifierKeys.Control && key == Key.F) { FocusFilter(); e.Handled = true; return; }
        if (mod == ModifierKeys.Control && key == Key.L) { EditPath(); e.Handled = true; return; }
        if (mod == ModifierKeys.Control && key == Key.A) { Grid.SelectAll(); foreach (var p in items.Where(i => i.IsParent)) Grid.SelectedItems.Remove(p); e.Handled = true; return; }
        // Búsqueda rápida por letra
        if (mod == ModifierKeys.None && key >= Key.A && key <= Key.Z)
        {
            char c = (char)('a' + (key - Key.A));
            var list = view.Cast<FileItem>().ToList();
            int start = FocusedItem != null ? list.IndexOf(FocusedItem) + 1 : 0;
            var hit = list.Skip(start).Concat(list.Take(start)).FirstOrDefault(i => !i.IsParent && char.ToLowerInvariant(i.Name[0]) == c);
            if (hit != null) { Grid.SelectedItem = hit; Grid.ScrollIntoView(hit); FocusRow(hit); }
            e.Handled = true; return;
        }
        Command?.Invoke(this, e);
    }

    void FocusRow(FileItem it)
    {
        Grid.UpdateLayout();
        if (Grid.ItemContainerGenerator.ContainerFromItem(it) is DataGridRow r) r.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    public void FocusList()
    {
        Grid.Focus();
        if (Grid.SelectedItem == null && view != null && view.Count > 0) Grid.SelectedIndex = 0;
        if (Grid.SelectedItem is FileItem it) { Grid.ScrollIntoView(it); Dispatcher.BeginInvoke(() => FocusRow(it), DispatcherPriority.Background); }
    }

    void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string prop = e.Column.SortMemberPath;
        sortDir = prop == sortProp && sortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        sortProp = prop;
        foreach (var c in Grid.Columns) c.SortDirection = c == e.Column ? sortDir : null;
        if (view != null) view.CustomSort = new Sorter(sortProp, sortDir);
    }

    class Sorter(string prop, ListSortDirection dir) : IComparer
    {
        public int Compare(object x, object y)
        {
            var a = (FileItem)x; var b = (FileItem)y;
            if (a.IsParent) return -1; if (b.IsParent) return 1;
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            int r = prop switch
            {
                "Size" => a.Size.CompareTo(b.Size),
                "Modified" => a.Modified.CompareTo(b.Modified),
                "Type" => string.Compare(a.Type, b.Type, StringComparison.CurrentCultureIgnoreCase),
                _ => NaturalCompare(a.Name, b.Name)
            };
            if (r == 0) r = NaturalCompare(a.Name, b.Name);
            return dir == ListSortDirection.Ascending ? r : -r;
        }

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);
        static int NaturalCompare(string a, string b) => StrCmpLogicalW(a, b);
    }

    // ───────────── Arrastrar y soltar ─────────────
    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !dragArmed) return;
        var d = e.GetPosition(null) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = SelectedPaths();
        if (paths.Length == 0) return;
        dragArmed = false;
        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(Grid, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    string DropTarget(DragEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d)?.Item is FileItem { IsDir: true, IsParent: false } it) return it.FullPath;
        return CurrentPath;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || CurrentPath == null) { e.Effects = DragDropEffects.None; return; }
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        string target = DropTarget(e);
        if (files.Any(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase))) { e.Effects = DragDropEffects.None; return; }
        bool same = files.Length > 0 && string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) e.Effects = DragDropEffects.Move;
        else if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) e.Effects = DragDropEffects.Copy;
        else e.Effects = same ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        string target = DropTarget(e);
        files = files.Where(f => !string.Equals(Path.GetDirectoryName(f.TrimEnd('\\'))?.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return;
        OnDragOver(sender, e);
        SetActive();
        Dropped?.Invoke(this, files, target, e.Effects);
    }

    public static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
