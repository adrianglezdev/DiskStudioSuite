using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DiskStudio.Core;
using Microsoft.VisualBasic.FileIO;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

public partial class ExplorerView : UserControl
{
    public readonly FilePane Left = new(), Right = new();
    FilePane active;
    public FilePane Active => active;
    FilePane Other => active == Left ? Right : Left;

    public ExplorerView()
    {
        InitializeComponent();
        LeftHost.Content = Left;
        RightHost.Content = Right;
        HiddenToggle.IsChecked = Settings.Current.ShowHidden;
        foreach (var p in new[] { Left, Right })
        {
            p.Activated += SetActive;
            p.Dropped += OnDropped;
            p.Command += OnCommand;
            p.MenuFactory = BuildMenu;
        }
        string sys = Path.GetPathRoot(Environment.SystemDirectory)!;
        Left.NewTab(Settings.Current.LeftPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Right.NewTab(Settings.Current.RightPath ?? FirstExternal() ?? sys);
        SetActive(Left);
        Loaded += (_, _) => active.FocusList();
        PreviewKeyDown += OnGlobalKey;
    }

    static string FirstExternal() => AppState.Volumes.FirstOrDefault(v => v.Ready && v.IsRemovable)?.Root;

    public void SaveState()
    {
        Settings.Current.LeftPath = Left.CurrentPath;
        Settings.Current.RightPath = Right.CurrentPath;
    }

    void SetActive(FilePane p)
    {
        active = p;
        (p == Left ? Right : Left).SetInactive();
        p.SetActive();
    }

    public void Open(string path)
    {
        if (active == null) SetActive(Left);
        active.Navigate(path);
        Dispatcher.BeginInvoke(() => active.FocusList(), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ───────────── Teclado ─────────────
    void OnGlobalKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && (Left.Grid.IsKeyboardFocusWithin || Right.Grid.IsKeyboardFocusWithin))
        {
            SetActive(Other); active.FocusList(); e.Handled = true;
        }
        else if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control) { HiddenToggle.IsChecked = !HiddenToggle.IsChecked; e.Handled = true; }
        else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { active.Reload(); e.Handled = true; }
    }

    void OnCommand(FilePane p, KeyEventArgs e)
    {
        var mod = Keyboard.Modifiers;
        bool h = true;
        switch (e.Key)
        {
            case Key.F2: OnRename(null, null); break;
            case Key.F3: QuickView(); break;
            case Key.F5: Transfer(false); break;
            case Key.F6: Transfer(true); break;
            case Key.F7: OnNewFolder(null, null); break;
            case Key.F8: case Key.Delete: Delete(mod == ModifierKeys.Shift); break;
            case Key.C when mod == ModifierKeys.Control: ToClipboard(false); break;
            case Key.X when mod == ModifierKeys.Control: ToClipboard(true); break;
            case Key.V when mod == ModifierKeys.Control: Paste(); break;
            case Key.N when mod == (ModifierKeys.Control | ModifierKeys.Shift): OnNewFolder(null, null); break;
            default: h = false; break;
        }
        e.Handled = h;
    }

    // ───────────── Copiar / mover ─────────────
    void OnCopy(object s, RoutedEventArgs e) => Transfer(false);
    void OnMove(object s, RoutedEventArgs e) => Transfer(true);

