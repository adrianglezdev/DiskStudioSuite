using System.Windows;
using System.Windows.Controls;
using DiskStudio.Core;

namespace DiskStudio.Views;

public partial class TransfersView : UserControl, ISnapshotDemo
{
    public async Task Demo(string action)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dss-demo");
        System.IO.Directory.CreateDirectory(dir + "/origen");
        var f = dir + "/origen/video-vacaciones.mp4";
        if (!System.IO.File.Exists(f)) { using var fs = System.IO.File.Create(f); fs.SetLength(400L << 20); }
        Settings.Current.SpeedLimitMBs = 60;
        TransferQueue.Enqueue(FileOps.CopyJob([dir + "/origen"], dir + "/destino", false, new CopyOptions { Conflict = ConflictPolicy.Overwrite }));
        TransferQueue.Enqueue(FolderSync.SyncJob("Copia de Documentos", dir + "/origen", dir + "/copia", SyncMode.Update, new CopyOptions()));
        await Task.Delay(3500);
        Settings.Current.SpeedLimitMBs = 0;
    }

    public TransfersView()
    {
        InitializeComponent();
        List.ItemsSource = TransferQueue.Jobs;
        LimitSlider.Value = Settings.Current.SpeedLimitMBs;
        UpdateLimit();
        AfterBox.SelectedIndex = (int)TransferQueue.After;
        TransferQueue.StateChanged += Update;
        TransferQueue.Jobs.CollectionChanged += (_, _) => Update();
        Update();
    }

    void Update()
    {
        Empty.Visibility = TransferQueue.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int active = TransferQueue.PendingCount;
        Subtitle.Text = active > 0 ? $"{active} trabajo(s) pendientes · se ejecutan de uno en uno" : "Cola de copias, movimientos, clonados e imágenes";
        if (AfterBox.SelectedIndex != (int)TransferQueue.After) AfterBox.SelectedIndex = (int)TransferQueue.After;
    }

    static TransferJob J(object s) => (TransferJob)((FrameworkElement)s).DataContext;
    void OnPause(object s, RoutedEventArgs e) => J(s).Pause();
    void OnResume(object s, RoutedEventArgs e) => J(s).Resume();
    void OnCancel(object s, RoutedEventArgs e) => J(s).Cancel();
    void OnRemove(object s, RoutedEventArgs e) => TransferQueue.Jobs.Remove(J(s));
    void OnClear(object s, RoutedEventArgs e) => TransferQueue.ClearFinished();
    void OnPauseAll(object s, RoutedEventArgs e) { foreach (var j in TransferQueue.Jobs) j.Pause(); }
    void OnResumeAll(object s, RoutedEventArgs e) { foreach (var j in TransferQueue.Jobs) j.Resume(); }
    void OnGoExplorer(object s, RoutedEventArgs e) => MainWindow.Instance.Navigate("explorer");

    void OnRetry(object s, RoutedEventArgs e)
    {
        var j = J(s);
        var failed = j.Errors.Select(x => x.Path).Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).Distinct().ToList();
        if (failed.Count == 0 || j.RetryFactory == null) return;
        TransferQueue.Enqueue(j.RetryFactory(failed));
    }

    void OnLimit(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded && e.OldValue == 0 && e.NewValue == Settings.Current.SpeedLimitMBs) { UpdateLimit(); return; }
        Settings.Current.SpeedLimitMBs = (int)LimitSlider.Value;
        Settings.Save();
        UpdateLimit();
    }

    void UpdateLimit() { if (LimitText != null) LimitText.Text = LimitSlider.Value == 0 ? "Sin límite" : $"{LimitSlider.Value:0} MB/s"; }

    void OnAfter(object s, SelectionChangedEventArgs e)
    {
        if (AfterBox.SelectedIndex >= 0 && (int)TransferQueue.After != AfterBox.SelectedIndex)
            TransferQueue.After = (AfterQueue)AfterBox.SelectedIndex;
    }
}
