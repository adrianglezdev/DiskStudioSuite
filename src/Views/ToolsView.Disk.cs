using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Herramientas de unidad: capacidad real, bloqueos, reparación y BitLocker.</summary>
public partial class ToolsView
{
    static TextBlock Wrap(string text, string style = "Caption", double size = 13) =>
        Text(text, style, size).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; });

    ComboBox VolumeCombo(Func<VolumeInfo, bool> filter, out Action refill)
    {
        var cb = new ComboBox { MinWidth = 320 };
        void fill()
        {
            var prev = cb.SelectedItem as VolumeInfo;
            var list = AppState.Volumes.Where(v => v.Ready && filter(v)).ToList();
            cb.ItemsSource = list;
            cb.SelectedItem = list.Find(v => v.Root == (prev?.Root ?? presetPath) || (presetPath != null && v.Root == Path.GetPathRoot(presetPath))) ?? list.FirstOrDefault();
        }
        refill = fill;
        fill();
        return cb;
    }

    static UIElement Notice(string glyph, string text, Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14),
            Background = new SolidColorBrush(Color.FromArgb(26, c.R, c.G, c.B)),
            Child = Columns((Icon(glyph, 16, brush, new Thickness(0, 0, 12, 0)), Auto), (Wrap(text, null, 13).Also(t => t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush")), Star))
        };
    }

    static UIElement AdminRow(string what)
    {
        if (Native.IsAdmin) return null;
        return Col(Notice(Icons.Admin, $"{what} requiere ejecutar Disk Studio Suite como administrador.", Brushes2.Caution),
            Btn("Reiniciar como administrador", Icons.Admin, (_, _) => Native.RestartAsAdmin(), "SecondaryButton").Also(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 0, 0, 14); }));
    }

    // ═════════════ Capacidad real ═════════════
    static readonly (string, string)[] CapacityInfo =
    [
        ("¿Qué es un pendrive falso?", "Algunos pendrives y tarjetas baratos se venden con una capacidad que no tienen: Windows ve, por ejemplo, 1 TB, pero solo hay 32 GB de memoria real. Cuando lo llenas, lo que pasa de la capacidad real se pierde o sobrescribe lo anterior sin avisar."),
        ("Cómo se comprueba", "Disk Studio Suite llena el espacio libre con archivos de prueba que contienen un patrón único para cada megabyte, y después los vuelve a leer directamente del dispositivo, sin la caché de Windows. Si algún bloque no coincide, la memoria no es fiable a partir de ese punto."),
        ("¿Se borran mis archivos?", "No. Solo se usa el espacio libre y los archivos de prueba (carpeta «DSS-Comprobacion») se eliminan al terminar. Para comprobar el dispositivo entero, primero cópialos a otro sitio y formatéalo."),
        ("Cuánto tarda", "Hay que escribir y leer todo el espacio libre: en un pendrive USB 2 de 32 GB puede llevar más de una hora. La prueba también sirve para medir la velocidad real sostenida."),
    ];

    FrameworkElement CapacityTool()
    {
        var all = new CheckBox { Content = "Mostrar también discos internos", Margin = new Thickness(0, 8, 0, 0) };
        var vol = VolumeCombo(v => !v.IsSystem && (v.IsRemovable || all.IsChecked == true), out var refill);
        all.Click += (_, _) => refill();
        var keep = new CheckBox { Content = "Borrar los archivos de prueba al terminar", IsChecked = true, Margin = new Thickness(0, 12, 0, 14) };
        var info = Text("", "Caption", 13, margin: new Thickness(0, 8, 0, 0));
        var results = new StackPanel();
        void upd() => info.Text = vol.SelectedItem is VolumeInfo v ? $"Se probarán {Fmt.Size(v.Free)} libres de {Fmt.Size(v.Total)} · {v.FileSystem}" + (string.IsNullOrEmpty(v.DiskModel) ? "" : " · " + v.DiskModel) : "Conecta un pendrive o una tarjeta de memoria.";
        vol.SelectionChanged += (_, _) => upd();
        upd();

        void show(VolumeInfo v, CapacityResult r)
        {
            var brush = r.Passed ? Brushes2.Good : Brushes2.Bad;
            results.Children.Insert(0, Card(Col(
                Row(Icon(r.Passed ? Icons.Success : Icons.Error, 22, brush, new Thickness(0, 0, 12, 0)), Text(r.Passed ? $"{v.Letter} {v.Name}: capacidad real verificada" : $"{v.Letter} {v.Name}: el dispositivo NO es fiable", "CardTitle", 16)),
                Wrap(r.Verdict, null, 13).Also(t => { t.Margin = new Thickness(0, 10, 0, 10); t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush"); }),
                new UsageBar { Value = r.Tested > 0 ? (double)r.Ok / r.Tested : 0, Fill = brush, Height = 8 },
                Text($"Correctos {Fmt.Size(r.Ok)} · con errores {Fmt.Size(r.Corrupt)} · escritura {Fmt.Speed(r.WriteSpeed)} · lectura {Fmt.Speed(r.ReadSpeed)}", "Caption", 12, margin: new Thickness(0, 8, 0, 0)),
                r.Passed ? null : Wrap("Consejo: no guardes datos importantes en él. Si lo compraste hace poco, pide la devolución. Si quieres seguir usándolo, reinícialo con una partición que no pase de la capacidad fiable.", "Caption", 12).Also(t => t.Margin = new Thickness(0, 8, 0, 0))
            ), new Thickness(0, 14, 0, 0)));
        }

        var go = Btn("Iniciar comprobación", Icons.Play, async (_, _) =>
        {
            if (vol.SelectedItem is not VolumeInfo v) return;
            if (v.Free < 64L << 20) { await Dialogs.Error("Sin espacio libre", "La unidad está casi llena: libera espacio o formatéala antes de comprobarla."); return; }
            if (!await Dialogs.Confirm("Comprobar capacidad real", $"Se escribirán {Fmt.Size(v.Free)} de datos de prueba en {v.Letter} y se volverán a leer. Tus archivos no se tocan, pero la unidad estará llena mientras dure la prueba.", "Comprobar")) return;
            TransferQueue.Enqueue(CapacityCheck.Job(v, keep.IsChecked == true, r => show(v, r)));
            Toast.Show("Comprobación en cola", $"{v.Letter} {v.Name}", Icons.Shield);
        }, "PrimaryButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);

        var top = Card(Col(
            Row(Text("Unidad", "Label"), InfoBtn("Comprobar la capacidad real", CapacityInfo)),
            vol, all, info, keep, go), new Thickness(0), 20).Also(c => c.MaxWidth = 760).Also(c => c.HorizontalAlignment = HorizontalAlignment.Left);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Col(top, results.Also(r => r.MaxWidth = 760).Also(r => r.HorizontalAlignment = HorizontalAlignment.Left)) };
    }

    // ═════════════ ¿Por qué no puedo expulsarlo? ═════════════
    FrameworkElement LocksTool()
    {
        var vol = VolumeCombo(v => !v.IsSystem, out _);
        var list = new StackPanel();
        var status = Text("Elige una unidad y pulsa «Buscar».", "Caption", 13, margin: new Thickness(0, 12, 0, 0));
        Button go = null, eject = null;

        async void search()
        {
            if (vol.SelectedItem is not VolumeInfo v) return;
            go.IsEnabled = false; status.Text = "Buscando programas que usan la unidad…"; list.Children.Clear();
            var found = await Task.Run(() => LockFinder.Find(v.Letter));
            go.IsEnabled = true;
            eject.Visibility = v.IsRemovable ? Visibility.Visible : Visibility.Collapsed;
            if (found.Count == 0)
            {
                status.Text = $"Ningún programa tiene archivos abiertos en {v.Letter}." + (Native.IsAdmin ? "" : " (Como administrador también se revisan los procesos del sistema.)");
                return;
            }
            status.Text = $"{found.Count} programa(s) usan {v.Letter}." + (Native.IsAdmin ? "" : " Como administrador se ven también los procesos del sistema.");
            foreach (var l in found) list.Children.Add(LockerCard(l, search));
        }
        go = Btn("Buscar", Icons.Search, (_, _) => search(), "PrimaryButton");
        eject = Btn("Expulsar", Icons.Eject, (_, _) => { if (vol.SelectedItem is VolumeInfo v) DiskActions.Eject(v); }, "SecondaryButton").Also(b => { b.Visibility = Visibility.Collapsed; b.Margin = new Thickness(8, 0, 0, 0); });
        vol.SelectionChanged += (_, _) => { list.Children.Clear(); status.Text = ""; };
        var top = Card(Col(Text("Unidad", "Label"), Row(vol, go.Also(g => g.Margin = new Thickness(12, 0, 0, 0)), eject), status), new Thickness(0), 20);
        if (presetPath != null) Dispatcher.BeginInvoke(search);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Col(top, list.Also(l => l.Margin = new Thickness(0, 14, 0, 0))).Also(c => { c.MaxWidth = 900; c.HorizontalAlignment = HorizontalAlignment.Left; }) };
    }

    internal static UIElement LockerCard(Locker l, Action refresh)
    {
        var title = Row(Text(string.IsNullOrEmpty(l.Name) ? "Proceso " + l.Pid : l.Name, "CardTitle", 15), Text($"  PID {l.Pid}", "Caption", 12),
            string.IsNullOrEmpty(l.Title) ? null : Text("  · " + l.Title, "Caption", 12));
        var actions = Row(
            l.IsSelf ? null : Btn("Cerrar", Icons.Close, async (_, _) =>
            {
                if (!LockFinder.Close(l, false))
                {
                    if (!await Dialogs.Confirm("El programa no se ha cerrado", $"«{l.Name}» no ha respondido. ¿Forzar el cierre? Se perderá lo que no esté guardado en él.", "Forzar cierre", true)) return;
                    if (!LockFinder.Close(l, true)) { await Dialogs.Error("No se pudo cerrar", Native.IsAdmin ? "Windows no permite terminar este proceso." : "Puede que necesites ejecutar Disk Studio Suite como administrador."); return; }
                }
                Log.Info($"Programa cerrado: {l.Name} ({l.Pid})");
                refresh();
            }, "SecondaryButton"));
        var body = Col(title, Wrap(l.FilesText, "Caption", 12).Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
        return Card(Columns((Tile(Icons.Terminal, null, 36), Auto), (body.Also(b => b.Margin = new Thickness(12, 0, 12, 0)), Star), (actions, Auto)), new Thickness(0, 0, 0, 8), 14);
    }

    // ═════════════ Comprobar y reparar ═════════════
    static readonly (string, string)[] RepairInfo =
    [
        ("Comprobar", "Busca errores en el sistema de archivos sin cambiar nada. En NTFS se hace con la unidad en uso (chkdsk /scan), así que puedes seguir trabajando."),
        ("Reparar", "Corrige los errores encontrados (chkdsk /f). La unidad se desmonta mientras dura: cierra antes los archivos abiertos en ella. En la unidad del sistema, la reparación se programa para el próximo reinicio de Windows."),
        ("Optimizar", "Hace lo adecuado según el tipo de disco: en SSD envía TRIM para que el disco sepa qué espacio está libre (más velocidad y menos desgaste); en discos duros, desfragmenta. No hace falta en pendrives."),
    ];

    FrameworkElement RepairTool()
    {
        var vol = VolumeCombo(v => v.Type is DriveType.Fixed or DriveType.Removable && !string.IsNullOrEmpty(v.FileSystem), out _);
        var info = Text("", "Caption", 13, margin: new Thickness(0, 8, 0, 16));
        void upd()
        {
            if (vol.SelectedItem is not VolumeInfo v) { info.Text = ""; return; }
            var d = AppState.Disks.Find(x => x.Number == v.DiskNumber);
            info.Text = $"{v.FileSystem} · {Fmt.Size(v.Total - v.Free)} usados de {Fmt.Size(v.Total)}" + (d != null ? $" · {d.Title} ({(string.IsNullOrEmpty(d.Media) ? d.Bus : d.Media)})" : "") + (v.IsSystem ? " · unidad del sistema" : "");
        }
        vol.SelectionChanged += (_, _) => upd();
        upd();

        Button tile(string glyph, string title, string desc, RepairMode mode)
        {
            var b = new Button { Style = S("ActionTile"), Width = 250, Margin = new Thickness(0, 0, 12, 12) };
            b.Content = Col(Tile(glyph, null, 40).Also(x => x.HorizontalAlignment = HorizontalAlignment.Left), Text(title, "CardTitle", 15, margin: new Thickness(0, 12, 0, 4)),
                Wrap(desc, "Caption", 12).Also(t => t.Height = 50));
            b.Click += async (_, _) =>
            {
                if (vol.SelectedItem is not VolumeInfo v) return;
                if (!Native.IsAdmin) { if (await Dialogs.Confirm("Se necesita administrador", "Esta acción requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin(); return; }
                if (mode == RepairMode.Fix)
                {
                    string msg = v.IsSystem
                        ? $"{v.Letter} es la unidad de Windows: no se puede reparar mientras se usa. Se marcará para que Windows la compruebe y repare en el próximo reinicio."
                        : $"Se desmontará {v.Letter} mientras se repara: los programas que tengan archivos abiertos en ella los perderán. Guarda tu trabajo antes de continuar.";
                    if (!await Dialogs.Confirm(v.IsSystem ? "Reparar al reiniciar" : $"Reparar {v.Letter}", msg, v.IsSystem ? "Programar" : "Reparar")) return;
                }
                TransferQueue.Enqueue(VolumeRepair.Job(v, mode));
                Toast.Show("En cola", $"{title} {v.Letter}", glyph);
            };
            return b;
        }
        var tiles = new WrapPanel();
        tiles.Children.Add(tile(Icons.Search, "Comprobar", "Busca errores sin cambiar nada.", RepairMode.Scan));
        tiles.Children.Add(tile(Icons.Wrench, "Reparar", "Corrige los errores del sistema de archivos.", RepairMode.Fix));
        tiles.Children.Add(tile(Icons.Speed, "Optimizar", "TRIM en SSD, desfragmentar en discos duros.", RepairMode.Optimize));
        var card = Card(Col(AdminRow("Comprobar y reparar"), Row(Text("Unidad", "Label"), InfoBtn("Comprobar, reparar y optimizar", RepairInfo)), vol, info, tiles,
            Wrap("El progreso y el resultado aparecen en Transferencias y en el Registro.", "Caption", 12)), new Thickness(0), 20);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = card.Also(c => { c.MaxWidth = 860; c.HorizontalAlignment = HorizontalAlignment.Left; c.VerticalAlignment = VerticalAlignment.Top; }) };
    }

    // ═════════════ BitLocker ═════════════
    static readonly (string, string)[] BitLockerHelp =
    [
        ("¿Qué es BitLocker?", "Es el cifrado de discos de Windows. Una unidad cifrada solo se puede leer después de desbloquearla con su contraseña, su clave de recuperación (48 cifras) o automáticamente con el chip TPM del equipo."),
        ("Suspender la protección", "Los datos siguen cifrados, pero la clave queda accesible para que Windows arranque sin pedirla. Hazlo antes de actualizar la BIOS o de clonar el disco del sistema, y reanúdala después."),
        ("Clonar un disco cifrado", "Al clonar o crear una imagen de una unidad cifrada se copian los datos cifrados. Desbloquéala antes de empezar: si está bloqueada no se puede leer su contenido. Guarda siempre tu clave de recuperación."),
    ];

    FrameworkElement BitLockerTool()
    {
        var list = new StackPanel();
        var status = Text("", "Caption", 13, margin: new Thickness(0, 0, 0, 12));
        async void load()
        {
            list.Children.Clear();
            status.Text = "Consultando el estado de BitLocker…";
            var vols = AppState.Volumes.Where(v => v.Type is DriveType.Fixed or DriveType.Removable).ToList();
            var infos = await Task.Run(() => vols.Select(v => (v, BitLocker.Get(v.Letter))).ToList());
            status.Text = Native.IsAdmin ? $"{infos.Count(i => i.Item2.Encrypted)} de {infos.Count} unidades cifradas." : "Sin administrador solo se muestra el estado básico y no se puede cambiar.";
            foreach (var (v, b) in infos) list.Children.Add(BitLockerRow(v, b, load));
        }
        var head = Columns((Col(Row(Text("Unidades", "SectionTitle"), InfoBtn("BitLocker", BitLockerHelp)), status), Star),
            (Row(Btn("Actualizar", Icons.Refresh, (_, _) => load(), "SecondaryButton"),
                 Btn("Panel de Windows", Icons.External, (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "/name Microsoft.BitLockerDriveEncryption") { UseShellExecute = true }); } catch { } }, "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0))).Also(r => r.VerticalAlignment = VerticalAlignment.Top), Auto));
        load();
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Col(AdminRow("Gestionar BitLocker"), head, list).Also(c => { c.MaxWidth = 900; c.HorizontalAlignment = HorizontalAlignment.Left; }) };
    }

    static UIElement BitLockerRow(VolumeInfo v, BitLockerInfo b, Action reload)
    {
        Brush color = b.Locked ? Brushes2.Caution : b.Encrypted && !b.Suspended ? Brushes2.Good : b.Suspended ? Brushes2.Caution : Brushes2.Text2;
        var info = Col(Row(Text($"{v.Letter}  {v.Name}", "CardTitle", 15), Chip(b.StatusText, color).Also(c => c.Margin = new Thickness(10, 0, 0, 0))),
            Text((v.Ready ? $"{v.FileSystem} · {Fmt.Size(v.Total)}" : "Sin acceso al contenido") + (b.Method.Length > 0 ? " · " + b.Method : "") + (v.IsSystem ? " · unidad del sistema" : ""), "Caption", 12, margin: new Thickness(0, 4, 0, 0)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button act(string text, string glyph, Func<Task> a) => Btn(text, glyph, async (_, _) =>
        {
            if (!Native.IsAdmin) { if (await Dialogs.Confirm("Se necesita administrador", "Cambiar BitLocker requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin(); return; }
            try { await a(); } catch (Exception ex) { await Dialogs.Error("BitLocker", ex.Message); }
            reload();
            AppState.Rescan(false);
        }, "SecondaryButton").Also(x => x.Margin = new Thickness(8, 0, 0, 0));
        if (b.Locked)
            actions.Children.Add(act("Desbloquear", Icons.Lock, async () =>
            {
                var pb = new PasswordBox { MinWidth = 380 };
                var panel = Col(Text("Contraseña o clave de recuperación (48 cifras)", "Label"), pb);
                if (await Dialogs.Show($"Desbloquear {v.Letter}", null, ["Desbloquear", "Cancelar"], 0, extra: panel, glyph: Icons.Lock) != 0) return;
                string secret = pb.Password; pb.Clear();
                string err = await Task.Run(() => BitLocker.Unlock(v.Letter, secret));
                if (err != null) await Dialogs.Error("No se pudo desbloquear", err);
                else { Log.Ok($"{v.Letter} desbloqueada"); Toast.Show("Unidad desbloqueada", v.Letter, Icons.Success); }
            }));
        else if (b.Encrypted && b.Suspended)
            actions.Children.Add(act("Reanudar", Icons.Play, async () => { await Task.Run(() => BitLocker.Resume(v.Letter)); Log.Ok($"Protección BitLocker reanudada en {v.Letter}"); }));
        else if (b.Encrypted)
        {
            actions.Children.Add(act("Suspender", Icons.Pause, async () =>
            {
                if (!await Dialogs.Confirm($"Suspender BitLocker en {v.Letter}", "Los datos seguirán cifrados, pero la clave quedará accesible hasta que reanudes la protección" + (v.IsSystem ? " (Windows la reanuda solo tras el próximo reinicio)." : "."), "Suspender")) return;
                await Task.Run(() => BitLocker.Suspend(v.Letter, v.IsSystem ? 1u : 0u));
                Log.Ok($"Protección BitLocker suspendida en {v.Letter}");
            }));
            if (!v.IsSystem && b.Detailed) actions.Children.Add(act("Bloquear", Icons.Lock, async () => { await Task.Run(() => BitLocker.Lock(v.Letter)); Log.Ok($"{v.Letter} bloqueada"); }));
        }
        return Card(Columns((Tile(b.Encrypted || b.Locked ? Icons.Lock : v.Glyph, b.Encrypted || b.Locked ? (Brush)Application.Current.FindResource("BrandBrush") : (Brush)Application.Current.FindResource("BrandSlateBrush"), 36), Auto),
            (info.Also(i => i.Margin = new Thickness(12, 0, 0, 0)), Star), (actions, Auto)), new Thickness(0, 0, 0, 8), 14);
    }
}

/// <summary>Acciones de unidad compartidas entre páginas.</summary>
public static class DiskActions
{
    /// <summary>Expulsa la unidad; si algún programa la está usando, lo muestra antes.</summary>
    public static async void Eject(VolumeInfo v)
    {
        if (TransferQueue.Active != null && (TransferQueue.Active.Subtitle.Contains(v.Root) || TransferQueue.Active.Title.Contains(v.Letter)))
        { await Dialogs.Info("Disco en uso", "Hay una operación en curso con este disco. Espera a que termine."); return; }
        var lockers = (await Task.Run(() => { try { return LockFinder.Find(v.Letter); } catch { return []; } })).Where(l => !l.IsSelf).ToList();
        if (lockers.Count > 0)
        {
            var sp = new StackPanel { MaxWidth = 560 };
            foreach (var l in lockers.Take(6))
                sp.Children.Add(Row(Icon(Icons.Terminal, 13, margin: new Thickness(0, 0, 8, 0)), Text($"{l.Name} (PID {l.Pid})" + (l.Files.Count > 0 ? " · " + Path.GetFileName(l.Files[0]) : ""), size: 13)).Also(r => r.Margin = new Thickness(0, 2, 0, 2)));
            int r = await Dialogs.Show($"{v.Letter} está en uso", $"Estos programas tienen archivos abiertos en {v.Letter}. Si expulsas ahora, Windows puede negarse o podrías perder datos.", ["Cerrarlos y expulsar", "Expulsar igualmente", "Cancelar"], 0, extra: sp, glyph: Icons.Warning, glyphBrush: Brushes2.Caution);
            if (r == 2) return;
            if (r == 0)
                foreach (var l in lockers)
                    if (!await Task.Run(() => LockFinder.Close(l, false)))
                    {
                        await Dialogs.Error("No se pudo cerrar", $"«{l.Name}» no se ha cerrado. Ciérralo tú y vuelve a intentarlo, o usa la herramienta «¿Por qué no puedo expulsarlo?».");
                        return;
                    }
        }
        try
        {
            if (!Native.Eject(v.Root)) { await Dialogs.Error("No se pudo expulsar", "Windows no ofrece expulsar esta unidad."); return; }
            Log.Info($"Expulsión solicitada para {v.Letter}");
            Toast.Show("Expulsando", $"{v.Letter} {v.Name}. Windows avisará cuando sea seguro retirarlo.", Icons.Eject);
            await Task.Delay(4000);
            if (Directory.Exists(v.Root) && DriveInfo.GetDrives().Any(d => d.Name.Equals(v.Root, StringComparison.OrdinalIgnoreCase) && d.IsReady))
            {
                if (await Dialogs.Confirm($"{v.Letter} sigue conectada", "Windows no ha podido expulsarla, normalmente porque algún programa la está usando. ¿Quieres ver cuáles?", "Ver programas"))
                    MainWindow.Instance.Navigate<ToolsView>("tools").Open("locks", v.Root);
            }
        }
        catch (Exception ex) { await Dialogs.Error("No se pudo expulsar", ex.Message); }
    }
}
