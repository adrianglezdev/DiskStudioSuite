using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskStudio.Core;
using static DiskStudio.Views.UI;

namespace DiskStudio.Views;

/// <summary>Montar imágenes como unidades y convertir entre formatos.</summary>
public partial class CloneView
{
    static readonly (string, string)[] IncInfo =
    [
        ("Imagen completa", "Copia todo el disco, sector a sector. Es la base: guárdala bien."),
        ("Imagen incremental", "Compara el disco con la imagen anterior y guarda solo los bloques de 1 MiB que han cambiado. Tarda lo que se tarda en leer el disco, pero ocupa muy poco. Puedes encadenar tantas como quieras: completa → incremental 1 → incremental 2…"),
        ("Para restaurar", "Elige la incremental de la fecha que quieras recuperar: Disk Studio Suite combina automáticamente la imagen completa y todas las incrementales hasta esa fecha. Todos los archivos de la cadena deben estar en la misma carpeta."),
        ("Índice de bloques", "Junto a cada imagen se guarda un archivo .blocks con la huella de cada bloque. Si lo borras, la siguiente incremental tendrá que volver a leer la imagen anterior entera."),
    ];

    static readonly (string, string)[] MountInfo =
    [
        ("Montar", "La imagen aparece como un disco más, con su letra de unidad, para que puedas abrir y copiar archivos sin restaurar nada. En solo lectura no se puede modificar por accidente."),
        ("Formatos", "ISO, VHD y VHDX se montan directamente con Windows. Para las imágenes .img, Disk Studio Suite crea primero una copia .vhd (la misma imagen con un pequeño pie de 512 bytes) y monta esa."),
        ("Convertir", "• .img → .vhdx: disco virtual dinámico que solo ocupa los datos (útil para Hyper-V o para montarlo en Windows).\n• .vhd/.vhdx → .img: imagen en bruto para restaurarla o usarla en otros programas.\n• Las imágenes incrementales se pueden convertir a una imagen completa de la fecha elegida."),
    ];

    UIElement MountPanel()
    {
        // ── Montar ──
        var file = new TextBox();
        var ro = new CheckBox { Content = "Solo lectura (recomendado)", IsChecked = true, Margin = new Thickness(0, 0, 0, 14) };
        var mounted = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void render()
        {
            mounted.Children.Clear();
            var items = VirtualDisk.Mounted.Select(m => (m.Name, m.Letters, m.Path, m.DiskNumber)).ToList();
            foreach (var d in AppState.Disks.Where(d => d.Bus == "Virtual" && !items.Any(i => i.DiskNumber == d.Number)))
                items.Add(($"Disco virtual {d.Number} · {d.Title}", string.Join(" ", d.Volumes.Select(v => v.Letter)), null, d.Number));
            if (items.Count == 0) { mounted.Children.Add(Text("No hay imágenes montadas.", "Caption", 13)); return; }
            foreach (var (name, letters, path, num) in items)
            {
                var row = Columns((Tile(Icons.Layers, null, 32), Auto),
                    (Col(Text(name, size: 14), Text(letters.Length > 0 ? "Unidades: " + letters : "Sin letra de unidad", "Caption", 12)).Also(c => c.Margin = new Thickness(12, 0, 0, 0)), Star),
                    (Row(letters.Length > 0 ? Btn("Abrir", Icons.FolderOpen, (_, _) => MainWindow.Instance.Navigate<ExplorerView>("explorer").Open(letters.Split(' ')[0] + "\\"), "SecondaryButton") : null,
                        Btn("Desmontar", Icons.Eject, async (_, _) =>
                        {
                            try { await Task.Run(() => { if (path != null) VirtualDisk.Dismount(path); else VirtualDisk.DismountDisk(num); }); Log.Info($"Imagen desmontada: {name}"); }
                            catch (Exception ex) { await Dialogs.Error("No se pudo desmontar", ex.Message); }
                            AppState.Rescan(false); render();
                        }, "SecondaryButton").Also(b => b.Margin = new Thickness(8, 0, 0, 0))), Auto));
                mounted.Children.Add(new Border { Style = S("SubtleCard"), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6), Child = row });
            }
        }
        VirtualDisk.Mounted.CollectionChanged += (_, _) => render();
        render();

