using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DiskStudio.Core;

// ═══════════════════════════ Comprobar la capacidad real (pendrives falsos) ═══════════════════════════

public class CapacityResult
{
    public long Tested, Ok, Corrupt;
    public long FirstBad = -1;          // posición del primer bloque erróneo
    public bool WriteFailed;            // el dispositivo dejó de aceptar escrituras
    public double WriteSpeed, ReadSpeed;
    public bool Passed => Corrupt == 0 && !WriteFailed && Tested > 0;
    public string Verdict => Passed
        ? $"Todo correcto: se escribieron y comprobaron {Fmt.Size(Tested)} sin errores."
        : FirstBad >= 0
            ? $"El dispositivo falla a partir de {Fmt.Size(FirstBad)}: {Fmt.Size(Corrupt)} no conservan los datos. Solo los primeros {Fmt.Size(FirstBad)} son fiables (capacidad falsa o memoria dañada)."
            : WriteFailed ? $"El dispositivo dejó de aceptar datos tras {Fmt.Size(Tested)}." : "No se pudo completar la prueba.";
}

/// <summary>Llena el espacio libre con datos de prueba y los vuelve a leer sin caché (como H2testw).</summary>
public static class CapacityCheck
{
    public const int Block = 1 << 20;
    public const string FolderName = "DSS-Comprobacion";
    const long FileBlocks = 1024;       // 1 GiB por archivo
    const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static TransferJob Job(VolumeInfo v, bool deleteAfter, Action<CapacityResult> done) => new()
    {
        Title = $"Comprobar capacidad real de {v.Letter}",
        Subtitle = $"{v.Name} · {Fmt.Size(v.Free)} libres",
        Glyph = Icons.Shield,
        Work = ctx => Task.Run(() =>
        {
            if (v.IsSystem) throw new InvalidOperationException("No se puede llenar la unidad del sistema.");
            var r = Run(Path.Combine(v.Root, FolderName), 0, deleteAfter, ctx);
            (r.Passed ? (Action<string>)Log.Ok : Log.Warn)($"{v.Letter}: {r.Verdict}");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => done?.Invoke(r));
        })
    };

    /// <summary>Patrón determinista de cada bloque: incluye su posición para detectar memorias que «dan la vuelta».</summary>
    public static void Fill(Span<byte> buf, long blockIndex)
    {
        var u = MemoryMarshal.Cast<byte, ulong>(buf);
        ulong x = (ulong)blockIndex * 0x9E3779B97F4A7C15UL ^ 0xD1B54A32D192ED03UL;
        u[0] = (ulong)blockIndex;
        for (int i = 1; i < u.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; u[i] = x; }
    }

    /// <param name="limit">Bytes a probar (0 = todo el espacio libre).</param>
    public static CapacityResult Run(string dir, long limit, bool deleteAfter, JobContext ctx)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.dss")) File.Delete(f);
        string root = Path.GetPathRoot(Path.GetFullPath(dir))!;
        long free = new DriveInfo(root).AvailableFreeSpace - 4L * Block;   // margen para el sistema de archivos
        long total = (limit > 0 ? Math.Min(limit, free) : free) / Block * Block;
        if (total < Block) throw new IOException("No hay espacio libre suficiente para la prueba.");
        var r = new CapacityResult();
        ctx.SetTotal(total * 2);
        using var mem = new AlignedBuffer(Block);
        var files = new List<(string path, long first, long blocks)>();
        long written = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            // 1) Escribir
            for (int n = 1; written < total; n++)
            {
                long blocks = Math.Min(FileBlocks, (total - written) / Block);
                string path = Path.Combine(dir, $"{n:D5}.dss");
                long first = written / Block;
                long done = 0;
                try
                {
                    using var h = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering, blocks * Block);
                    for (; done < blocks; done++)
                    {
                        ctx.Check();
                        Fill(mem.Span, first + done);
                        RandomAccess.Write(h, (ReadOnlySpan<byte>)mem.Span, done * Block);
                        written += Block;
                        ctx.AddBytes(Block);
                        ctx.SetCurrent($"Escribiendo datos de prueba: {Fmt.Size(written)} de {Fmt.Size(total)}");
                    }
                }
                catch (IOException ex)
                {
                    r.WriteFailed = true;
                    Log.Warn($"Escritura interrumpida en {Fmt.Size(written)}: {ex.Message}");
                }
                if (done > 0) files.Add((path, first, done));
                if (r.WriteFailed) break;
            }
            r.WriteSpeed = written / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            if (r.WriteFailed) ctx.SetTotal(written * 2 + (total - written));

            // 2) Leer y comparar (sin caché de Windows)
            sw.Restart();
            var expect = new byte[Block];
            foreach (var (path, first, blocks) in files)
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering);
                for (long b = 0; b < blocks; b++)
                {
                    ctx.Check();
                    bool ok;
                    try
                    {
                        int got = RandomAccess.Read(h, mem.Span, b * Block);
                        Fill(expect, first + b);
                        ok = got == Block && mem.Span.SequenceEqual(expect);
                    }
                    catch (IOException) { ok = false; }
                    r.Tested += Block;
                    if (ok) r.Ok += Block;
                    else { r.Corrupt += Block; if (r.FirstBad < 0) r.FirstBad = (first + b) * Block; }
                    ctx.AddBytes(Block);
                    ctx.SetCurrent($"Comprobando: {Fmt.Size(r.Tested)} de {Fmt.Size(written)}" + (r.Corrupt > 0 ? $" · {Fmt.Size(r.Corrupt)} con errores" : ""));
                }
            }
            r.ReadSpeed = r.Tested / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            if (deleteAfter)
            {
                foreach (var f in files) try { File.Delete(f.path); } catch { }
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
        }
        return r;
    }
}

