using System.IO;
using System.Management;

namespace DiskStudio.Core;

/// <summary>Un tramo del disco: una partición o un hueco sin asignar.</summary>
public class DiskSegment
{
    public PartitionInfo Part;           // null = espacio sin asignar
    public long Offset, Size;
    public bool IsFree => Part == null;
}

/// <summary>Editor de particiones: crear, eliminar, reducir, ampliar, dividir, letra, nombre y partición activa.</summary>
public static class PartitionEditor
{
    const long MiB = 1 << 20;
    static ManagementScope Scope => new(@"\\.\root\Microsoft\Windows\Storage");

    // ───────────── Mapa ─────────────
    public static List<DiskSegment> Segments(PhysicalDisk d)
    {
        var list = new List<DiskSegment>();
        long start = MiB;                                        // el primer MiB queda reservado para la tabla
        long end = d.Size - (d.PartitionStyle == "GPT" ? MiB : 0); // copia de la GPT al final
        long pos = start;
        foreach (var p in d.Partitions.OrderBy(p => p.Offset))
        {
            if (p.Offset - pos >= 16 * MiB) list.Add(new DiskSegment { Offset = pos, Size = p.Offset - pos });
            list.Add(new DiskSegment { Part = p, Offset = p.Offset, Size = p.Size });
            pos = Math.Max(pos, p.Offset + p.Size);
        }
        long tail = PartitionTable.AlignUp(pos, MiB);
        if (end - tail >= 16 * MiB && d.PartitionStyle is "MBR" or "GPT") list.Add(new DiskSegment { Offset = tail, Size = (end - tail) / MiB * MiB });
        return list;
    }

    public enum Role { Data, Esp, Msr, Recovery, Other }

    public static Role RoleOf(PartitionInfo p)
    {
        if (Guid.TryParse(p.GptType, out var g))
        {
            if (g == PartitionTable.GptEsp) return Role.Esp;
            if (g == PartitionTable.GptMsr) return Role.Msr;
            if (g == PartitionTable.GptRecovery) return Role.Recovery;
            if (g == PartitionTable.GptBasic) return Role.Data;
            return Role.Other;
        }
        return p.MbrType switch { 0xEF => Role.Esp, 0x27 => Role.Recovery, 0x05 or 0x0F => Role.Other, _ => Role.Data };
    }

    public static string RoleText(PartitionInfo p) => RoleOf(p) switch
    {
        Role.Esp => "Partición del sistema EFI", Role.Msr => "Reservada de Microsoft (MSR)", Role.Recovery => "Recuperación",
        Role.Other => "Otra", _ => p.IsSystem ? "Reservada para el sistema" : "Datos"
    };

