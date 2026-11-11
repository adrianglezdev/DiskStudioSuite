using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DiskStudio.Core;

public enum InstallKind { Portable, Installed }

/// <summary>
/// Actualización desde la propia app: descarga el instalador (si está instalada) o el ejecutable (si es portable)
/// de la release de GitHub, comprueba su SHA-256 con SHA256SUMS.txt y lo aplica reiniciando la app.
/// </summary>
public static class Updater
{
    /// <summary>Clave de desinstalación que crea el instalador (Inno Setup añade «_is1» al AppId).</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{912AA28E-5E5C-4996-8772-5C2E84DDA6C0}_is1";
    public const string PortableName = "DiskStudioSuite.exe";
    public const string SumsName = "SHA256SUMS.txt";
    static readonly Regex SetupName = new(@"^DiskStudioSuite-Setup-[\d.]+(-[\w.]+)?\.exe$", RegexOptions.IgnoreCase);

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, PortableName);
    public static string ExeDir => Path.GetDirectoryName(ExePath)!;

    public static InstallKind Kind => InstalledDir() is string d && SameDir(d, ExeDir) ? InstallKind.Installed : InstallKind.Portable;

    static string InstalledDir()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(UninstallKey); return k?.GetValue("InstallLocation") as string; }
        catch { return null; }
    }

    public static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>El archivo de la release que corresponde a esta forma de ejecutarse.</summary>
    public static ReleaseAsset AssetFor(UpdateInfo u, InstallKind kind)
    {
        var list = u.Assets ?? [];
        var setup = list.FirstOrDefault(a => SetupName.IsMatch(a.Name));
        var portable = list.FirstOrDefault(a => a.Name.Equals(PortableName, StringComparison.OrdinalIgnoreCase));
        return kind == InstallKind.Installed ? setup ?? portable : portable;
    }

    /// <summary>Lee un SHA256SUMS.txt («hash  nombre» o «hash *nombre» por línea).</summary>
    public static Dictionary<string, string> ParseSums(string text)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success) r[m.Groups[2].Value.Trim()] = m.Groups[1].Value.ToLowerInvariant();
        }
        return r;
    }

    public static bool CanWrite(string dir)
    {
        try { string t = Path.Combine(dir, $".dss-prueba-{Guid.NewGuid():N}"); File.WriteAllText(t, ""); File.Delete(t); return true; }
        catch { return false; }
    }

    static string WorkDir { get { var d = Path.Combine(Path.GetTempPath(), "DiskStudioSuite-actualizacion"); Directory.CreateDirectory(d); return d; } }

    /// <summary>Descarga un archivo (o lo copia si la «URL» es una ruta local, para pruebas) informando del progreso.</summary>
    static async Task Fetch(string url, string target, long expected, IProgress<(long done, long total)> progress, CancellationToken tk)
    {
        if (File.Exists(url) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            string src = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
            await using var i = File.OpenRead(src);
            await using var o = File.Create(target);
            await Copy(i, o, i.Length, progress, tk);
            return;
        }
        using var http = Updates.Http(TimeSpan.FromMinutes(30));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, tk);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? expected;
        await using var s = await resp.Content.ReadAsStreamAsync(tk);
        await using var f = File.Create(target);
        await Copy(s, f, total, progress, tk);
    }

    static async Task Copy(Stream i, Stream o, long total, IProgress<(long, long)> progress, CancellationToken tk)
    {
        var buf = new byte[1 << 20];
        long done = 0; int n;
        while ((n = await i.ReadAsync(buf, tk)) > 0)
        {
            await o.WriteAsync(buf.AsMemory(0, n), tk);
            done += n;
            progress?.Report((done, total));
        }
    }

    /// <summary>Descarga el archivo de la actualización y comprueba su SHA-256. Devuelve la ruta descargada.</summary>
    public static async Task<string> Download(UpdateInfo u, ReleaseAsset asset, IProgress<(long done, long total)> progress, CancellationToken tk)
    {
        var sumsAsset = u.Assets?.FirstOrDefault(a => a.Name.Equals(SumsName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("La versión publicada no incluye SHA256SUMS.txt: no se puede comprobar la descarga.");
        string dir = Path.Combine(WorkDir, u.Tag);
        Directory.CreateDirectory(dir);
        string sumsFile = Path.Combine(dir, SumsName);
        await Fetch(sumsAsset.Url, sumsFile, sumsAsset.Size, null, tk);
        var sums = ParseSums(await File.ReadAllTextAsync(sumsFile, tk));
        if (!sums.TryGetValue(asset.Name, out var expected))
            throw new InvalidDataException($"SHA256SUMS.txt no incluye «{asset.Name}»: no se puede comprobar la descarga.");
        string target = Path.Combine(dir, asset.Name);
        await Fetch(asset.Url, target, asset.Size, progress, tk);
        string got;
        await using (var f = File.OpenRead(target)) got = Convert.ToHexString(await SHA256.HashDataAsync(f, tk)).ToLowerInvariant();
        if (got != expected)
        {
            try { File.Delete(target); } catch { }
            throw new InvalidDataException("La descarga está dañada o no es la publicada (su SHA-256 no coincide). No se ha instalado nada.");
        }
        Log.Ok($"Actualización {u.Tag} descargada y verificada (SHA-256 {got[..16]}…)");
        return target;
    }

    /// <summary>
    /// Versión portable: el ejecutable en uso se renombra a .old (Windows lo permite) y el nuevo ocupa su sitio.
    /// Si algo falla, se deja todo como estaba. Devuelve la ruta del .old.
    /// </summary>
    public static string PortableSwap(string current, string downloaded)
    {
        string old = current + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(current, old);
        try { File.Copy(downloaded, current, false); }
        catch
        {
            try { if (File.Exists(current)) File.Delete(current); } catch { }
            File.Move(old, current);
            throw;
        }
        return old;
    }

    /// <summary>Aplica la actualización descargada y cierra la app (la nueva versión se abre sola).</summary>
    public static void Apply(string file, InstallKind kind)
    {
        if (kind == InstallKind.Installed && SetupName.IsMatch(Path.GetFileName(file)))
        {
            Log.Info("Ejecutando el instalador de la actualización…");
            Process.Start(Clean(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELANZAR") { UseShellExecute = false }));
            return;
        }
        string old = PortableSwap(ExePath, file);
        try { Process.Start(Clean(new ProcessStartInfo(ExePath, "--actualizado --reinicio --sin-presentacion") { UseShellExecute = false, WorkingDirectory = ExeDir })); }
        catch
        {
            // No se pudo abrir la versión nueva: volver a la anterior
            try { File.Delete(ExePath); File.Move(old, ExePath); } catch { }
            throw;
        }
    }

    /// <summary>La versión nueva no hereda los ajustes de prueba (si no, volvería a actualizarse en bucle).</summary>
    static ProcessStartInfo Clean(ProcessStartInfo p)
    {
        foreach (var k in new[] { "DSS_UPDATE_FEED", "DSS_UPDATE_AUTO", "DSS_SNAP" }) p.Environment.Remove(k);
        return p;
    }

    /// <summary>Tras actualizar una versión portable, borra el ejecutable anterior.</summary>
    public static void CleanupOld()
    {
        string old = ExePath + ".old";
        for (int i = 0; i < 20 && File.Exists(old); i++)
        {
            try { File.Delete(old); } catch { Thread.Sleep(500); }
        }
        try { if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true); } catch { }
    }

    /// <summary>Convierte el texto de la release (Markdown sencillo) en líneas para mostrarlas.</summary>
    public static List<(int level, string text)> NotesLines(string md)
    {
        var r = new List<(int, string)>();
        foreach (var raw in (md ?? "").Replace("\r", "").Split('\n'))
        {
            string l = raw.TrimEnd();
            if (l.Trim() == "---") break;                       // a partir de aquí vienen las instrucciones de descarga
            if (l.Trim().Length == 0) continue;
            int level = 0;
            if (l.StartsWith("## ")) { level = 1; l = l[3..]; }
            else if (l.StartsWith("### ")) { level = 2; l = l[4..]; }
            else if (Regex.IsMatch(l, @"^\s*[-*] ")) l = "• " + Regex.Replace(l, @"^\s*[-*] ", "");
            l = Regex.Replace(l, @"\*\*(.+?)\*\*", "$1");
            l = Regex.Replace(l, @"(?<!\w)\*(.+?)\*(?!\w)", "$1");
            l = Regex.Replace(l, @"`([^`]+)`", "$1");
            l = Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1");
            r.Add((level, l.Trim()));
        }
        return r;
    }
}
