using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace DiskStudio.Core;

public static class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020, FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out long outBuf, int outSize, out int returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetVolumeLabel(string root, string label);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        public string lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass;
        public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO i);

    public static void ShowProperties(string path)
    {
        var i = new SHELLEXECUTEINFO { lpVerb = "properties", lpFile = path, nShow = 5, fMask = 0x0000000C };
        i.cbSize = Marshal.SizeOf(i);
        ShellExecuteEx(ref i);
    }

    public static bool IsAdmin =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static bool RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--reinicio") { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception) { return false; } // el usuario canceló el UAC
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error("No se pudo abrir", ex.Message); }
    }

    public static void ShowInExplorer(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else Process.Start("explorer.exe", $"\"{path}\"");
    }

    public static void OpenTerminal(string dir)
    {
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir}\"") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true }); }
    }

    public static void Shutdown(bool sleep)
    {
        if (sleep) Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { CreateNoWindow = true });
        else Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 60 /c \"Disk Studio Suite: la cola de copias ha terminado.\"") { CreateNoWindow = true });
    }

    // Expulsión segura mediante el verbo "Expulsar" del shell
    public static bool Eject(string root)
    {
        Type t = Type.GetTypeFromProgID("Shell.Application")!;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic folder = shell.NameSpace(17);
        dynamic item = folder.ParseName(root);
        if (item == null) return false;
        foreach (dynamic verb in item.Verbs())
        {
            string name = ((string)verb.Name).Replace("&", "");
            if (name.Contains("xpuls", StringComparison.OrdinalIgnoreCase) || name.Contains("Eject", StringComparison.OrdinalIgnoreCase))
            { verb.DoIt(); return true; }
        }
        item.InvokeVerb("Eject");
        return true;
    }

    public static SafeFileHandle OpenRaw(string device, bool write)
    {
        var h = CreateFile(device, GENERIC_READ | (write ? GENERIC_WRITE : 0), FILE_SHARE_READ | FILE_SHARE_WRITE,
                           IntPtr.Zero, OPEN_EXISTING, write ? FILE_FLAG_WRITE_THROUGH : 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo abrir " + device);
        return h;
    }

    public static long GetDeviceLength(SafeFileHandle h)
    {
        if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long len, 8, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return len;
    }

    public static bool Ioctl(SafeFileHandle h, uint code) =>
        DeviceIoControl(h, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
}

/// <summary>Iconos del sistema para archivos y carpetas.</summary>
public static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> types = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource Get(string path, bool isDir, bool large = false)
    {
        string ext = isDir ? "" : Path.GetExtension(path);
        bool perFile = !isDir && (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase));
        bool isRoot = isDir && Path.GetPathRoot(path) == path;
        string key = (large ? "L|" : "S|") + (perFile || isRoot ? path : isDir ? "<dir>" : ext == "" ? "<file>" : ext);
        if (cache.TryGetValue(key, out var img)) return img;

        var info = new SHFILEINFO();
        uint flags = 0x100 | 0x400 | (large ? 0u : 0x1u); // ICON | TYPENAME | SMALL
        string q = path;
        if (!(perFile || isRoot)) { flags |= 0x10; q = isDir ? "folder" : "x" + ext; }
        SHGetFileInfo(q, isDir ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf(info), flags);
        ImageSource src = null;
        if (info.hIcon != IntPtr.Zero)
        {
            src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            DestroyIcon(info.hIcon);
        }
        if (!isDir && !types.ContainsKey(ext)) types[ext] = info.szTypeName ?? "";
        cache[key] = src;
        return src;
    }

    public static string TypeName(string ext)
    {
        if (types.TryGetValue(ext, out var t) && !string.IsNullOrEmpty(t)) return t;
        return ext.Length > 1 ? "Archivo " + ext[1..].ToUpperInvariant() : "Archivo";
    }
}

public static class Fmt
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(long bytes)
    {
        double b = bytes; int i = 0;
        while (Math.Abs(b) >= 1024 && i < Units.Length - 1) { b /= 1024; i++; }
        return (i == 0 ? b.ToString("0") : b.ToString(b < 10 ? "0.##" : b < 100 ? "0.#" : "0")) + " " + Units[i];
    }

    public static string Speed(double bps) => Size((long)bps) + "/s";

    /// <summary>Fecha de modificación sin fallar si el archivo tiene una fecha no válida.</summary>
    public static DateTime SafeWriteTime(this FileSystemInfo i) { try { return i.LastWriteTime; } catch (ArgumentOutOfRangeException) { return DateTime.MinValue; } }

    public static string Time(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }
}