/// <summary>Memoria alineada a 4 KB, necesaria para leer y escribir sin caché.</summary>
public sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    readonly byte* ptr;
    readonly int length;
    public AlignedBuffer(int length) { this.length = length; ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096); }
    public override Span<byte> GetSpan() => new(ptr, length);
    public Span<byte> Span => GetSpan();
    public override MemoryHandle Pin(int elementIndex = 0) => new(ptr + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(ptr);
}

// ═══════════════════════════ ¿Qué programa está usando el disco? ═══════════════════════════

public class Locker
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> Files { get; } = [];
    public bool IsSelf => Pid == Environment.ProcessId;
    public string FilesText => Files.Count == 0 ? "Programa ejecutándose desde la unidad" : string.Join("\n", Files.Take(6)) + (Files.Count > 6 ? $"\n… y {Files.Count - 6} más" : "");
}

public static class LockFinder
{
    [StructLayout(LayoutKind.Sequential)]
    struct HandleEntry
    {
        public IntPtr Object, Pid, Handle;
        public uint Access;
        public ushort BackTrace, TypeIndex;
        public uint Attributes, Reserved;
    }

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFinalPathNameByHandle(IntPtr h, StringBuilder buf, uint len, uint flags);

    /// <summary>Busca los procesos con archivos abiertos en la unidad (p. ej. "E:").</summary>
    public static List<Locker> Find(string letter)
    {
        string prefix = letter.TrimEnd('\\').ToUpperInvariant() + "\\";
        var found = new Dictionary<int, Locker>();
        Locker of(int pid)
        {
            if (found.TryGetValue(pid, out var l)) return l;
            string name = "", title = "";
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; title = p.MainWindowTitle; } catch { }
            return found[pid] = new Locker { Pid = pid, Name = name, Title = title };
        }

