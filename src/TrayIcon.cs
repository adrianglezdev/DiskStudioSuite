using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using DiskStudio.Core;

namespace DiskStudio;

/// <summary>Icono en el área de notificación (junto al reloj) con menú y avisos.</summary>
public sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA d);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int n);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);

    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const int WM_CALLBACK = 0x8000 + 77, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203, NIN_BALLOONUSERCLICK = 0x0405;

    public static TrayIcon Current { get; private set; }
    readonly HwndSource window;
    readonly IntPtr icon;
    readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    bool added;

    TrayIcon()
    {
        window = new HwndSource(new HwndSourceParameters("DiskStudioSuiteTray") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        window.AddHook(WndProc);
        ExtractIconEx(Environment.ProcessPath ?? "", 0, out _, out icon, 1);
        Add();
    }

    public static void Ensure() { Current ??= new TrayIcon(); }
    public static void Remove() { Current?.Dispose(); Current = null; }

    NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = window.Handle, uID = 1, uFlags = flags, uCallbackMessage = WM_CALLBACK, hIcon = icon, szTip = "Disk Studio Suite", szInfo = "", szInfoTitle = ""
    };

    void Add() { var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP); added = Shell_NotifyIcon(NIM_ADD, ref d); }

    public void SetTip(string tip)
    {
        var d = Data(NIF_TIP);
        d.szTip = tip.Length > 120 ? tip[..120] : tip;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// <summary>Aviso de Windows (se muestra como notificación del sistema).</summary>
    public void Balloon(string title, string text, bool warning = false)
    {
        var d = Data(NIF_INFO);
        d.szInfoTitle = title.Length > 60 ? title[..60] : title;
        d.szInfo = text.Length > 250 ? text[..250] : text;
        d.dwInfoFlags = warning ? 2 : 1;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == taskbarCreated) { Add(); return IntPtr.Zero; }
        if (msg != WM_CALLBACK) return IntPtr.Zero;
        int ev = lParam.ToInt32() & 0xFFFF;
        if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK) MainWindow.Instance?.ShowFromTray();
        else if (ev == WM_RBUTTONUP) ShowMenu();
        return IntPtr.Zero;
    }

    void ShowMenu()
    {
        var m = new ContextMenu { Placement = PlacementMode.MousePoint };
        MenuItem item(string text, string glyph, Action a)
        {
            var mi = new MenuItem { Header = L.T(text), Icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Icon"), FontSize = 14 } };
            mi.Click += (_, _) => a();
            return mi;
        }
        m.Items.Add(item("Abrir Disk Studio Suite", Icons.Home, () => MainWindow.Instance?.ShowFromTray()));
        m.Items.Add(item("Discos y salud", Icons.Health, () => { MainWindow.Instance?.ShowFromTray(); MainWindow.Instance?.Navigate("disks"); }));
        if (TransferQueue.Active != null) m.Items.Add(item("Transferencias", Icons.Copy, () => { MainWindow.Instance?.ShowFromTray(); MainWindow.Instance?.Navigate("transfers"); }));
        m.Items.Add(new Separator());
        m.Items.Add(item("Salir", Icons.Power, () => MainWindow.Instance?.ExitApp()));
        SetForegroundWindow(window.Handle);
        m.IsOpen = true;
    }

    public void Dispose()
    {
        if (added) { var d = Data(0); Shell_NotifyIcon(NIM_DELETE, ref d); added = false; }
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        window.Dispose();
    }
}
