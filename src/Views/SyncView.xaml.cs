using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DiskStudio.Core;

namespace DiskStudio.Views;

public partial class SyncView : UserControl, ISnapshotDemo
{
    public async Task Demo(string action)
    {
        string d = Path.Combine(Path.GetTempPath(), "dss-demo-sync");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        void w(string rel, string txt, int min = 0) { var p = Path.Combine(d, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, txt); File.SetLastWriteTime(p, DateTime.Now.AddMinutes(min)); }
        w("Fotos/2026/playa.jpg", new string('a', 40000)); w("Copia/2026/playa.jpg", new string('a', 40000));
        w("Fotos/2026/cena.jpg", "nuevo"); w("Fotos/informe.docx", "v2", 30); w("Copia/informe.docx", "v1");
        w("Copia/antiguo.txt", "x"); w("Fotos/Viaje/mapa.png", "m"); w("Fotos/notas.txt", "n"); w("Copia/notas.txt", "n");
        File.SetLastWriteTime(Path.Combine(d, "Copia/notas.txt"), File.GetLastWriteTime(Path.Combine(d, "Fotos/notas.txt")));
        ModeMirror.IsChecked = true;
        Prepare(Path.Combine(d, "Fotos"), Path.Combine(d, "Copia"), true);
        await Task.Delay(1500);
    }

    List<CompareItem> items = [];
    ICollectionView view;
    CancellationTokenSource cts;
    string comparedLeft, comparedRight;

    public SyncView()
    {
        InitializeComponent();
        Results.Visibility = Visibility.Collapsed;
    }

    SyncMode Mode => ModeMirror.IsChecked == true ? SyncMode.Mirror : ModeTwoWay.IsChecked == true ? SyncMode.TwoWay : SyncMode.Update;
    FileFilter Filter => new() { Include = IncludeBox.Text, Exclude = ExcludeBox.Text, IncludeHidden = HiddenBox.IsChecked == true };

    public void Prepare(string left, string right, bool run)
    {
        LeftBox.Text = left; RightBox.Text = right;
        if (run) OnCompare(null, null);
    }

    void OnPickLeft(object s, RoutedEventArgs e) { var p = Dialogs.PickFolder(LeftBox.Text, "Carpeta de origen"); if (p != null) LeftBox.Text = p; }
    void OnPickRight(object s, RoutedEventArgs e) { var p = Dialogs.PickFolder(RightBox.Text, "Carpeta de destino"); if (p != null) RightBox.Text = p; }
    void OnSwap(object s, RoutedEventArgs e) => (LeftBox.Text, RightBox.Text) = (RightBox.Text, LeftBox.Text);

    void OnMode(object s, RoutedEventArgs e)
    {
        if (items.Count == 0) return;
        FolderSync.Plan(items, Mode);
        UpdatePlan();
    }

