using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskStudio.Core;

public enum TableStyle { Mbr = 0, Gpt = 1, Raw = 2 }

/// <summary>Una entrada de la tabla de particiones (MBR o GPT).</summary>
public class PartEntry
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public byte MbrType { get; set; }
    public bool Active { get; set; }
    public Guid GptType { get; set; }
    public Guid GptId { get; set; }
    public ulong GptAttributes { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Índice de la partición de origen de la que se copian los datos (-1 = nueva).</summary>
    public int Source { get; set; } = -1;
    /// <summary>Partición nueva que hay que formatear (p. ej. EFI creada al convertir).</summary>
    public string FormatAs { get; set; }
    /// <summary>La partición se ha reducido para que quepa: se crea vacía y se copia archivo a archivo.</summary>
    public bool CopyFiles { get; set; }
    public long ShrunkFrom { get; set; }
    public string Role => PartitionTable.RoleOf(this);
    public long End => Offset + Length;
}

public class DiskLayout
{
    public TableStyle Style { get; set; }
    public uint Signature { get; set; }
    public Guid DiskId { get; set; }
    public int SectorSize { get; set; } = 512;
    public long DiskSize { get; set; }
    public List<PartEntry> Parts { get; set; } = [];
}

public class ClonePlan
{
    public DiskLayout Target = new();
    public List<string> Notes = [];
    public List<string> Errors = [];
    public bool RebuildBoot;        // reconstruir el arranque de Windows en el destino
    public bool Uefi;               // arranque resultante
    public bool Ok => Errors.Count == 0;
}

public static class PartitionTable
{
    public const long MiB = 1048576;
    public static readonly Guid GptBasic = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid GptEsp = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid GptMsr = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid GptRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    public static readonly Guid GptLdmMeta = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
    public static readonly Guid GptLdmData = new("af9b60a0-1431-4f62-bc68-3311714a69ad");
    const ulong GptRequired = 0x1, GptNoDriveLetter = 0x8000000000000000;

