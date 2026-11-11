using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Diálogos de actualización: novedades, descarga con progreso e instalación.</summary>
public static class UpdateUi
{
    static bool busy, waitingQueue;

    /// <summary>Ofrece la actualización. <paramref name="manual"/>: el usuario la ha buscado (se muestra aunque la omitiera antes).</summary>
    public static async Task Offer(UpdateInfo u, bool manual)
    {
        if (u == null || busy) return;
        if (!manual && string.Equals(Settings.Current.SkippedVersion, u.Tag, StringComparison.OrdinalIgnoreCase)) return;
        var kind = Updater.Kind;
        var asset = Updater.AssetFor(u, kind);

        var sp = new StackPanel { MaxWidth = 560 };
        sp.Children.Add(Row(Chip(L.F("Tienes la {0}", Updates.Current.ToString(3))), Text("  →  ", "Caption", 14),
            Chip(L.F("Nueva: {0}", u.Tag) + (u.Prerelease ? " · " + L.T("en pruebas") : ""), Brushes2.Good)).Also(r => r.Margin = new Thickness(0, 0, 0, 12)));
        var notes = new StackPanel();
        foreach (var (level, text) in Updater.NotesLines(u.Notes))
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = level switch { 1 => 15, 2 => 14, _ => 13 }, Margin = new Thickness(0, level > 0 ? 10 : 2, 0, 2), FontWeight = level > 0 ? FontWeights.SemiBold : FontWeights.Normal };
            tb.SetResourceReference(TextBlock.ForegroundProperty, level > 0 ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            notes.Children.Add(tb);
        }
        if (notes.Children.Count == 0) notes.Children.Add(Text("Esta versión no incluye notas.", "Caption", 13));
        sp.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        string how = asset == null ? "Esta versión no tiene un archivo para actualizar desde la app: se abrirá su página de descarga."
            : kind == InstallKind.Installed && asset.Name != Updater.PortableName ? $"Se descargará el instalador ({Fmt.Size(asset.Size)}), se comprobará su SHA-256 y Disk Studio Suite se reiniciará actualizado."
            : $"Se descargará el nuevo ejecutable portable ({Fmt.Size(asset.Size)}), se comprobará su SHA-256 y sustituirá al actual. Disk Studio Suite se reiniciará actualizado.";
        sp.Children.Add(Text(how, "Caption", 12, margin: new Thickness(0, 12, 0, 0)).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));

        int r = await Dialogs.Show("Hay una versión nueva de Disk Studio Suite", null, ["Actualizar ahora", "Más tarde", "Omitir esta versión"], 0, extra: sp, glyph: Icons.Update, cancel: 1);
        if (r == 2) { Settings.Current.SkippedVersion = u.Tag; Settings.Save(); Log.Info($"Versión {u.Tag} omitida"); return; }
        if (r != 0) return;
        if (asset == null) { Native.Open(u.Url); return; }
        await Install(u, asset, kind, ask: false);
    }

    /// <summary>Descarga, comprueba e instala. <paramref name="ask"/>: pedir confirmación antes de reiniciar.</summary>
    public static async Task Install(UpdateInfo u, ReleaseAsset asset, InstallKind kind, bool ask)
    {
        if (busy) return;
        if (TransferQueue.PendingCount > 0)
        {
            if (waitingQueue) return;
            if (!await Dialogs.Confirm("Hay operaciones en curso", "Actualizar cierra Disk Studio Suite. ¿Quieres que se actualice en cuanto termine la cola?", "Actualizar al terminar")) return;
            waitingQueue = true;
            void wait()
            {
                if (TransferQueue.PendingCount > 0) return;
                TransferQueue.StateChanged -= wait;
                waitingQueue = false;
                _ = Install(u, asset, kind, ask);
            }
            TransferQueue.StateChanged += wait;
            Toast.Show("Actualización pendiente", "Se instalará cuando termine la cola.", Icons.Update);
            return;
        }
        if (kind == InstallKind.Portable && !Updater.CanWrite(Updater.ExeDir))
        {
            await Dialogs.Error("No se puede actualizar aquí", $"Disk Studio Suite no puede escribir en {Updater.ExeDir}. Descarga la versión nueva desde su página o mueve el programa a otra carpeta.");
            Native.Open(u.Url);
            return;
        }
        busy = true;
        try
        {
            var bar = new ProgressBar { Maximum = 1, Height = 6, MinWidth = 420, Margin = new Thickness(0, 4, 0, 8) };
            var info = Text("Conectando…", "Caption", 12);
            var panel = Col(Text(L.F("Descargando {0}", asset.Name), size: 13), bar, info);
            using var cts = new CancellationTokenSource();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<(long done, long total)>(p =>
            {
                bar.Value = p.total > 0 ? (double)p.done / p.total : 0;
                bar.IsIndeterminate = p.total <= 0;
                double speed = p.done / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                info.Text = L.F("{0} de {1}", Fmt.Size(p.done), Fmt.Size(p.total)) + " · " + Fmt.Speed(speed);
            });
            string file = null; Exception error = null;
            Task download = null;
            // La descarga empieza cuando el diálogo ya se ve, para poder cerrarlo al terminar
            panel.Loaded += (_, _) => download ??= Task.Run(async () =>
            {
                try { file = await Updater.Download(u, asset, progress, cts.Token); }
                catch (Exception ex) { error = ex; }
                finally { await Application.Current.Dispatcher.InvokeAsync(() => MainWindow.Instance.CloseDialogFromCode(99)); }
            });
            int r = await Dialogs.Show($"Actualizando a {u.Tag}", null, ["Cancelar"], 0, extra: panel, glyph: Icons.Download, cancel: 0);
            if (r != 99) { cts.Cancel(); if (download != null) await download; Log.Info("Actualización cancelada"); return; }
            await download;
            if (error is OperationCanceledException) return;
            if (error != null) { Log.Error("Actualización: " + error.Message); await Dialogs.Error("No se pudo actualizar", error.Message); return; }
            if (ask && !await Dialogs.Confirm("Actualización lista", "Disk Studio Suite se cerrará unos segundos y se abrirá ya actualizado.", "Reiniciar y actualizar")) return;
            Settings.Save();
            AutoTasks.Save();
            try { Updater.Apply(file, kind); }
            catch (Exception ex) { Log.Error("Actualización: " + ex.Message); await Dialogs.Error("No se pudo instalar la actualización", ex.Message); return; }
            Log.Ok($"Instalando {u.Tag}: Disk Studio Suite se reinicia");
            MainWindow.Instance.ExitApp();
        }
        finally { busy = false; }
    }

    /// <summary>Al encontrar una versión nueva al iniciar: instalarla sola (si se ha elegido) o preguntar.</summary>
    public static async Task OnFound(UpdateInfo u)
    {
        if (string.Equals(Settings.Current.SkippedVersion, u.Tag, StringComparison.OrdinalIgnoreCase)) return;
        var kind = Updater.Kind;
        var asset = Updater.AssetFor(u, kind);
        bool auto = Settings.Current.AutoInstallUpdates || Environment.GetEnvironmentVariable("DSS_UPDATE_AUTO") == "1";
        if (auto && asset != null && TransferQueue.PendingCount == 0 && MainWindow.Instance.IsVisible)
        {
            Toast.Show("Actualizando", L.F("Instalando Disk Studio Suite {0}…", u.Tag), Icons.Update);
            await Install(u, asset, kind, ask: false);
            return;
        }
        if (!MainWindow.Instance.IsVisible)
        {
            Toast.Show("Hay una versión nueva", $"Disk Studio Suite {u.Tag} está disponible. Abre la app para actualizar.", Icons.Update);
            return;
        }
        await Offer(u, manual: false);
    }
}