    async void OnCompare(object s, RoutedEventArgs e)
    {
        if (cts != null) { cts.Cancel(); return; }
        string l = LeftBox.Text.Trim(), r = RightBox.Text.Trim();
        if (!Directory.Exists(l)) { await Dialogs.Error("Origen no válido", "La carpeta de origen no existe."); return; }
        if (string.IsNullOrEmpty(r)) { await Dialogs.Error("Destino no válido", "Indica la carpeta de destino."); return; }
        if (FileOps.IsInside(r, l) || FileOps.IsInside(l, r)) { await Dialogs.Error("Carpetas anidadas", "Una carpeta no puede estar dentro de la otra."); return; }

        cts = new CancellationTokenSource();
        CompareBtn.Content = "Detener";
        Results.Visibility = Visibility.Collapsed;
        ApplyBar.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        Busy.Visibility = Visibility.Visible;
        var filter = Filter; bool content = ByContent.IsChecked == true; var tk = cts.Token;
        try
        {
            items = await Task.Run(() => FolderSync.Compare(l, r, filter, content, tk, p => Dispatcher.BeginInvoke(() => PlaceholderText.Text = "Comparando " + p)));
            comparedLeft = l; comparedRight = r;
            FolderSync.Plan(items, Mode);
            view = CollectionViewSource.GetDefaultView(items);
            view.Filter = o => ShowSame.IsChecked == true || ((CompareItem)o).Status != DiffStatus.Same;
            Results.ItemsSource = view;
            foreach (var i in items) i.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(CompareItem.Include)) UpdatePlan(); };
            Results.Visibility = Visibility.Visible;
            Placeholder.Visibility = items.Any(i => i.Status != DiffStatus.Same) || ShowSame.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            PlaceholderText.Text = items.Count == 0 ? "Las carpetas están vacías" : "Las dos carpetas son idénticas ✓";
            ApplyBar.Visibility = Visibility.Visible;
            UpdatePlan();
            Log.Info($"Comparación {l} ↔ {r}: {items.Count(i => i.Status != DiffStatus.Same)} diferencias");
        }
        catch (OperationCanceledException) { PlaceholderText.Text = "Comparación cancelada"; }
        catch (Exception ex) { PlaceholderText.Text = "Error: " + ex.Message; }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            CompareBtn.Content = "Comparar";
            cts = null;
        }
    }

    void OnShowSame(object s, RoutedEventArgs e)
    {
        view?.Refresh();
        if (Results.Visibility == Visibility.Visible) Placeholder.Visibility = view != null && !view.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdatePlan()
    {
        var act = items.Where(i => i.Include && i.Action != SyncAction.None).ToList();
        int copies = act.Count(i => i.Action is SyncAction.ToRight or SyncAction.ToLeft);
        int deletes = act.Count(i => i.Action is SyncAction.DeleteRight or SyncAction.DeleteLeft);
        long bytes = act.Where(i => i.Action == SyncAction.ToRight).Sum(i => Math.Max(0, i.LeftSize)) + act.Where(i => i.Action == SyncAction.ToLeft).Sum(i => Math.Max(0, i.RightSize));
        int same = items.Count(i => i.Status == DiffStatus.Same);
        PlanText.Text = act.Count == 0 ? $"Nada que sincronizar · {same} elementos idénticos"
            : $"{copies} copias ({Fmt.Size(bytes)})" + (deletes > 0 ? $" · {deletes} borrados" : "") + $" · {same} idénticos";
        ApplyBtn.IsEnabled = act.Count > 0;
        view?.Refresh();
    }

    async void OnApply(object s, RoutedEventArgs e)
    {
        var ops = FolderSync.ToOps(items, comparedLeft, comparedRight);
        int deletes = ops.Count(o => o.Kind is OpKind.Delete or OpKind.DeleteDir);
        if (deletes > 0 && !await Dialogs.Confirm("Confirmar borrados", $"La sincronización eliminará {deletes} elemento(s) en el destino. ¿Continuar?", "Sincronizar", danger: true)) return;
        var opt = new CopyOptions { Verify = VerifyBox.IsChecked == true, Filter = Filter };
        TransferQueue.Enqueue(FolderSync.OpsJob($"Sincronizar ({ModeName})", $"{comparedLeft} → {comparedRight}", ops, opt));
        Toast.Show("Sincronización en cola", $"{ops.Count} operaciones", Icons.Sync);
        items.Clear(); Results.ItemsSource = null; Results.Visibility = Visibility.Collapsed; ApplyBar.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible; PlaceholderText.Text = "Sincronización enviada a Transferencias";
    }

    string ModeName => Mode switch { SyncMode.Mirror => "espejo", SyncMode.TwoWay => "bidireccional", _ => "actualizar" };

    async void OnSaveTask(object s, RoutedEventArgs e)
    {
        if (!Directory.Exists(LeftBox.Text) || string.IsNullOrWhiteSpace(RightBox.Text)) { await Dialogs.Error("Faltan carpetas", "Indica origen y destino."); return; }
        string name = await Dialogs.Prompt("Guardar como tarea", "Nombre de la tarea:", $"Sincronizar {Path.GetFileName(LeftBox.Text.TrimEnd('\\'))}", "Guardar");
        if (string.IsNullOrWhiteSpace(name)) return;
        var t = new AutoTask
        {
            Name = name, Source = LeftBox.Text, Destination = RightBox.Text,
            Kind = Mode switch { SyncMode.Mirror => TaskKind.Mirror, SyncMode.TwoWay => TaskKind.TwoWay, _ => TaskKind.Update },
            Include = IncludeBox.Text, Exclude = ExcludeBox.Text, Verify = VerifyBox.IsChecked == true
        };
        AutoTasks.Items.Add(t); AutoTasks.Save();
        Toast.Show("Tarea guardada", $"«{name}» en Tareas automáticas", Icons.Tasks);
        MainWindow.Instance.Navigate<TasksView>("tasks").Edit(t);
    }
}
