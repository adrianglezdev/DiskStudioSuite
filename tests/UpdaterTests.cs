using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DiskStudio.Core;

/// <summary>Pruebas del actualizador (sin red: usa archivos locales como si fueran la release).</summary>
static class UpdaterTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== Actualizador");
        string R = Path.Combine(Path.GetTempPath(), "dss-tests-upd");
        if (Directory.Exists(R)) Directory.Delete(R, true);
        Directory.CreateDirectory(R);

        // Release falsa con instalador, portable y sumas
        string setup = Path.Combine(R, "DiskStudioSuite-Setup-9.9.9.exe"), portable = Path.Combine(R, "DiskStudioSuite.exe"), sums = Path.Combine(R, "SHA256SUMS.txt");
        File.WriteAllBytes(setup, RandomNumberGenerator.GetBytes(300_000));
        File.WriteAllBytes(portable, RandomNumberGenerator.GetBytes(500_000));
        string H(string f) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();
        File.WriteAllText(sums, $"{H(setup)}  DiskStudioSuite-Setup-9.9.9.exe\n{H(portable)} *DiskStudioSuite.exe\n");
        var feed = new[]
        {
            new { tag_name = "v9.9.9", html_url = "u", prerelease = false, draft = false, body = "## Disk Studio Suite v9.9.9\n\nCambios:\n\n### Corregido\n- **Uno** con `código` y [enlace](http://x)\n* Dos\n\n---\n\nDescarga…",
                  assets = new[] { new { name = "DiskStudioSuite-Setup-9.9.9.exe", browser_download_url = setup, size = 300_000L }, new { name = "DiskStudioSuite.exe", browser_download_url = portable, size = 500_000L }, new { name = "SHA256SUMS.txt", browser_download_url = sums, size = 200L } } },
            new { tag_name = "v9.10.0-beta", html_url = "u2", prerelease = true, draft = false, body = "", assets = Array.Empty<object>().Select(_ => new { name = "", browser_download_url = "", size = 0L }).ToArray() },
        };
        string json = JsonSerializer.Serialize(feed);

        var u = Updates.Pick(json, new Version(0, 3, 0), includePre: false);
        check(u?.Tag == "v9.9.9" && u.Assets.Count == 3, "elige la versión estable con sus 3 archivos (sin pre-release)");
        check(Updates.Pick(json, new Version(0, 3, 0), includePre: true)?.Tag == "v9.10.0-beta", "con «versiones en pruebas» elige la pre-release más nueva");
        check(Updater.AssetFor(u, InstallKind.Installed).Name == "DiskStudioSuite-Setup-9.9.9.exe", "instalada → descarga el instalador");
        check(Updater.AssetFor(u, InstallKind.Portable).Name == "DiskStudioSuite.exe", "portable → descarga el ejecutable");
        var onlyPortable = u with { Assets = u.Assets.Where(a => !a.Name.Contains("Setup")).ToList() };
        check(Updater.AssetFor(onlyPortable, InstallKind.Installed).Name == "DiskStudioSuite.exe", "instalada sin instalador publicado → usa el ejecutable");

        var parsed = Updater.ParseSums(File.ReadAllText(sums));
        check(parsed.Count == 2 && parsed["DiskStudioSuite.exe"] == H(portable), "lee SHA256SUMS.txt (con y sin «*»)");

        var notes = Updater.NotesLines(u.Notes);
        check(notes[0] == (1, "Disk Studio Suite v9.9.9") && notes.Any(n => n == (2, "Corregido")), "notas: títulos de nivel 1 y 2");
        check(notes.Any(n => n.text == "• Uno con código y enlace") && notes.Any(n => n.text == "• Dos"), "notas: viñetas sin marcas de Markdown");
        check(!notes.Any(n => n.text.StartsWith("Descarga")), "notas: se corta en «---» (instrucciones de descarga)");

        // Descarga y verificación
        long last = 0;
        var prog = new Progress<(long done, long total)>(p => last = p.done);
        string got = Updater.Download(u, Updater.AssetFor(u, InstallKind.Portable), prog, CancellationToken.None).GetAwaiter().GetResult();
        check(File.Exists(got) && H(got) == H(portable), "descarga el archivo y comprueba su SHA-256");
        File.WriteAllText(sums, $"{new string('0', 64)}  DiskStudioSuite.exe\n");
        bool rejected = false;
        try { Updater.Download(u, Updater.AssetFor(u, InstallKind.Portable), null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "rechaza una descarga cuyo SHA-256 no coincide");

        // Sustitución del ejecutable portable
        string app = Path.Combine(R, "app"); Directory.CreateDirectory(app);
        string cur = Path.Combine(app, "DiskStudioSuite.exe"), neu = Path.Combine(R, "nuevo.exe");
        File.WriteAllText(cur, "versión vieja"); File.WriteAllText(neu, "versión nueva");
        string old = Updater.PortableSwap(cur, neu);
        check(File.ReadAllText(cur) == "versión nueva" && File.ReadAllText(old) == "versión vieja", "portable: el nuevo ocupa su sitio y el viejo queda como .old");
        File.WriteAllText(cur, "versión nueva 2");
        bool rolled = false;
        try { Updater.PortableSwap(cur, Path.Combine(R, "no-existe.exe")); }
        catch { rolled = File.Exists(cur) && File.ReadAllText(cur) == "versión nueva 2"; }
        check(rolled, "portable: si falla la copia, se deja el ejecutable como estaba");
        check(Updater.CanWrite(app), "detecta carpetas en las que se puede escribir");
        check(Updater.SameDir(app + "\\", app), "compara carpetas sin depender de la barra final");
        check(Updater.Kind == InstallKind.Portable, "las pruebas se ejecutan como «portable» (no instaladas)");
        try { Directory.Delete(R, true); } catch { }
    }
}