    async void Transfer(bool move)
    {
        var sel = active.SelectedPaths();
        string dest = Other.CurrentPath;
        if (sel.Length == 0) { Toast.Show("Nada seleccionado", "Selecciona archivos o carpetas en el panel activo.", Icons.Info); return; }
        if (string.Equals(dest?.TrimEnd('\\'), active.CurrentPath?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        { await Dialogs.Info("Mismo destino", "Los dos paneles muestran la misma carpeta. Abre otra carpeta en el panel de destino."); return; }

        var verify = new CheckBox { Content = "Verificar los datos después de copiar", IsChecked = Settings.Current.VerifyByDefault, Margin = new Thickness(0, 0, 0, 8) };
        var policy = new ComboBox
        {
            ItemsSource = new[] { "Preguntar", "Reemplazar siempre", "Omitir existentes", "Reemplazar si es más reciente", "Reemplazar si es diferente", "Conservar ambos" },
            SelectedIndex = (int)Settings.Current.Conflict
        };
        var panel = Col(verify, Text("Si el archivo ya existe:", "Label"), policy);
        string what = sel.Length == 1 ? $"«{Path.GetFileName(sel[0])}»" : $"{sel.Length} elementos";
        int r = await Dialogs.Show(move ? "Mover" : "Copiar", $"{what}\n→ {dest}", [move ? "Mover" : "Copiar", "Cancelar"], 0, extra: panel, glyph: move ? Icons.Move : Icons.Copy);
        if (r != 0) return;
        var opt = new CopyOptions { Verify = verify.IsChecked == true, Conflict = (ConflictPolicy)policy.SelectedIndex };
        TransferQueue.Enqueue(FileOps.CopyJob(sel, dest, move, opt));
    }

    void OnDropped(FilePane target, string[] files, string folder, DragDropEffects eff)
    {
        bool move = eff == DragDropEffects.Move;
        TransferQueue.Enqueue(FileOps.CopyJob(files, folder, move));
    }

    void ToClipboard(bool cut)
    {
        var sel = active.SelectedPaths();
        if (sel.Length == 0) return;
        var data = new DataObject();
        var col = new StringCollection(); col.AddRange(sel);
        data.SetFileDropList(col);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        Clipboard.SetDataObject(data, true);
        Toast.Show(cut ? "Cortado" : "Copiado", $"{sel.Length} elemento(s) en el portapapeles", cut ? Icons.Move : Icons.Copy);
    }

    void Paste()
    {
        if (!Clipboard.ContainsFileDropList()) return;
        var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
        bool move = false;
        if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
        {
            var b = new byte[4]; ms.Read(b, 0, 4);
            move = (BitConverter.ToInt32(b, 0) & 2) != 0;
        }
        TransferQueue.Enqueue(FileOps.CopyJob(files, active.CurrentPath, move));
        if (move) Clipboard.Clear();
    }

    // ───────────── Archivos ─────────────
    async void OnNewFolder(object s, RoutedEventArgs e)
    {
        string name = await Dialogs.Prompt("Nueva carpeta", "Nombre:", "Nueva carpeta", "Crear");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            string p = Path.Combine(active.CurrentPath, name);
            Directory.CreateDirectory(p);
            Log.Info("Carpeta creada: " + p);
            active.Reload();
        }
        catch (Exception ex) { await Dialogs.Error("No se pudo crear la carpeta", ex.Message); }
    }

    async void OnRename(object s, RoutedEventArgs e)
    {
        var sel = active.SelectedPaths();
        if (sel.Length == 0) return;
        if (sel.Length > 1) { MainWindow.Instance.Navigate<ToolsView>("tools").Open("rename", null, sel); return; }
        string old = sel[0];
        string name = await Dialogs.Prompt("Renombrar", "Nuevo nombre:", Path.GetFileName(old), "Renombrar");
        if (string.IsNullOrWhiteSpace(name) || name == Path.GetFileName(old)) return;
        try
        {
            BatchRename.MoveAny(old, Path.Combine(Path.GetDirectoryName(old)!, name));
            Log.Info($"Renombrado: {old} → {name}");
            active.Reload(false);
        }
        catch (Exception ex) { await Dialogs.Error("No se pudo renombrar", ex.Message); }
    }

    void OnDelete(object s, RoutedEventArgs e) => Delete(Keyboard.Modifiers == ModifierKeys.Shift);

    async void Delete(bool permanent)
    {
        var sel = active.SelectedPaths();
        if (sel.Length == 0) return;
        string what = sel.Length == 1 ? $"«{Path.GetFileName(sel[0])}»" : $"{sel.Length} elementos";
        var vol = AppState.VolumeOf(sel[0]);
        bool noBin = vol != null && vol.Type != DriveType.Fixed;
        if (Settings.Current.ConfirmDelete || permanent || noBin)
        {
            string msg = permanent ? $"Se eliminará {what} DEFINITIVAMENTE, sin pasar por la Papelera."
                       : noBin ? $"¿Eliminar {what}?\n\nEn unidades extraíbles o de red Windows puede borrarlo definitivamente."
                       : $"¿Enviar {what} a la Papelera de reciclaje?";
            if (!await Dialogs.Confirm("Eliminar", msg, "Eliminar", danger: permanent || noBin)) return;
        }
        int ok = 0;
        await Task.Run(() =>
        {
            foreach (var p in sel)
            {
                try
                {
                    var opt = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
                    if (Directory.Exists(p)) FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, opt);
                    else FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, opt);
                    ok++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error($"No se pudo eliminar {p}: {ex.Message}"); }
            }
        });
        Log.Info($"Eliminados {ok} elemento(s) de {active.CurrentPath}" + (permanent ? " (definitivo)" : " (Papelera)"));
        active.Reload(false);
    }

    async void OnZip(object s, RoutedEventArgs e)
    {
        var sel = active.SelectedPaths();
        if (sel.Length == 0) return;
        string def = sel.Length == 1 ? Path.GetFileNameWithoutExtension(sel[0]) : Path.GetFileName(active.CurrentPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(def)) def = "Archivo";
        string name = await Dialogs.Prompt("Comprimir en ZIP", $"Se creará en {Other.CurrentPath}", def + ".zip", "Comprimir");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
        TransferQueue.Enqueue(Archive.ZipJob(sel, Path.Combine(Other.CurrentPath, name)));
    }

    void Extract()
    {
        foreach (var z in active.SelectedPaths().Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            TransferQueue.Enqueue(Archive.UnzipJob(z, Path.Combine(Other.CurrentPath, Path.GetFileNameWithoutExtension(z))));
    }

    void OnHash(object s, RoutedEventArgs e) => MainWindow.Instance.Navigate<ToolsView>("tools").Open("hash", null, active.SelectedPaths().Where(File.Exists).ToArray());
    void OnCompare(object s, RoutedEventArgs e) => MainWindow.Instance.Navigate<SyncView>("sync").Prepare(active.CurrentPath, Other.CurrentPath, true);
    void OnSearch(object s, RoutedEventArgs e) => MainWindow.Instance.Navigate<ToolsView>("tools").Open("search", active.CurrentPath);
    void OnSwap(object s, RoutedEventArgs e) { string a = Left.CurrentPath, b = Right.CurrentPath; Left.Navigate(b); Right.Navigate(a); }
    void OnSame(object s, RoutedEventArgs e) => Other.Navigate(active.CurrentPath);

    void OnHidden(object s, RoutedEventArgs e)
    {
        Settings.Current.ShowHidden = HiddenToggle.IsChecked == true;
        Settings.Save();
        Left.Reload(); Right.Reload();
    }

    async void CalcSize()
    {
        var sel = active.SelectedPaths().Where(Directory.Exists).ToArray();
        if (sel.Length == 0 && active.CurrentPath != null) sel = [active.CurrentPath];
        Toast.Show("Calculando tamaño…", string.Join(", ", sel.Select(Path.GetFileName)), Icons.Pie);
        var res = await Task.Run(() => sel.Select(p => (p, Archive.FolderSize(p, CancellationToken.None))).ToList());
        await Dialogs.Info("Tamaño", string.Join("\n", res.Select(r => $"{(Path.GetFileName(r.p) is { Length: > 0 } n ? n : r.p)}: {Fmt.Size(r.Item2.size)} · {r.Item2.files:N0} archivos · {r.Item2.dirs:N0} carpetas")));
    }

    async void QuickView()
    {
        var it = active.FocusedItem;
        if (it == null || it.IsDir) return;
        string ext = Path.GetExtension(it.FullPath).ToLowerInvariant();
        UIElement content;
        try
        {
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".ico" or ".webp")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(it.FullPath); bmp.DecodePixelWidth = 1200; bmp.EndInit();
                content = new Image { Source = bmp, MaxHeight = 520, MaxWidth = 580, Stretch = System.Windows.Media.Stretch.Uniform };
            }
            else if (it.Size < 20 * 1048576)
            {
                byte[] head = new byte[Math.Min(it.Size, 65536)];
                using (var fs = File.OpenRead(it.FullPath)) fs.ReadExactly(head);
                bool binary = head.Take(4096).Count(b => b == 0) > 4;
                string text = binary ? string.Join(" ", head.Take(1024).Select(b => b.ToString("X2"))) : System.Text.Encoding.UTF8.GetString(head);
                content = new TextBox { Text = text, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Height = 440, Width = 580, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }
            else content = Text("Archivo demasiado grande para la vista rápida.", "Caption");
        }
        catch (Exception ex) { content = Text(ex.Message, "Caption"); }
        await Dialogs.Show(it.Name, $"{Fmt.Size(it.Size)} · {it.Type} · {it.ModifiedText}", ["Abrir", "Cerrar"], 1, extra: content, glyph: Icons.Eye)
            .ContinueWith(t => { if (t.Result == 0) Dispatcher.Invoke(() => Native.Open(it.FullPath)); });
    }

    // ───────────── Menú contextual ─────────────
    ContextMenu BuildMenu(FilePane p)
    {
        var sel = p.SelectedPaths();
        bool any = sel.Length > 0;
        bool zip = sel.Any(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var items = new List<(string, string, Action)>();
        if (any)
        {
            items.Add(("Abrir", Icons.FolderOpen, () => p.OpenSelected()));
            if (sel.Length == 1 && File.Exists(sel[0])) items.Add(("Abrir con…", Icons.External, () => System.Diagnostics.Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {sel[0]}")));
            if (sel.Length == 1 && File.Exists(sel[0])) items.Add(("Vista rápida (F3)", Icons.Eye, QuickView));
            items.Add(("-", null, null));
            items.Add(("Copiar al otro panel (F5)", Icons.Copy, () => Transfer(false)));
            items.Add(("Mover al otro panel (F6)", Icons.Move, () => Transfer(true)));
            items.Add(("Copiar (Ctrl+C)", Icons.Copy, () => ToClipboard(false)));
            items.Add(("Cortar (Ctrl+X)", Icons.Move, () => ToClipboard(true)));
        }
        if (Clipboard.ContainsFileDropList()) items.Add(("Pegar (Ctrl+V)", Icons.Paste, Paste));
        if (any)
        {
            items.Add(("-", null, null));
            items.Add(("Renombrar (F2)", Icons.Rename, () => OnRename(null, null)));
            items.Add(("Renombrado masivo…", Icons.Rename, () => MainWindow.Instance.Navigate<ToolsView>("tools").Open("rename", null, sel)));
            items.Add(("Eliminar (Supr)", Icons.Delete, () => Delete(false)));
            items.Add(("Borrado seguro…", Icons.Wipe, () => MainWindow.Instance.Navigate<ToolsView>("tools").Open("wipe", null, sel)));
            items.Add(("-", null, null));
            items.Add(("Comprimir en ZIP…", Icons.Zip, () => OnZip(null, null)));
            if (zip) items.Add(("Extraer en el otro panel", Icons.Zip, Extract));
            items.Add(("Calcular hash…", Icons.Fingerprint, () => OnHash(null, null)));
        }
        items.Add(("Calcular tamaño", Icons.Pie, CalcSize));
        items.Add(("-", null, null));
        items.Add(("Nueva carpeta (F7)", Icons.NewFolder, () => OnNewFolder(null, null)));
        items.Add(("Analizar espacio aquí", Icons.Pie, () => MainWindow.Instance.Navigate<ToolsView>("tools").Open("space", p.CurrentPath)));
        items.Add(("Buscar duplicados aquí", Icons.Duplicate, () => MainWindow.Instance.Navigate<ToolsView>("tools").Open("dups", p.CurrentPath)));
        items.Add(("Comparar con el otro panel", Icons.Compare, () => OnCompare(null, null)));
        items.Add(("Clonar esta carpeta…", Icons.Clone, () => MainWindow.Instance.Navigate<CloneView>("clone").PrepareFiles(any && Directory.Exists(sel[0]) ? sel[0] : p.CurrentPath, Other.CurrentPath)));
        items.Add(("-", null, null));
        items.Add(("Añadir a favoritos", Icons.Star, () => { if (!Settings.Current.Favorites.Contains(p.CurrentPath)) { Settings.Current.Favorites.Add(p.CurrentPath); Settings.Save(); } }));
        items.Add(("Mostrar en el Explorador", Icons.External, () => Native.ShowInExplorer(any ? sel[0] : p.CurrentPath)));
        items.Add(("Abrir terminal aquí", Icons.Terminal, () => Native.OpenTerminal(p.CurrentPath)));
        items.Add(("Propiedades", Icons.Properties, () => Native.ShowProperties(any ? sel[0] : p.CurrentPath)));
        return Menu([.. items]);
    }
}