    const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050, IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054,
               IOCTL_DISK_CREATE_DISK = 0x0007C058, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    const int EntrySize = 144, HeaderSize = 48;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);

    // ───────────────────────── Roles y tipos ─────────────────────────
    public static string RoleOf(PartEntry p)
    {
        if (p.GptType == GptEsp || p.MbrType == 0xEF) return "EFI";
        if (p.GptType == GptMsr) return "MSR";
        if (p.GptType == GptRecovery || p.MbrType == 0x27) return "Recuperación";
        if (p.GptType == GptLdmMeta || p.GptType == GptLdmData || p.MbrType == 0x42) return "Dinámico";
        if (p.MbrType is 0x05 or 0x0F or 0x85) return "Extendida";
        return "Datos";
    }

    static byte ToMbrType(PartEntry p, string fs) => RoleOf(p) switch
    {
        "EFI" => 0xEF,
        "Recuperación" => 0x27,
        _ => fs is "FAT32" ? (byte)0x0C : fs is "FAT" or "FAT16" ? (byte)0x0E : (byte)0x07
    };

    static Guid ToGptType(PartEntry p) => RoleOf(p) switch
    {
        "EFI" => GptEsp,
        "Recuperación" => GptRecovery,
        _ => GptBasic
    };

    public static long AlignUp(long v, long a) => (v + a - 1) / a * a;

    // ───────────────────────── Lectura / escritura (requiere administrador) ─────────────────────────
    public static DiskLayout Read(int disk)
    {
        using var h = Native.OpenRaw(@"\\.\PhysicalDrive" + disk, false);
        return Read(h);
    }

    public static DiskLayout Read(SafeFileHandle h)
    {
        var geo = new byte[256];
        if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, geo, geo.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo leer la geometría del disco");
        var layout = new DiskLayout
        {
            SectorSize = BinaryPrimitives.ReadInt32LittleEndian(geo.AsSpan(20)),
            DiskSize = BinaryPrimitives.ReadInt64LittleEndian(geo.AsSpan(24))
        };
        var buf = new byte[HeaderSize + EntrySize * 256];
        if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 0, buf, buf.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo leer la tabla de particiones");
        var s = buf.AsSpan();
        layout.Style = (TableStyle)BinaryPrimitives.ReadInt32LittleEndian(s);
        int count = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        if (layout.Style == TableStyle.Mbr) layout.Signature = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        if (layout.Style == TableStyle.Gpt) layout.DiskId = new Guid(s.Slice(8, 16));
        for (int i = 0; i < count; i++)
        {
            var e = s.Slice(HeaderSize + i * EntrySize, EntrySize);
            var p = new PartEntry
            {
                Offset = BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                Length = BinaryPrimitives.ReadInt64LittleEndian(e[16..])
            };
            if (layout.Style == TableStyle.Mbr)
            {
                p.MbrType = e[32];
                p.Active = e[33] != 0;
                if (p.MbrType == 0 || p.Length == 0) continue;        // huecos de la tabla MBR
            }
            else if (layout.Style == TableStyle.Gpt)
            {
                p.GptType = new Guid(e.Slice(32, 16));
                p.GptId = new Guid(e.Slice(48, 16));
                p.GptAttributes = BinaryPrimitives.ReadUInt64LittleEndian(e[64..]);
                p.Name = Encoding.Unicode.GetString(e.Slice(72, 72)).TrimEnd('\0');
                if (p.GptType == Guid.Empty) continue;
            }
            p.Source = layout.Parts.Count;
            layout.Parts.Add(p);
        }
        layout.Parts.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        for (int i = 0; i < layout.Parts.Count; i++) layout.Parts[i].Source = i;
        return layout;
    }

    /// <summary>Crea una tabla nueva vacía y después escribe las particiones. No toca los datos de las particiones.</summary>
    public static void CreateEmpty(SafeFileHandle h, DiskLayout l)
    {
        var cd = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(cd, (int)l.Style);
        if (l.Style == TableStyle.Mbr) BinaryPrimitives.WriteUInt32LittleEndian(cd.AsSpan(4), l.Signature);
        else { l.DiskId.TryWriteBytes(cd.AsSpan(4)); BinaryPrimitives.WriteInt32LittleEndian(cd.AsSpan(20), 128); }
        if (!DeviceIoControl(h, IOCTL_DISK_CREATE_DISK, cd, cd.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo crear la tabla de particiones");
    }

    public static void WriteLayout(SafeFileHandle h, DiskLayout l)
    {
        int slots = l.Style == TableStyle.Mbr ? 4 : Math.Max(l.Parts.Count, 1);
        if (l.Style == TableStyle.Mbr && l.Parts.Count > 4) throw new InvalidOperationException("MBR solo admite 4 particiones primarias.");
        var buf = new byte[HeaderSize + EntrySize * slots];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, (int)l.Style);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], slots);
        if (l.Style == TableStyle.Mbr) BinaryPrimitives.WriteUInt32LittleEndian(s[8..], l.Signature);
        else
        {
            // Reutilizar la cabecera GPT que Windows acaba de crear (DiskId y zona utilizable)
            var cur = new byte[HeaderSize + EntrySize * 4];
            if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 0, cur, cur.Length, out _, IntPtr.Zero) && BinaryPrimitives.ReadInt32LittleEndian(cur) == 1)
                cur.AsSpan(8, 40).CopyTo(s.Slice(8, 40));
            else
            {
                l.DiskId.TryWriteBytes(s.Slice(8, 16));
                long first = 34L * l.SectorSize, usable = l.DiskSize - first - 33L * l.SectorSize;
                BinaryPrimitives.WriteInt64LittleEndian(s[24..], first);
                BinaryPrimitives.WriteInt64LittleEndian(s[32..], usable);
                BinaryPrimitives.WriteInt32LittleEndian(s[40..], 128);
            }
        }
        for (int i = 0; i < l.Parts.Count; i++)
        {
            var p = l.Parts[i];
            var e = s.Slice(HeaderSize + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(e, (int)l.Style);
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], p.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[16..], p.Length);
            BinaryPrimitives.WriteInt32LittleEndian(e[24..], i + 1);
            e[28] = 1; // RewritePartition
            if (l.Style == TableStyle.Mbr)
            {
                e[32] = p.MbrType; e[33] = (byte)(p.Active ? 1 : 0); e[34] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(e[36..], (uint)(p.Offset / l.SectorSize));
            }
            else
            {
                p.GptType.TryWriteBytes(e.Slice(32, 16));
                (p.GptId == Guid.Empty ? Guid.NewGuid() : p.GptId).TryWriteBytes(e.Slice(48, 16));
                BinaryPrimitives.WriteUInt64LittleEndian(e[64..], p.GptAttributes);
                var name = Encoding.Unicode.GetBytes((p.Name ?? "").Length > 35 ? p.Name[..35] : p.Name ?? "");
                name.CopyTo(e.Slice(72, 72));
            }
        }
        for (int i = l.Parts.Count; i < slots; i++) // ranuras MBR vacías
        {
            var e = s.Slice(HeaderSize + i * EntrySize, EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(e, (int)l.Style);
            BinaryPrimitives.WriteInt32LittleEndian(e[24..], i + 1);
            e[28] = 1;
        }
        if (!DeviceIoControl(h, IOCTL_DISK_SET_DRIVE_LAYOUT_EX, buf, buf.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo escribir la tabla de particiones");
        Native.Ioctl(h, Native.IOCTL_DISK_UPDATE_PROPERTIES);
        RefreshStorageCache();
    }

    /// <summary>Obliga a Windows a releer discos y particiones (equivale a Update-HostStorageCache).</summary>
    public static void RefreshStorageCache()
    {
        try
        {
            using var c = new ManagementClass(@"\\.\root\Microsoft\Windows\Storage", "MSFT_StorageSetting", null);
            c.InvokeMethod("UpdateHostStorageCache", null);
        }
        catch { }
    }

    // ───────────────────────── Conversión de tipos ─────────────────────────
    /// <summary>Convierte el estilo conservando posiciones (para conversión sin pérdida).</summary>
    public static DiskLayout Convert(DiskLayout src, TableStyle to, Func<int, string> fsOf, List<string> errors)
    {
        var dst = new DiskLayout { Style = to, SectorSize = src.SectorSize, DiskSize = src.DiskSize, Signature = NewSignature(), DiskId = Guid.NewGuid() };
        foreach (var p in src.Parts)
        {
            string role = RoleOf(p);
            if (role == "Dinámico") { errors.Add("El disco es dinámico: no se puede convertir sin pérdida."); continue; }
            if (role == "Extendida") continue;                    // las lógicas ya vienen como entradas propias
            if (to == TableStyle.Mbr && role == "MSR") continue;   // la reservada de Microsoft no tiene datos
            var n = new PartEntry { Offset = p.Offset, Length = p.Length, Source = p.Source, Name = p.Name };
            if (to == TableStyle.Gpt)
            {
                n.GptType = src.Style == TableStyle.Gpt ? p.GptType : ToGptType(p);
                n.GptId = src.Style == TableStyle.Gpt ? p.GptId : Guid.NewGuid();
                n.GptAttributes = src.Style == TableStyle.Gpt ? p.GptAttributes : RoleOf(n) == "Recuperación" ? GptRequired | GptNoDriveLetter : 0;
                if (string.IsNullOrEmpty(n.Name)) n.Name = RoleOf(n) switch { "EFI" => "EFI system partition", "Recuperación" => "Recovery", _ => "Basic data partition" };
            }
            else
            {
                n.MbrType = src.Style == TableStyle.Mbr ? p.MbrType : ToMbrType(p, fsOf?.Invoke(p.Source));
                n.Active = src.Style == TableStyle.Mbr && p.Active;
            }
            dst.Parts.Add(n);
        }
        Validate(dst, errors);
        return dst;
    }

    public static void Validate(DiskLayout l, List<string> errors)
    {
        long ss = l.SectorSize;
        if (l.Style == TableStyle.Mbr)
        {
            if (l.Parts.Count > 4) errors.Add($"MBR admite como máximo 4 particiones primarias y hay {l.Parts.Count}.");
            long max = 0xFFFFFFFFL * ss;
            if (l.Parts.Any(p => p.End > max)) errors.Add($"MBR no puede direccionar más de {Fmt.Size(max)}: hay particiones que terminan más allá.");
            if (l.Parts.Count(p => p.Active) > 1) errors.Add("Solo puede haber una partición activa.");
        }
        else
        {
            if (l.Parts.Count > 128) errors.Add("GPT admite como máximo 128 particiones.");
            if (l.Parts.Any(p => p.Offset < 34 * ss)) errors.Add("La primera partición empieza demasiado pronto: no cabe la tabla GPT al principio del disco.");
            if (l.Parts.Any(p => p.End > l.DiskSize - 33 * ss)) errors.Add("La última partición llega al final del disco: no cabe la copia de la tabla GPT (hay que reducirla ~1 MB).");
        }
        var sorted = l.Parts.OrderBy(p => p.Offset).ToList();
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i].Offset < sorted[i - 1].End) errors.Add("Hay particiones solapadas.");
        if (l.Parts.Any(p => p.End > l.DiskSize)) errors.Add($"Las particiones no caben en el disco destino ({Fmt.Size(l.DiskSize)}).");
    }

    // ───────────────────────── Planificación del clonado ─────────────────────────
    /// <summary>
    /// Calcula la tabla del disco destino. Si se cambia de estilo o se optimiza para SSD, las particiones se recolocan
    /// de forma consecutiva alineadas a 1 MiB; si no, se conservan las posiciones exactas.
    /// </summary>
    public static ClonePlan PlanClone(DiskLayout src, long targetSize, int targetSector, TableStyle? wanted, bool alignSsd,
                                      Func<int, string> fsOf, Func<int, bool> hasWindows, Func<int, bool> isBootStore,
                                      Func<int, long> usedOf = null, Func<int, string> nameOf = null)
    {
        var plan = new ClonePlan();
        TableStyle style = wanted ?? src.Style;
        bool convert = style != src.Style;
        int winIdx = src.Parts.FindIndex(p => hasWindows?.Invoke(p.Source) == true);
        plan.RebuildBoot = winIdx >= 0;
        plan.Uefi = style == TableStyle.Gpt;
        var t = plan.Target;
        t.Style = style; t.SectorSize = targetSector; t.DiskSize = targetSize; t.Signature = NewSignature(); t.DiskId = Guid.NewGuid();
        if (targetSector != src.SectorSize) plan.Errors.Add($"Los discos tienen distinto tamaño de sector ({src.SectorSize} y {targetSector} bytes): no se pueden clonar sector a sector.");

        var parts = new List<PartEntry>();
        foreach (var p in src.Parts)
        {
            string role = RoleOf(p);
            if (role == "Dinámico") { plan.Errors.Add("El disco de origen es dinámico: no se admite."); return plan; }
            if (role == "Extendida") continue;
            if (convert && style == TableStyle.Mbr && role == "MSR") { plan.Notes.Add("Se omite la partición reservada de Microsoft (MSR): en MBR no se usa."); continue; }
            parts.Add(p);
        }

        // MBR → GPT con Windows: la "Reservada para el sistema" (arranque BIOS) se sustituye por una partición EFI
        var newParts = new List<PartEntry>();
        if (convert && style == TableStyle.Gpt && plan.RebuildBoot)
        {
            var sysRes = parts.FirstOrDefault(p => p.Source != winIdx && p.Length <= 1536 * MiB && (p.Active || isBootStore?.Invoke(p.Source) == true) && RoleOf(p) == "Datos");
            if (sysRes != null) { parts.Remove(sysRes); plan.Notes.Add($"Se sustituye la partición «Reservada para el sistema» ({Fmt.Size(sysRes.Length)}) por una partición EFI: en UEFI ya no hace falta."); }
            newParts.Add(new PartEntry { Length = 260 * MiB, GptType = GptEsp, GptId = Guid.NewGuid(), Name = "EFI system partition", FormatAs = "FAT32", GptAttributes = GptNoDriveLetter });
            newParts.Add(new PartEntry { Length = 16 * MiB, GptType = GptMsr, GptId = Guid.NewGuid(), Name = "Microsoft reserved partition" });
            plan.Notes.Add("Se crea una partición EFI de 260 MB y una MSR de 16 MB para arrancar en modo UEFI.");
        }

        bool relocate = convert || alignSsd;
        var all = newParts.Concat(parts).ToList();
        // Si en el destino no caben con sus posiciones actuales, se recolocan seguidas (y, si hace falta, se reduce una)
        long usableEnd = targetSize - (style == TableStyle.Gpt ? MiB : 0);
        if (!relocate && all.Any(p => p.End > usableEnd) && usedOf != null)
        {
            relocate = true;
            plan.Notes.Add("Las particiones no caben en sus posiciones actuales: se colocarán seguidas, alineadas a 1 MiB.");
        }
        long cursor = style == TableStyle.Gpt ? MiB : MiB;
        foreach (var p in all)
        {
            var n = new PartEntry { Length = p.Length, Source = p.Source, Name = p.Name, FormatAs = p.FormatAs };
            if (p.Source < 0) { n.GptType = p.GptType; n.GptId = p.GptId; n.GptAttributes = p.GptAttributes; }
            else if (style == TableStyle.Gpt)
            {
                n.GptType = src.Style == TableStyle.Gpt ? p.GptType : ToGptType(p);
                n.GptId = Guid.NewGuid();
                n.GptAttributes = src.Style == TableStyle.Gpt ? p.GptAttributes : RoleOf(p) == "Recuperación" ? GptRequired | GptNoDriveLetter : 0;
                if (string.IsNullOrEmpty(n.Name)) n.Name = RoleOf(p) switch { "EFI" => "EFI system partition", "Recuperación" => "Recovery", _ => "Basic data partition" };
            }
            else
            {
                n.MbrType = src.Style == TableStyle.Mbr ? p.MbrType : ToMbrType(p, fsOf?.Invoke(p.Source));
                n.Active = src.Style == TableStyle.Mbr ? p.Active : false;
            }
            n.Offset = relocate ? AlignUp(cursor, MiB) : p.Offset;
            cursor = n.Offset + n.Length;
            t.Parts.Add(n);
        }

        // Destino algo más pequeño: reducir la partición de datos con más espacio libre, si sus datos caben con margen
        long overflow = t.Parts.Count == 0 ? 0 : t.Parts.Max(p => p.End) - usableEnd;
        if (overflow > 0 && relocate && usedOf != null)
        {
            string name(int i) => nameOf?.Invoke(i) is { Length: > 0 } nm ? nm : $"la partición {i + 1}";
            var candidates = t.Parts.Where(p => p.Source >= 0 && RoleOf(p) == "Datos" && usedOf(p.Source) >= 0 &&
                                                (fsOf?.Invoke(p.Source) ?? "").ToUpperInvariant() is "NTFS" or "EXFAT" or "FAT32")
                                    .OrderByDescending(p => p.Length - usedOf(p.Source)).ToList();
            var c = candidates.FirstOrDefault();
            if (c == null) plan.Errors.Add("Las particiones no caben en el disco destino y ninguna se puede reducir (hace falta una partición de datos NTFS, exFAT o FAT32 con espacio libre).");
            else
            {
                long used = usedOf(c.Source);
                bool win = hasWindows?.Invoke(c.Source) == true;
                // Margen: 5 % (mín. 32 MB) para datos; 10 % (mín. 1 GB) para Windows, cuyos enlaces duros se duplican al copiar
                long need = used + Math.Max(used / (win ? 10 : 20), (win ? 1024 : 32) * MiB);
                long newLen = (c.Length - overflow) / MiB * MiB;
                if (newLen < need)
                    plan.Errors.Add($"Para caber en el disco destino, {name(c.Source)} tendría que quedarse en {Fmt.Size(newLen)}, y sus datos ocupan {Fmt.Size(used)}. Libera espacio en ella o usa un disco más grande.");
                else
                {
                    c.ShrunkFrom = c.Length; c.Length = newLen; c.CopyFiles = true;
                    // Correr las particiones que van detrás
                    var after = t.Parts.Where(p => p.Offset > c.Offset).OrderBy(p => p.Offset).ToList();
                    long cur = c.End;
                    foreach (var p in after) { p.Offset = AlignUp(cur, MiB); cur = p.End; }
                    plan.Notes.Add($"{name(c.Source)} se reducirá de {Fmt.Size(c.ShrunkFrom)} a {Fmt.Size(newLen)} para que quepa (sus datos ocupan {Fmt.Size(used)}). Se creará de nuevo y se copiará archivo a archivo, conservando permisos, fechas y atributos. El disco de origen no se modifica.");
                    if (win) plan.Notes.Add($"{name(c.Source)} contiene Windows. La copia archivo a archivo conserva tus datos, pero si necesitas que ese Windows arranque, es más seguro reducir antes la partición en el disco de origen con «Particiones» y clonar después.");
                }
            }
        }

        // GPT → MBR con Windows: la partición EFI pasa a ser la partición activa de arranque BIOS
        if (convert && style == TableStyle.Mbr && plan.RebuildBoot)
        {
            var esp = t.Parts.FirstOrDefault(p => RoleOf(p) == "EFI");
            if (esp != null) { esp.MbrType = 0x0C; esp.Active = true; esp.FormatAs = "FAT32"; plan.Notes.Add("La partición EFI se convierte en partición de arranque activa (FAT32) para BIOS heredado."); }
            else t.Parts.First(p => p.Source == winIdx).Active = true;
        }

        if (relocate && src.Parts.Any(p => p.Offset % MiB != 0) && alignSsd)
            plan.Notes.Add("Hay particiones desalineadas en el origen: en el destino quedarán alineadas a 1 MiB.");
        if (plan.RebuildBoot)
            plan.Notes.Add(plan.Uefi ? "Se reconstruirá el arranque de Windows en modo UEFI (bcdboot)." : "Se reconstruirá el arranque de Windows en modo BIOS heredado (bcdboot).");
        if (plan.RebuildBoot && convert)
            plan.Notes.Add(plan.Uefi ? "Para arrancar desde el disco nuevo, pon la BIOS/firmware en modo UEFI (desactiva CSM/Legacy)."
                                     : "Para arrancar desde el disco nuevo, activa el modo Legacy/CSM en la BIOS.");
        Validate(t, plan.Errors);
        return plan;
    }

    public static uint NewSignature() { Span<byte> b = stackalloc byte[4]; Random.Shared.NextBytes(b); return BinaryPrimitives.ReadUInt32LittleEndian(b) | 1; }

    // ───────────────────────── Desde los datos de Windows (sin administrador, para vista previa) ─────────────────────────
    public static DiskLayout FromWmi(PhysicalDisk d)
    {
        var l = new DiskLayout { Style = d.PartitionStyle == "GPT" ? TableStyle.Gpt : d.PartitionStyle == "MBR" ? TableStyle.Mbr : TableStyle.Raw, DiskSize = d.Size, SectorSize = d.SectorSize };
        int i = 0;
        foreach (var p in d.Partitions.OrderBy(p => p.Offset))
            l.Parts.Add(new PartEntry
            {
                Offset = p.Offset, Length = p.Size, Source = i++, MbrType = p.MbrType, Active = p.IsActive,
                GptType = Guid.TryParse(p.GptType, out var g) ? g : Guid.Empty, Name = p.Type
            });
        return l;
    }

    // ───────────────────────── Copia de seguridad de la tabla ─────────────────────────
    public record TableBackup(string File, int Disk, string Serial, string Model, DateTime Date, TableStyle Style);
    static string BackupDir { get { var d = Path.Combine(Settings.DataDir, "TablasParticiones"); Directory.CreateDirectory(d); return d; } }

    /// <summary>Guarda los 34 primeros y 34 últimos sectores (MBR, GPT y su copia) y la descripción de la tabla.</summary>
    public static string Backup(SafeFileHandle h, DiskLayout l, PhysicalDisk d)
    {
        int n = 34 * l.SectorSize;
        var head = ReadAt(h, 0, n);
        var tail = ReadAt(h, l.DiskSize - n, n);
        string name = $"Disco{d.Number}_{Safe(d.Serial)}_{DateTime.Now:yyyyMMdd-HHmmss}";
        string bin = Path.Combine(BackupDir, name + ".bin");
        using (var fs = File.Create(bin)) { fs.Write(head); fs.Write(tail); }
        File.WriteAllText(Path.Combine(BackupDir, name + ".json"), JsonSerializer.Serialize(new
        {
            Disco = d.Number, Serie = d.Serial, Modelo = d.Title, Fecha = DateTime.Now, Estilo = l.Style.ToString(),
            Tamaño = l.DiskSize, Sector = l.SectorSize, Particiones = l.Parts.Select(p => new { p.Offset, p.Length, p.MbrType, p.Active, GptType = p.GptType.ToString(), p.Name })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"Copia de la tabla de particiones del disco {d.Number} guardada en {bin}");
        return bin;
    }

    public static List<TableBackup> ListBackups(string serial) =>
        Directory.GetFiles(BackupDir, "*.json").Select(f =>
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                return new TableBackup(Path.ChangeExtension(f, ".bin"), r.GetProperty("Disco").GetInt32(), r.GetProperty("Serie").GetString(),
                    r.GetProperty("Modelo").GetString(), r.GetProperty("Fecha").GetDateTime(), Enum.Parse<TableStyle>(r.GetProperty("Estilo").GetString()!));
            }
            catch { return null; }
        }).Where(b => b != null && File.Exists(b.File) && (serial == null || string.Equals(b.Serial, serial, StringComparison.OrdinalIgnoreCase)))
          .OrderByDescending(b => b.Date).ToList();

    public static void Restore(SafeFileHandle h, string bin, DiskLayout current)
    {
        var data = File.ReadAllBytes(bin);
        int n = data.Length / 2;
        if (n != 34 * current.SectorSize) throw new IOException("La copia no corresponde a un disco con este tamaño de sector.");
        WriteAt(h, 0, data.AsSpan(0, n));
        WriteAt(h, current.DiskSize - n, data.AsSpan(n, n));
        Native.Ioctl(h, Native.IOCTL_DISK_UPDATE_PROPERTIES);
    }

    static string Safe(string s) => new((s ?? "").Where(char.IsLetterOrDigit).Take(24).ToArray());

    static byte[] ReadAt(SafeFileHandle h, long off, int n)
    {
        var b = new byte[n];
        int got = RandomAccess.Read(h, b, off);
        if (got != n) throw new IOException("Lectura incompleta del disco");
        return b;
    }

    static void WriteAt(SafeFileHandle h, long off, ReadOnlySpan<byte> data) => RandomAccess.Write(h, data, off);

    // ───────────────────────── Conversión sin pérdida (disco de datos) ─────────────────────────
    public static TransferJob ConvertJob(PhysicalDisk d, TableStyle to) => new()
    {
        Title = $"Convertir disco {d.Number} a {to.ToString().ToUpperInvariant()}",
        Subtitle = $"{d.Title} · sin pérdida de datos",
        Glyph = Icons.Swap,
        Work = ctx => Task.Run(() =>
        {
            if (d.IsSystem || d.IsBoot) throw new InvalidOperationException("Para el disco del sistema se usa MBR2GPT (solo de MBR a GPT).");
            ctx.SetCurrent("Leyendo la tabla de particiones…");
            using var h = Native.OpenRaw(d.Device, true);
            var src = Read(h);
            if (src.Style == to) throw new InvalidOperationException($"El disco ya es {to}.");
            var errors = new List<string>();
            string fsOf(int i) => d.Partitions.FirstOrDefault(p => p.Offset == src.Parts[i].Offset)?.FileSystem;
            var dst = Convert(src, to, fsOf, errors);

            // Si a GPT le falta sitio al final para la copia de la tabla, reducir la última partición 1 MB (NTFS)
            if (to == TableStyle.Gpt && errors.Any(e => e.Contains("final del disco")))
            {
                var last = src.Parts.OrderBy(p => p.End).Last();
                ctx.SetCurrent("Reduciendo 1 MB la última partición para hacer sitio a la tabla GPT…");
                ShrinkPartition(d.Number, last.Offset, last.Length - MiB);
                src = Read(h);
                errors.Clear();
                dst = Convert(src, to, fsOf, errors);
            }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors.Distinct()));

            ctx.SetCurrent("Guardando copia de seguridad de la tabla…");
            Backup(h, src, d);
            ctx.SetCurrent("Desmontando volúmenes…");
            var locks = LockVolumes(d);
            try
            {
                ctx.SetCurrent($"Escribiendo la tabla {to}…");
                CreateEmpty(h, dst);
                if (to == TableStyle.Mbr)
                {
                    // borrar restos de la cabecera GPT (sectores 1-33 y copia final) para que ninguna herramienta los confunda
                    var zero = new byte[33 * src.SectorSize];
                    RandomAccess.Write(h, zero, src.SectorSize);
                    RandomAccess.Write(h, zero, src.DiskSize - zero.Length);
                }
                WriteLayout(h, dst);
            }
            finally { Unlock(locks); }
            ctx.SetTotal(1); ctx.AddBytes(1);
            Log.Ok($"Disco {d.Number} convertido a {to} sin pérdida de datos ({dst.Parts.Count} particiones).");
        })
    };

    public static TransferJob RestoreJob(PhysicalDisk d, string bin) => new()
    {
        Title = $"Restaurar tabla de particiones del disco {d.Number}",
        Subtitle = Path.GetFileName(bin),
        Glyph = Icons.Undo,
        Work = ctx => Task.Run(() =>
        {
            using var h = Native.OpenRaw(d.Device, true);
            var cur = Read(h);
            var locks = LockVolumes(d);
            try { Restore(h, bin, cur); } finally { Unlock(locks); }
            RefreshStorageCache();
            ctx.SetTotal(1); ctx.AddBytes(1);
            Log.Ok($"Tabla de particiones del disco {d.Number} restaurada desde {Path.GetFileName(bin)}");
        })
    };

    /// <summary>Conversión del disco del sistema con la herramienta oficial de Microsoft.</summary>
    public static TransferJob Mbr2GptJob(PhysicalDisk d) => new()
    {
        Title = $"Convertir el disco del sistema a GPT (MBR2GPT)",
        Subtitle = d.Title,
        Glyph = Icons.Swap,
        Work = ctx => Task.Run(() =>
        {
            ctx.SetCurrent("Validando con MBR2GPT…");
            var (code, outp) = RunTool("mbr2gpt.exe", $"/validate /disk:{d.Number} /allowFullOS");
            Log.Info("MBR2GPT /validate: " + outp);
            if (code != 0) throw new InvalidOperationException("MBR2GPT no permite convertir este disco: " + LastLine(outp));
            ctx.SetCurrent("Convirtiendo…");
            (code, outp) = RunTool("mbr2gpt.exe", $"/convert /disk:{d.Number} /allowFullOS");
            Log.Info("MBR2GPT /convert: " + outp);
            if (code != 0) throw new InvalidOperationException("La conversión falló: " + LastLine(outp));
            ctx.SetTotal(1); ctx.AddBytes(1);
            Log.Ok("Disco del sistema convertido a GPT. Antes de reiniciar, cambia la BIOS a modo UEFI.");
        })
    };

    static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? s;

    public static (int code, string output) RunTool(string exe, string args)
    {
        string path = Path.Combine(Environment.SystemDirectory, exe);
        var psi = new ProcessStartInfo(File.Exists(path) ? path : exe, args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.Trim());
    }

    // ───────────────────────── Ayudas de volúmenes (WMI de almacenamiento) ─────────────────────────
    public static ManagementObject FindPartition(int disk, long offset)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk}"));
        foreach (ManagementObject p in s.Get())
            if (System.Convert.ToInt64(p["Offset"]) == offset) return p;
        return null;
    }

    /// <summary>Ruta \\?\Volume{...}\ de la partición, si tiene un volumen montado.</summary>
    public static string VolumePath(int disk, long offset)
    {
        var p = FindPartition(disk, offset);
        if (p?["AccessPaths"] is string[] paths) return paths.FirstOrDefault(a => a.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
        return null;
    }

    public static string DriveLetter(int disk, long offset)
    {
        var p = FindPartition(disk, offset);
        char c = System.Convert.ToChar(p?["DriveLetter"] ?? '\0');
        return c == '\0' ? null : c + ":";
    }

    /// <summary>Asigna una letra libre a la partición y la devuelve ("S:").</summary>
    public static string AssignLetter(int disk, long offset)
    {
        string have = DriveLetter(disk, offset);
        if (have != null) return have;
        var p = FindPartition(disk, offset) ?? throw new IOException("No se encontró la partición en el disco destino.");
        var used = DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])).ToHashSet();
        char free = "ZYXWVUTSRQPONMLKJIHG".First(ch => !used.Contains(ch));
        var args = p.GetMethodParameters("AddAccessPath");
        args["AccessPath"] = free + ":\\";
        p.InvokeMethod("AddAccessPath", args, null);
        for (int i = 0; i < 20 && !Directory.Exists(free + ":\\"); i++) Thread.Sleep(250);
        return free + ":";
    }

    public static void RemoveLetter(int disk, long offset, string letter)
    {
        try
        {
            var p = FindPartition(disk, offset);
            if (p == null) return;
            var args = p.GetMethodParameters("RemoveAccessPath");
            args["AccessPath"] = letter.TrimEnd('\\') + "\\";
            p.InvokeMethod("RemoveAccessPath", args, null);
        }
        catch { }
    }

    static void ShrinkPartition(int disk, long offset, long newSize)
    {
        var p = FindPartition(disk, offset) ?? throw new IOException("No se encontró la última partición.");
        var args = p.GetMethodParameters("Resize");
        args["Size"] = (ulong)(newSize / MiB * MiB);
        var r = p.InvokeMethod("Resize", args, null);
        uint code = System.Convert.ToUInt32(r?["ReturnValue"] ?? 0u);
        if (code != 0) throw new IOException($"No se pudo reducir la última partición (código {code}). Solo es posible con NTFS.");
    }

    public static List<SafeFileHandle> LockVolumes(PhysicalDisk d)
    {
        var list = new List<SafeFileHandle>();
        foreach (var p in d.Partitions)
        {
            string vp = VolumePath(d.Number, p.Offset);
            if (vp == null) continue;
            try
            {
                var h = Native.OpenRaw(vp.TrimEnd('\\'), true);
                Native.Ioctl(h, Native.FSCTL_LOCK_VOLUME);
                Native.Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME);
                list.Add(h);
            }
            catch (Exception ex) { Log.Warn($"No se pudo desmontar {vp}: {ex.Message}"); }
        }
        return list;
    }

    public static void Unlock(List<SafeFileHandle> locks)
    {
        foreach (var h in locks) { try { Native.Ioctl(h, Native.FSCTL_UNLOCK_VOLUME); } catch { } h.Dispose(); }
    }
}