    /// <summary>Motivo por el que no se puede eliminar la partición (null = se puede).</summary>
    public static string CannotDelete(PhysicalDisk d, PartitionInfo p)
    {
        if (p.IsBoot || p.IsSystem) return "Es una partición de arranque o del sistema: Windows dejaría de arrancar.";
        if (!string.IsNullOrEmpty(p.Letter) && string.Equals(p.Letter.TrimEnd('\\') + "\\", Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)) return "Es la partición de Windows.";
        if (!string.IsNullOrEmpty(p.Letter) && string.Equals(p.Letter.TrimEnd('\\') + "\\", Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) return "Disk Studio Suite se está ejecutando desde esta partición.";
        if ((d.IsSystem || d.IsBoot) && RoleOf(p) is Role.Esp or Role.Msr or Role.Recovery) return "Windows necesita esta partición para arrancar o recuperarse.";
        return null;
    }

    public static List<string> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])).ToHashSet();
        return "DEFGHIJKLMNOPQRSTUVWXYZ".Where(c => !used.Contains(c)).Select(c => c + ":").ToList();
    }

    // ───────────── Consultas ─────────────
    static ManagementObject Part(int disk, long offset) => PartitionTable.FindPartition(disk, offset) ?? throw new IOException("No se encontró la partición (¿ha cambiado el disco?).");

    static ManagementObject Disk(int n)
    {
        using var s = new ManagementObjectSearcher(Scope, new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number={n}"));
        return s.Get().Cast<ManagementObject>().FirstOrDefault() ?? throw new IOException("No se encontró el disco " + n);
    }

    static ManagementBaseObject Call(ManagementObject o, string method, Dictionary<string, object> args, string what)
    {
        var p = o.GetMethodParameters(method);
        if (args != null) foreach (var (k, v) in args) p[k] = v;
        var r = o.InvokeMethod(method, p, null);
        uint code = Convert.ToUInt32(r?["ReturnValue"] ?? 0u);
        if (code != 0) throw new IOException(code switch
        {
            40001 => $"No se pudo {what}: acceso denegado.",
            40002 or 41006 => $"No se pudo {what}: la partición está en uso.",
            41004 => $"No se pudo {what}: no hay espacio suficiente.",
            42007 or 42008 => $"No se pudo {what}: el tamaño no es válido para este sistema de archivos.",
            _ => $"No se pudo {what} (código {code})."
        });
        return r;
    }

    /// <summary>Tamaños mínimo y máximo a los que Windows puede llevar la partición.</summary>
    public static (long min, long max) SupportedSize(int disk, long offset)
    {
        var r = Call(Part(disk, offset), "GetSupportedSize", null, "consultar el tamaño admitido");
        return (Convert.ToInt64(r["SizeMin"]), Convert.ToInt64(r["SizeMax"]));
    }

    // ───────────── Operaciones (en la cola, con copia de la tabla) ─────────────
    static TransferJob Op(string title, PhysicalDisk d, string glyph, Action<JobContext> act) => new()
    {
        Title = title, Subtitle = $"Disco {d.Number} · {d.Title}", Glyph = glyph,
        Work = ctx => Task.Run(() =>
        {
            ctx.SetCurrent("Guardando una copia de la tabla de particiones…");
            try { using var h = Native.OpenRaw(d.Device, false); PartitionTable.Backup(h, PartitionTable.Read(h), d); }
            catch (Exception ex) { throw new IOException("No se pudo guardar la copia de la tabla de particiones, así que no se ha tocado nada: " + ex.Message); }
            act(ctx);
            PartitionTable.RefreshStorageCache();
        })
    };

    public static TransferJob ResizeJob(PhysicalDisk d, PartitionInfo p, long newSize) =>
        Op($"{(newSize < p.Size ? "Reducir" : "Ampliar")} {p.Display} a {Fmt.Size(newSize)}", d, Icons.Layers, ctx =>
        {
            ctx.SetCurrent(newSize < p.Size ? "Reduciendo (puede tardar si hay que mover archivos)…" : "Ampliando…");
            Call(Part(d.Number, p.Offset), "Resize", new() { ["Size"] = (ulong)(newSize / MiB * MiB) }, newSize < p.Size ? "reducir la partición" : "ampliar la partición");
            Log.Ok($"Partición {p.Display} del disco {d.Number}: {Fmt.Size(p.Size)} → {Fmt.Size(newSize)}");
        });

    public static TransferJob DeleteJob(PhysicalDisk d, PartitionInfo p) =>
        Op($"Eliminar partición {p.Display}", d, Icons.Delete, ctx =>
        {
            string why = CannotDelete(d, p);
            if (why != null) throw new InvalidOperationException(why);
            ctx.SetCurrent("Eliminando la partición…");
            if (!string.IsNullOrEmpty(p.Letter))
                foreach (var h in new[] { p.Letter })
                    try { using var v = Native.OpenRaw(@"\\.\" + h.TrimEnd('\\'), true); Native.Ioctl(v, Native.FSCTL_LOCK_VOLUME); Native.Ioctl(v, Native.FSCTL_DISMOUNT_VOLUME); } catch { }
            Call(Part(d.Number, p.Offset), "DeleteObject", null, "eliminar la partición");
            Log.Ok($"Partición {p.Display} ({Fmt.Size(p.Size)}) eliminada del disco {d.Number}");
        });

    public static TransferJob CreateJob(PhysicalDisk d, DiskSegment free, long size, FormatOptions o, string letter) =>
        Op($"Crear partición {o.FileSystem} de {Fmt.Size(size)}", d, Icons.Add, ctx => Create(ctx, d, free.Offset, size, o, letter));

    static void Create(JobContext ctx, PhysicalDisk d, long offset, long size, FormatOptions o, string letter)
    {
        ctx.SetCurrent("Creando la partición…");
        long off = PartitionTable.AlignUp(offset, MiB);
        var args = new Dictionary<string, object> { ["Offset"] = (ulong)off, ["Size"] = (ulong)(size / MiB * MiB) };
        if (string.IsNullOrEmpty(letter)) args["AssignDriveLetter"] = false; else args["DriveLetter"] = letter[0];
        if (d.PartitionStyle == "MBR") args["MbrType"] = (ushort)(o.FileSystem == "FAT32" ? 12 : 7);
        else args["GptType"] = PartitionTable.GptBasic.ToString("B");
        Call(Disk(d.Number), "CreatePartition", args, "crear la partición");
        PartitionTable.RefreshStorageCache();
        string l = null;
        for (int i = 0; i < 40 && l == null; i++) { Thread.Sleep(250); l = PartitionTable.DriveLetter(d.Number, off); }
        bool temp = l == null;
        l ??= PartitionTable.AssignLetter(d.Number, off);
        ctx.SetCurrent($"Formateando como {o.FileSystem}…");
        Formatter.FormatPartition(ctx, l, size, o);
        if (temp && string.IsNullOrEmpty(letter)) PartitionTable.RemoveLetter(d.Number, off, l);
        Log.Ok($"Partición {o.FileSystem} de {Fmt.Size(size)} creada en el disco {d.Number}" + (temp ? "" : $" ({l})"));
    }

    /// <summary>Divide una partición: la reduce y crea otra nueva en el espacio liberado.</summary>
    public static TransferJob SplitJob(PhysicalDisk d, PartitionInfo p, long firstSize, FormatOptions o, string letter) =>
        Op($"Dividir {p.Display}", d, Icons.Layers, ctx =>
        {
            ctx.SetCurrent("Reduciendo la partición original…");
            Call(Part(d.Number, p.Offset), "Resize", new() { ["Size"] = (ulong)(firstSize / MiB * MiB) }, "reducir la partición");
            PartitionTable.RefreshStorageCache();
            long newOff = PartitionTable.AlignUp(p.Offset + firstSize / MiB * MiB, MiB);
            long newSize = (p.Offset + p.Size - newOff) / MiB * MiB;
            if (newSize < 16 * MiB) throw new IOException("No queda espacio suficiente para la nueva partición.");
            Create(ctx, d, newOff, newSize, o, letter);
        });

    /// <summary>Inicializa un disco vacío (sin tabla de particiones).</summary>
    public static TransferJob InitializeJob(PhysicalDisk d, TableStyle style) => new()
    {
        Title = $"Inicializar disco {d.Number} como {style.ToString().ToUpperInvariant()}", Subtitle = d.Title, Glyph = Icons.Add,
        Work = ctx => Task.Run(() =>
        {
            if (d.IsSystem || d.IsBoot) throw new InvalidOperationException("No se puede inicializar el disco del sistema.");
            if (d.Partitions.Count > 0) throw new InvalidOperationException("El disco ya tiene particiones.");
            Call(Disk(d.Number), "Initialize", new() { ["PartitionStyle"] = (ushort)(style == TableStyle.Gpt ? 2 : 1) }, "inicializar el disco");
            PartitionTable.RefreshStorageCache();
            Log.Ok($"Disco {d.Number} inicializado como {style}");
        })
    };

    public static void SetLetter(PhysicalDisk d, PartitionInfo p, string letter)
    {
        var part = Part(d.Number, p.Offset);
        if (!string.IsNullOrEmpty(p.Letter)) Call(part, "RemoveAccessPath", new() { ["AccessPath"] = p.Letter.TrimEnd('\\') + "\\" }, "quitar la letra");
        if (!string.IsNullOrEmpty(letter)) Call(Part(d.Number, p.Offset), "AddAccessPath", new() { ["AccessPath"] = letter.TrimEnd('\\') + "\\" }, "asignar la letra");
        Log.Ok($"Partición {p.Display}: letra {(string.IsNullOrEmpty(letter) ? "quitada" : letter)}");
    }

    public static void SetActive(PhysicalDisk d, PartitionInfo p)
    {
        if (d.PartitionStyle != "MBR") throw new InvalidOperationException("Solo los discos MBR tienen partición activa.");
        Call(Part(d.Number, p.Offset), "SetAttributes", new() { ["IsActive"] = true }, "marcar la partición como activa");
        Log.Ok($"Partición {p.Display} del disco {d.Number} marcada como activa");
    }
}