        // Programas que se ejecutan desde la unidad
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainModule?.FileName is string f && f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) of(p.Id); }
            catch { }
            finally { p.Dispose(); }
        }

        // Archivos abiertos (tabla de identificadores del sistema)
        var task = Task.Run(() => ScanHandles(prefix, (pid, file) => { lock (found) { var l = of(pid); if (!l.Files.Contains(file)) l.Files.Add(file); } }));
        task.Wait(TimeSpan.FromSeconds(25));
        return found.Values.Where(l => !l.IsSelf || l.Files.Count > 0).OrderBy(l => l.Name).ToList();
    }

    static HandleEntry[] Snapshot()
    {
        int len = 1 << 22;
        while (true)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int st = NtQuerySystemInformation(64, buf, len, out int need);
                if (st == unchecked((int)0xC0000004)) { len = Math.Max(len * 2, need + (1 << 20)); continue; }
                if (st != 0) return [];
                long count = Marshal.ReadIntPtr(buf).ToInt64();
                int size = Marshal.SizeOf<HandleEntry>();
                IntPtr first = buf + IntPtr.Size * 2;
                var r = new HandleEntry[count];
                for (long i = 0; i < count; i++) r[i] = Marshal.PtrToStructure<HandleEntry>(first + (int)(i * size));
                return r;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static void ScanHandles(string prefix, Action<int, string> hit)
    {
        // Índice del tipo «File»: se averigua con un archivo abierto por nosotros
        HandleEntry[] all;
        ushort fileType = 0;
        string probe = Path.GetTempFileName();
        using (var fs = new FileStream(probe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.DeleteOnClose))
        {
            IntPtr mine = fs.SafeFileHandle.DangerousGetHandle();
            int me = Environment.ProcessId;
            all = Snapshot();
            foreach (var e in all) if (e.Pid.ToInt32() == me && e.Handle == mine) { fileType = e.TypeIndex; break; }
        }
        Stats = $"tabla {all.Length}, tipo {fileType}";
        if (fileType == 0) return;
        var files = all.Where(e => e.TypeIndex == fileType && e.Pid.ToInt32() != 4).ToArray();

        // Algunas consultas pueden quedarse colgadas (tuberías síncronas): un vigilante salta ese identificador
        var procs = new System.Collections.Concurrent.ConcurrentDictionary<int, IntPtr>();
        IntPtr self = GetCurrentProcess();
        int next = 0, generation = 0;
        long progress = 0;
        void worker(int gen)
        {
            var sb = new StringBuilder(1024);
            while (true)
            {
                int i = Interlocked.Increment(ref next) - 1;
                if (i >= files.Length || gen != Volatile.Read(ref generation)) return;
                var e = files[i];
                int pid = e.Pid.ToInt32();
                IntPtr hp = procs.GetOrAdd(pid, p => OpenProcess(0x0040 /*DUP_HANDLE*/, false, p));
                if (hp != IntPtr.Zero && DuplicateHandle(hp, e.Handle, self, out var dup, 0, false, 2 /*SAME_ACCESS*/))
                {
                    try
                    {
                        if (GetFileType(dup) == 1)   // solo archivos de disco
                        {
                            sb.Clear();
                            uint n = GetFinalPathNameByHandle(dup, sb, (uint)sb.Capacity, 0);
                            if (n > 0 && n < sb.Capacity)
                            {
                                string path = sb.ToString();
                                if (path.StartsWith(@"\\?\")) path = path[4..];
                                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Equals(prefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) hit(pid, path);
                            }
                        }
                    }
                    finally { CloseHandle(dup); }
                }
                Interlocked.Increment(ref progress);
            }
        }
        Thread start(int gen) { var t = new Thread(() => worker(gen)) { IsBackground = true }; t.Start(); return t; }
        var current = start(0);
        long seen = -1;
        var deadline = DateTime.Now.AddSeconds(20);
        while (!current.Join(300) && DateTime.Now < deadline)
        {
            long p = Interlocked.Read(ref progress);
            if (p == seen)
            {
                // sin avances en 300 ms: abandonar ese hilo y seguir con el siguiente identificador
                int gen = Interlocked.Increment(ref generation);
                current = start(gen);
            }
            seen = p;
        }
        foreach (var h in procs.Values) if (h != IntPtr.Zero) CloseHandle(h);
        Stats += $", archivos {files.Length}, revisados {Interlocked.Read(ref progress)}";
    }

    internal static string Stats = "";

    /// <summary>Pide al programa que se cierre y, si no responde, lo termina.</summary>
    public static bool Close(Locker l, bool force)
    {
        try
        {
            using var p = Process.GetProcessById(l.Pid);
            if (!force && p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(5000)) return true;
            if (!force) return false;
            p.Kill(true);
            return p.WaitForExit(5000);
        }
        catch (ArgumentException) { return true; }   // ya no existe
    }
}

// ═══════════════════════════ Comprobar, reparar y optimizar ═══════════════════════════

public enum RepairMode { Scan, Fix, Optimize }

public static class VolumeRepair
{
    public static TransferJob Job(VolumeInfo v, RepairMode mode) => new()
    {
        Title = mode switch
        {
            RepairMode.Scan => $"Comprobar {v.Letter}",
            RepairMode.Fix => v.IsSystem ? $"Programar reparación de {v.Letter}" : $"Reparar {v.Letter}",
            _ => $"Optimizar {v.Letter}"
        },
        Subtitle = $"{v.Name} · {v.FileSystem} · {Fmt.Size(v.Total)}",
        Glyph = mode == RepairMode.Optimize ? Icons.Speed : Icons.Wrench,
        Work = ctx => Task.Run(() => Run(ctx, v, mode))
    };

    static void Run(JobContext ctx, VolumeInfo v, RepairMode mode)
    {
        string drive = v.Letter.TrimEnd('\\');
        bool ntfs = v.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || v.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        if (mode == RepairMode.Fix && v.IsSystem)
        {
            ctx.SetCurrent("Marcando la unidad para comprobarla al reiniciar…");
            var (code, outp) = PartitionTable.RunTool("fsutil.exe", $"dirty set {drive}");
            if (code != 0) throw new IOException("fsutil: " + outp);
            Log.Ok($"{drive} se comprobará y reparará la próxima vez que reinicies Windows.");
            return;
        }
        if (mode == RepairMode.Fix && string.Equals(Path.GetPathRoot(AppContext.BaseDirectory), v.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disk Studio Suite se está ejecutando desde esta unidad: no se puede desmontar para repararla.");
        string exe, args, what;
        switch (mode)
        {
            case RepairMode.Scan: exe = "chkdsk.exe"; args = ntfs ? $"{drive} /scan" : drive; what = "Comprobando"; break;
            case RepairMode.Fix: exe = "chkdsk.exe"; args = $"{drive} /f /x"; what = "Reparando"; break;
            default: exe = "defrag.exe"; args = $"{drive} /O /U /V"; what = "Optimizando"; break;
        }
        ctx.SetTotal(100);
        long reported = 0;
        var (exit, output) = RunLive(exe, args, ctx, line =>
        {
            var m = Regex.Match(line, @"(\d{1,3})(?:[.,]\d+)?\s*%");
            if (m.Success && long.TryParse(m.Groups[1].Value, out long pct) && pct <= 100)
            {
                if (pct > reported) { ctx.AddBytes(pct - reported); reported = pct; }
                ctx.SetCurrent($"{what}: {pct}%");
            }
            else if (line.Length > 4) ctx.SetCurrent(line.Length > 120 ? line[..120] + "…" : line);
        });
        if (reported < 100) ctx.AddBytes(100 - reported);
        var summary = Summary(output);
        if (mode != RepairMode.Optimize)
        {
            // chkdsk: 0 sin errores · 1 errores corregidos · 2 limpieza realizada · 3 no se pudo comprobar o quedan errores
            string res = exit switch
            {
                0 => "sin errores", 1 => "se encontraron y corrigieron errores", 2 => "se hizo una limpieza menor",
                _ => mode == RepairMode.Scan ? "se encontraron problemas: usa «Reparar»" : "no se pudieron corregir todos los errores"
            };
            (exit <= 2 ? (Action<string>)Log.Ok : Log.Warn)($"{drive}: {res}. {summary}");
            if (exit > 2) ctx.Fail(drive, res + ". " + summary);
        }
        else if (exit != 0) throw new IOException($"defrag terminó con el código {exit}. {summary}");
        else Log.Ok($"{drive} optimizada. {summary}");
    }

    static string Summary(string output) => string.Join(" ", output.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 10 && !l.Contains('%')).TakeLast(2));

    /// <summary>Ejecuta una herramienta de consola leyendo su salida en vivo (también las líneas que se reescriben con \r).</summary>
    public static (int code, string output) RunLive(string exe, string args, JobContext ctx, Action<string> onLine)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        string path = Path.Combine(Environment.SystemDirectory, exe);
        var psi = new ProcessStartInfo(File.Exists(path) ? path : exe, args)
        { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = enc, StandardErrorEncoding = enc };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var all = new StringBuilder();
        var line = new StringBuilder();
        int ch;
        while ((ch = p.StandardOutput.Read()) >= 0)
        {
            if (ctx != null && ctx.Token.IsCancellationRequested) { try { p.Kill(true); } catch { } ctx.Check(); }
            all.Append((char)ch);
            if (ch is '\r' or '\n') { if (line.Length > 0) onLine(line.ToString().Trim()); line.Clear(); }
            else line.Append((char)ch);
        }
        if (line.Length > 0) onLine(line.ToString().Trim());
        p.WaitForExit();
        return (p.ExitCode, all + err.Result);
    }
}

// ═══════════════════════════ BitLocker ═══════════════════════════

public class BitLockerInfo
{
    public string Letter = "";
    public bool Encrypted, Locked, Protected, Suspended, InProgress;
    public int Percent = -1;
    public string Method = "";
    public bool Detailed;                 // datos completos (como administrador)
    public string StatusText => Locked ? "Bloqueada" : !Encrypted ? (InProgress ? "Descifrando" : "Sin cifrar")
        : InProgress ? (Percent >= 0 ? $"Cifrando ({Percent}%)" : "Cifrando") : Suspended ? "Protección suspendida" : "Cifrada y protegida";
}

public static class BitLocker
{
    const string Ns = @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption";

    static ManagementObject Volume(string letter)
    {
        var scope = new ManagementScope(Ns);
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='{letter.TrimEnd('\\')}'"));
        return s.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    static uint Call(ManagementObject o, string method, Dictionary<string, object> args, out ManagementBaseObject result)
    {
        var p = o.GetMethodParameters(method);
        if (args != null) foreach (var (k, v) in args) p[k] = v;
        result = o.InvokeMethod(method, p, null);
        return Convert.ToUInt32(result?["ReturnValue"] ?? 0u);
    }

    /// <summary>Estado de BitLocker. Sin administrador solo se sabe lo que expone el Explorador.</summary>
    public static BitLockerInfo Get(string letter)
    {
        var info = new BitLockerInfo { Letter = letter };
        if (Native.IsAdmin)
        {
            try
            {
                var v = Volume(letter);
                if (v == null) return info;
                info.Detailed = true;
                Call(v, "GetConversionStatus", null, out var conv);
                uint cs = Convert.ToUInt32(conv["ConversionStatus"]);
                info.Percent = (int)Convert.ToSingle(conv["EncryptionPercentage"] ?? 0);
                info.Encrypted = cs is 1 or 2 or 4 || (cs is 3 or 5 && info.Percent > 0);
                info.InProgress = cs is 2 or 3;
                Call(v, "GetLockStatus", null, out var ls);
                info.Locked = Convert.ToUInt32(ls["LockStatus"]) == 1;
                Call(v, "GetProtectionStatus", null, out var ps);
                uint prot = Convert.ToUInt32(ps["ProtectionStatus"]);
                info.Protected = prot == 1;
                info.Suspended = info.Encrypted && !info.Locked && prot == 0;
                if (Call(v, "GetEncryptionMethod", null, out var em) == 0)
                    info.Method = Convert.ToUInt32(em["EncryptionMethod"]) switch { 1 => "AES 128 + difusor", 2 => "AES 256 + difusor", 3 => "AES 128", 4 => "AES 256", 6 => "XTS-AES 128", 7 => "XTS-AES 256", _ => "" };
                return info;
            }
            catch (Exception ex) { Log.Warn($"BitLocker ({letter}): {ex.Message}"); }
        }
        // Propiedad del shell: 1 activo · 2 desactivado · 3 cifrando · 4 descifrando · 5 suspendido · 6 bloqueado
        int? sp = ShellStatus(letter);
        if (sp is int s)
        {
            info.Encrypted = s is 1 or 3 or 5 or 6;
            info.Protected = s is 1;
            info.Suspended = s is 5;
            info.Locked = s is 6;
            info.InProgress = s is 3 or 4;
        }
        return info;
    }

    static int? ShellStatus(string letter)
    {
        int? r = null;
        var t = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic item = shell.NameSpace(17).ParseName(letter.TrimEnd('\\') + "\\");
                object v = item?.ExtendedProperty("System.Volume.BitLockerProtection");
                if (v != null) r = Convert.ToInt32(v);
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(3000);
        return r;
    }

    public static string Unlock(string letter, string secret)
    {
        var v = Volume(letter) ?? throw new IOException("No se encontró el volumen.");
        string clean = secret.Trim();
        bool recovery = Regex.IsMatch(clean, @"^\d{6}(-?\d{6}){7}$");
        if (recovery && !clean.Contains('-')) clean = string.Join("-", Enumerable.Range(0, 8).Select(i => clean.Substring(i * 6, 6)));
        uint code = recovery ? Call(v, "UnlockWithNumericalPassword", new() { ["NumericalPassword"] = clean }, out _)
                             : Call(v, "UnlockWithPassphrase", new() { ["Passphrase"] = clean }, out _);
        return code == 0 ? null : code switch
        {
            0x80310027 or 0x80310033 or 0x8031006C => "La contraseña o la clave de recuperación no es correcta.",
            0x80310008 => "La unidad ya está desbloqueada.",
            _ => $"Windows no pudo desbloquear la unidad (código 0x{code:X8})."
        };
    }

    /// <summary>Suspende la protección (los datos siguen cifrados). rebootCount 0 = hasta reanudar a mano.</summary>
    public static void Suspend(string letter, uint rebootCount = 0)
    {
        var v = Volume(letter) ?? throw new IOException("No se encontró el volumen.");
        uint code = Call(v, "DisableKeyProtectors", new() { ["DisableCount"] = rebootCount }, out _);
        if (code != 0) throw new IOException($"No se pudo suspender BitLocker (código 0x{code:X8}).");
    }

    public static void Resume(string letter)
    {
        var v = Volume(letter) ?? throw new IOException("No se encontró el volumen.");
        uint code = Call(v, "EnableKeyProtectors", null, out _);
        if (code != 0) throw new IOException($"No se pudo reanudar BitLocker (código 0x{code:X8}).");
    }

    public static void Lock(string letter)
    {
        var v = Volume(letter) ?? throw new IOException("No se encontró el volumen.");
        uint code = Call(v, "Lock", new() { ["ForceDismount"] = true }, out _);
        if (code != 0) throw new IOException($"No se pudo bloquear la unidad (código 0x{code:X8}).");
    }
}