        var mount = Btn("Montar", Icons.Layers, async (_, _) =>
        {
            string f = file.Text.Trim();
            if (!File.Exists(f)) { await Dialogs.Error("Imagen no encontrada", f); return; }
            string ext = VirtualDisk.Ext(f);
            bool readOnly = ro.IsChecked == true;
            if (ext is ".vhd" or ".vhdx" && !Native.IsAdmin)
            { if (await Dialogs.Confirm("Se necesita administrador", "Windows solo permite montar discos VHD y VHDX como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin(); return; }
            try
            {
                if (VirtualDisk.IsMountable(f))
                {
                    var m = await Task.Run(() => VirtualDisk.Mount(f, readOnly && ext != ".iso"));
                    Log.Ok($"Imagen montada: {f}" + (m.Letters.Length > 0 ? $" en {m.Letters}" : ""));
                    Toast.Show("Imagen montada", m.Letters.Length > 0 ? $"{Path.GetFileName(f)} → {m.Letters}" : Path.GetFileName(f), Icons.Layers);
                    AppState.Rescan(false);
                }
                else if (ext is ".img" or ".img.gz" or ".dssinc")
                {
                    if (!Native.IsAdmin) { if (await Dialogs.Confirm("Se necesita administrador", "Montar la imagen requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin(); return; }
                    string stem = f;
                    foreach (var e2 in new[] { ".dssinc", ".gz", ".img" }) if (stem.EndsWith(e2, StringComparison.OrdinalIgnoreCase)) stem = stem[..^e2.Length];
                    string vhd = stem + ".vhd";
                    if (!File.Exists(vhd))
                    {
                        long len = ext == ".img" ? new FileInfo(f).Length : 0;
                        if (!await Dialogs.Confirm("Preparar la imagen", $"Para montarla, Disk Studio Suite creará una copia en formato VHD:\n{vhd}" + (len > 0 ? $"\n\nOcupará {Fmt.Size(len)}." : ""), "Crear y montar")) return;
                    }
                    TransferQueue.Enqueue(VirtualDisk.MountImgJob(f, vhd, readOnly, m => { Toast.Show("Imagen montada", m.Letters.Length > 0 ? m.Letters : m.Name, Icons.Layers); AppState.Rescan(false); }));
                    MainWindow.Instance.Navigate("transfers");
                }
                else await Dialogs.Error("Formato no admitido", "Se pueden montar archivos .iso, .vhd, .vhdx, .img, .img.gz e incrementales.");
            }
            catch (Exception ex) { await Dialogs.Error("No se pudo montar", ex.Message); }
        }, "PrimaryButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);

        var mountCard = Panel(Icons.Layers, "Montar una imagen", "Abre una imagen como si fuera un disco para sacar archivos sin restaurarla.",
            Row(Text("Imagen", "Label"), InfoBtn("Montar y convertir imágenes", MountInfo)),
            PathPicker(file, () => Dialogs.PickFile("Imágenes|*.iso;*.vhd;*.vhdx;*.img;*.img.gz;*.dssinc|Todos|*.*")).Also(g => g.Margin = new Thickness(0, 0, 0, 10)),
            ro, mount, Text("Montadas", "SectionTitle", margin: new Thickness(0, 22, 0, 4)), mounted);

        // ── Convertir ──
        var src = new TextBox();
        var fmt = new ComboBox { ItemsSource = new[] { "VHDX (disco virtual dinámico)", "VHD (fijo)", "IMG (en bruto)", "IMG.GZ (comprimida)" }, SelectedIndex = 0, Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        string[] exts = [".vhdx", ".vhd", ".img", ".img.gz"];
        var dst = new TextBox();
        void suggest()
        {
            string s = src.Text.Trim();
            if (s.Length == 0) return;
            string stem = s;
            foreach (var e2 in new[] { ".dssinc", ".gz", ".img", ".vhdx", ".vhd", ".iso" }) if (stem.EndsWith(e2, StringComparison.OrdinalIgnoreCase)) stem = stem[..^e2.Length];
            string t = stem + exts[Math.Max(0, fmt.SelectedIndex)];
            if (t.Equals(s, StringComparison.OrdinalIgnoreCase)) t = stem + "_convertida" + exts[fmt.SelectedIndex];
            dst.Text = t;
        }
        src.TextChanged += (_, _) => suggest();
        fmt.SelectionChanged += (_, _) => suggest();
        var convert = Btn("Convertir", Icons.Swap, async (_, _) =>
        {
            string s = src.Text.Trim(), d = dst.Text.Trim();
            if (!File.Exists(s)) { await Dialogs.Error("Imagen no encontrada", s); return; }
            if (d.Length < 4) { await Dialogs.Error("Destino no válido", "Indica el archivo de destino."); return; }
            bool needsAdmin = VirtualDisk.Ext(d) == ".vhdx" || (VirtualDisk.Ext(s) is ".vhd" or ".vhdx" && !VirtualDisk.IsFixedVhd(s, out _));
            if (needsAdmin && !Native.IsAdmin) { if (await Dialogs.Confirm("Se necesita administrador", "Esta conversión monta discos virtuales y requiere ejecutar Disk Studio Suite como administrador.", "Reiniciar como administrador")) Native.RestartAsAdmin(); return; }
            if (File.Exists(d) && !await Dialogs.Confirm("El archivo ya existe", $"Se sustituirá:\n{d}", "Sustituir", true)) return;
            TransferQueue.Enqueue(VirtualDisk.ConvertJob(s, d));
            MainWindow.Instance.Navigate("transfers");
        }, "PrimaryButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left);

        var convCard = Panel(Icons.Swap, "Convertir una imagen", "Entre .img, .img.gz, .vhd y .vhdx, o de una cadena incremental a una imagen completa.",
            Field("Imagen de origen", PathPicker(src, () => Dialogs.PickFile("Imágenes|*.img;*.img.gz;*.vhd;*.vhdx;*.iso;*.dssinc|Todos|*.*"))),
            Field("Formato de destino", fmt),
            Field("Archivo de destino", PathPicker(dst, () => Dialogs.SaveFile("Imagen|*" + exts[Math.Max(0, fmt.SelectedIndex)], Path.GetFileName(dst.Text), Path.GetDirectoryName(dst.Text)))),
            convert);

        return Col(mountCard, convCard.Also(c => ((FrameworkElement)c).Margin = new Thickness(0, 16, 0, 0)));
    }
}
