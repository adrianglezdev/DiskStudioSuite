using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;

namespace DiskStudio.Core;

public enum HealthLevel { Unknown, Good, Caution, Bad }

public class VolumeInfo
{
    public string Root { get; set; } = "";
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public DriveType Type { get; set; }
    public bool Ready { get; set; }
    public long Total { get; set; }
    public long Free { get; set; }
    public int DiskNumber { get; set; } = -1;
    public string DiskModel { get; set; } = "";
    public string Bus { get; set; } = "";
    public bool OnRemovableDisk { get; set; }
    public uint ClusterSize { get; set; }

    public double Used => Total > 0 ? (double)(Total - Free) / Total : 0;
    public bool IsRemovable => Type == DriveType.Removable || OnRemovableDisk;
    public bool IsSystem => string.Equals(Root, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);
    public string Name => string.IsNullOrEmpty(Label) ? KindText : Label;

    public string KindText => Type switch
    {
        DriveType.Fixed => IsRemovable ? "Disco externo" : "Disco local",
        DriveType.Removable => "Unidad extraíble",
        DriveType.Network => "Unidad de red",
        DriveType.CDRom => "CD/DVD",
        DriveType.Ram => "Disco RAM",
        _ => "Unidad"
    };

    public string Glyph => Type switch
    {
        DriveType.Network => Icons.Network,
        DriveType.CDRom => Icons.Cd,
        DriveType.Removable => Icons.Usb,
        _ => IsRemovable ? Icons.Usb : IsSystem ? Icons.DriveSystem : Icons.Drive
    };

    public override string ToString() => $"{Letter}  {Name}";
}

public class PartitionInfo
{
    public int Number;
    public long Offset, Size;
    public string Type = "", Letter = "", Label = "", FileSystem = "";
    public long Free;
    public bool IsSystem, IsBoot, IsHidden, IsActive;
    public byte MbrType;
    public string GptType = "";
    public string Display => !string.IsNullOrEmpty(Letter) ? $"{Letter} {Label}".Trim()
                           : IsSystem ? "Sistema EFI" : Type.Contains("Reserv", StringComparison.OrdinalIgnoreCase) || Type == "Reserved" ? "Reservada" : Type == "Recovery" ? "Recuperación" : "Partición " + Number;
}

public class SmartAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Value { get; set; }
    public int Worst { get; set; }
    public int Threshold { get; set; }
    public string Raw { get; set; } = "";
    public bool Warn { get; set; }
}

public class PhysicalDisk
{
    public int Number;
    public string Model = "", Serial = "", Bus = "", Media = "", PartitionStyle = "", Firmware = "";
    public long Size;
    public int SectorSize = 512;
    public bool IsSystem, IsBoot, IsRemovable;
    public HealthLevel Health = HealthLevel.Unknown;
    public string HealthText = "Desconocida";
    public int? Temperature, Wear, HealthPercent;
    public long? PowerOnHours, PowerCycles, ReadErrors, WriteErrors;
    public bool SmartFromSmartctl;
    public List<SmartAttribute> Attributes = [];
    public List<PartitionInfo> Partitions = [];
    public List<VolumeInfo> Volumes = [];

    public string Device => @"\\.\PhysicalDrive" + Number;
    public string Title => string.IsNullOrWhiteSpace(Model) ? "Disco " + Number : Model;
    public string Glyph => IsRemovable ? Icons.Usb : Media == "SSD" ? Icons.Ssd : Icons.Hdd;
}

public static class DiskScanner
{
    public static List<VolumeInfo> ScanVolumes()
    {
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            var v = new VolumeInfo { Root = d.Name, Letter = d.Name.TrimEnd('\\'), Type = d.DriveType };
            try
            {
                v.Ready = d.IsReady;
                if (v.Ready)
                {
                    v.Label = d.VolumeLabel; v.FileSystem = d.DriveFormat;
                    v.Total = d.TotalSize; v.Free = d.AvailableFreeSpace;
                    if (Native.GetDiskFreeSpace(v.Root, out uint spc, out uint bps, out _, out _)) v.ClusterSize = spc * bps;
                }
            }
            catch { v.Ready = false; }
            list.Add(v);
        }
        return list;
    }

    /// <summary>Escaneo completo: volúmenes + discos físicos con particiones y salud.</summary>
    public static (List<PhysicalDisk> disks, List<VolumeInfo> volumes) Scan(bool readSmart)
    {
        var vols = ScanVolumes();
        var byLetter = vols.ToDictionary(v => v.Letter.TrimEnd(':').ToUpperInvariant(), v => v);
        var disks = new List<PhysicalDisk>();

        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            var phys = new Dictionary<string, ManagementObject>();
            foreach (ManagementObject p in Query(scope, "SELECT * FROM MSFT_PhysicalDisk"))
                phys[S(p["DeviceId"])] = p;

            foreach (ManagementObject d in Query(scope, "SELECT * FROM MSFT_Disk"))
            {
                var pd = new PhysicalDisk
                {
                    Number = Convert.ToInt32(d["Number"]),
                    Model = S(d["FriendlyName"]).Trim(),
                    Serial = S(d["SerialNumber"]).Trim(),
                    Firmware = S(d["FirmwareVersion"]).Trim(),
                    Size = Convert.ToInt64(d["Size"] ?? 0L),
                    SectorSize = Convert.ToInt32(d["LogicalSectorSize"] ?? 512),
                    IsSystem = d["IsSystem"] is true,
                    IsBoot = d["IsBoot"] is true,
                    Bus = BusName(Convert.ToInt32(d["BusType"] ?? 0)),
                    PartitionStyle = Convert.ToInt32(d["PartitionStyle"] ?? 0) switch { 1 => "MBR", 2 => "GPT", _ => "Sin inicializar" }
                };
                pd.IsRemovable = pd.Bus is "USB" or "SD" or "MMC" or "1394";
                int health = Convert.ToInt32(d["HealthStatus"] ?? 0);

                if (phys.TryGetValue(pd.Number.ToString(), out var p))
                {
                    string pbus = BusName(Convert.ToInt32(p["BusType"] ?? 0));
                    if (pd.Bus is "RAID" or "Desconocido" && pbus is not ("RAID" or "Desconocido")) pd.Bus = pbus;
                    pd.Media = Convert.ToInt32(p["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                    if (pd.Media == "" && Convert.ToInt32(p["SpindleSpeed"] ?? 0) == 0 && pd.Bus == "NVMe") pd.Media = "SSD";
                    health = Math.Max(health, Convert.ToInt32(p["HealthStatus"] ?? 0));
                    if (readSmart) ReadReliability(p, pd);
                }
                if (pd.Bus == "NVMe" && pd.Media == "") pd.Media = "SSD";
                pd.Health = health switch { 0 => HealthLevel.Good, 1 => HealthLevel.Caution, 2 => HealthLevel.Bad, _ => HealthLevel.Unknown };

                disks.Add(pd);
            }

            // Particiones y volúmenes
            foreach (ManagementObject part in Query(scope, "SELECT * FROM MSFT_Partition"))
            {
                int dn = Convert.ToInt32(part["DiskNumber"]);
                var pd = disks.Find(x => x.Number == dn);
                if (pd == null) continue;
                var pi = new PartitionInfo
                {
                    Number = Convert.ToInt32(part["PartitionNumber"]),
                    Offset = Convert.ToInt64(part["Offset"] ?? 0L),
                    Size = Convert.ToInt64(part["Size"] ?? 0L),
                    IsSystem = part["IsSystem"] is true,
                    IsBoot = part["IsBoot"] is true,
                    IsHidden = part["IsHidden"] is true,
                    IsActive = part["IsActive"] is true,
                    MbrType = (byte)Convert.ToInt32(part["MbrType"] ?? 0),
                    GptType = S(part["GptType"]),
                    Type = PartType(part)
                };
                char letter = Convert.ToChar(part["DriveLetter"] ?? '\0');
                if (letter != '\0' && byLetter.TryGetValue(letter.ToString().ToUpperInvariant(), out var v))
                {
                    pi.Letter = v.Letter; pi.Label = v.Label; pi.FileSystem = v.FileSystem; pi.Free = v.Free;
                    v.DiskNumber = pd.Number; v.DiskModel = pd.Model; v.Bus = pd.Bus; v.OnRemovableDisk = pd.IsRemovable;
                    pd.Volumes.Add(v);
                }
                pd.Partitions.Add(pi);
            }
            foreach (var pd in disks) pd.Partitions.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }
        catch
        {
            FallbackWin32(disks, byLetter);
        }

        if (readSmart && Native.IsAdmin && SmartCtl.Available)
            foreach (var pd in disks) SmartCtl.Read(pd);

        foreach (var pd in disks) Assess(pd);
        disks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return (disks, vols);
    }

    static void ReadReliability(ManagementObject p, PhysicalDisk pd)
    {
        try
        {
            foreach (ManagementObject r in p.GetRelated("MSFT_StorageReliabilityCounter"))
            {
                int t = Convert.ToInt32(r["Temperature"] ?? 0);
                if (t > 0) pd.Temperature = t;
                int w = Convert.ToInt32(r["Wear"] ?? 0);
                if (w > 0) pd.Wear = w;
                long h = Convert.ToInt64(r["PowerOnHours"] ?? 0L);
                if (h > 0) pd.PowerOnHours = h;
                long c = Convert.ToInt64(r["StartStopCycleCount"] ?? 0L);
                if (c > 0) pd.PowerCycles = c;
                pd.ReadErrors = Convert.ToInt64(r["ReadErrorsUncorrected"] ?? r["ReadErrorsTotal"] ?? 0L);
                pd.WriteErrors = Convert.ToInt64(r["WriteErrorsUncorrected"] ?? r["WriteErrorsTotal"] ?? 0L);
            }
        }
        catch { /* requiere administrador */ }
    }

    static void Assess(PhysicalDisk pd)
    {
        int pct = 100;
        var level = pd.Health;
        foreach (var a in pd.Attributes)
        {
            long.TryParse(a.Raw.Split(' ')[0], out long raw);
            if (a.Id is 5 or 196 or 197 or 198 && raw > 0)
            {
                a.Warn = true;
                pct = Math.Min(pct, (int)Math.Max(10, 90 - raw));
                if (level == HealthLevel.Good) level = HealthLevel.Caution;
            }
            if (a.Threshold > 0 && a.Value <= a.Threshold) { a.Warn = true; level = HealthLevel.Bad; pct = Math.Min(pct, 5); }
        }
        if (pd.Wear is int wear) pct = Math.Min(pct, 100 - wear);
        if (pd.ReadErrors > 0 || pd.WriteErrors > 0) { if (level == HealthLevel.Good) level = HealthLevel.Caution; pct = Math.Min(pct, 70); }
        if (pd.Temperature is > 60 && level == HealthLevel.Good) level = HealthLevel.Caution;
        if (level == HealthLevel.Bad) pct = Math.Min(pct, 20);
        pd.Health = level;
        pd.HealthPercent = level == HealthLevel.Unknown ? null : pct;
        pd.HealthText = level switch
        {
            HealthLevel.Good => "Buena",
            HealthLevel.Caution => "Precaución",
            HealthLevel.Bad => "Mala",
            _ => "Desconocida"
        };
    }

    static void FallbackWin32(List<PhysicalDisk> disks, Dictionary<string, VolumeInfo> byLetter)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
            foreach (ManagementObject disk in s.Get())
            {
                var pd = new PhysicalDisk
                {
                    Number = Convert.ToInt32(disk["Index"]),
                    Model = S(disk["Model"]).Trim(),
                    Serial = S(disk["SerialNumber"]).Trim(),
                    Bus = S(disk["InterfaceType"]).Trim(),
                    Size = Convert.ToInt64(disk["Size"] ?? 0L)
                };
                pd.IsRemovable = pd.Bus == "USB";
                foreach (ManagementObject part in disk.GetRelated("Win32_DiskPartition"))
                {
                    var pi = new PartitionInfo { Number = Convert.ToInt32(part["Index"]) + 1, Size = Convert.ToInt64(part["Size"] ?? 0L), Offset = Convert.ToInt64(part["StartingOffset"] ?? 0L), Type = S(part["Type"]) };
                    foreach (ManagementObject ld in part.GetRelated("Win32_LogicalDisk"))
                        if (byLetter.TryGetValue(S(ld["DeviceID"]).TrimEnd(':'), out var v))
                        {
                            pi.Letter = v.Letter; pi.Label = v.Label; pi.FileSystem = v.FileSystem; pi.Free = v.Free;
                            v.DiskNumber = pd.Number; v.DiskModel = pd.Model; v.Bus = pd.Bus; v.OnRemovableDisk = pd.IsRemovable;
                            pd.Volumes.Add(v);
                        }
                    pd.Partitions.Add(pi);
                }
                disks.Add(pd);
            }
        }
        catch { }
    }

    static string PartType(ManagementObject part)
    {
        string gpt = S(part["GptType"]).ToLowerInvariant();
        return gpt switch
        {
            "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}" => "EFI",
            "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" => "Reserved",
            "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}" => "Recovery",
            "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}" => "Basic",
            "" => Convert.ToInt32(part["MbrType"] ?? 0) switch { 7 => "Basic", 0x27 => "Recovery", 0xEF => "EFI", _ => "Basic" },
            _ => "Other"
        };
    }

    static ManagementObjectCollection Query(ManagementScope scope, string q) =>
        new ManagementObjectSearcher(scope, new ObjectQuery(q)).Get();

    static string BusName(int b) => b switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "Virtual", 16 => "Storage Spaces", 17 => "NVMe",
        _ => "Desconocido"
    };

    static string S(object o) => o?.ToString() ?? "";
}

/// <summary>Lectura SMART completa mediante smartctl (smartmontools), si está instalado.</summary>
public static class SmartCtl
{
    static readonly string[] Candidates =
    [
        @"C:\Program Files\smartmontools\bin\smartctl.exe",
        @"C:\Program Files (x86)\smartmontools\bin\smartctl.exe",
        Path.Combine(AppContext.BaseDirectory, "smartctl.exe")
    ];

    public static string Exe => Candidates.FirstOrDefault(File.Exists);
    public static bool Available => Exe != null;

    public static void Read(PhysicalDisk pd)
    {
        try
        {
            var psi = new ProcessStartInfo(Exe, $"-j -a /dev/pd{pd.Number}")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("smart_status", out var st) && st.TryGetProperty("passed", out var passed))
            {
                pd.SmartFromSmartctl = true;
                if (!passed.GetBoolean()) pd.Health = HealthLevel.Bad;
                else if (pd.Health == HealthLevel.Unknown) pd.Health = HealthLevel.Good;
            }
            if (r.TryGetProperty("temperature", out var t) && t.TryGetProperty("current", out var tc)) pd.Temperature = tc.GetInt32();
            if (r.TryGetProperty("power_on_time", out var pot) && pot.TryGetProperty("hours", out var h)) pd.PowerOnHours = h.GetInt64();
            if (r.TryGetProperty("power_cycle_count", out var pc)) pd.PowerCycles = pc.GetInt64();
            if (r.TryGetProperty("firmware_version", out var fw)) pd.Firmware = fw.GetString();
            if (r.TryGetProperty("rotation_rate", out var rr) && rr.ValueKind == JsonValueKind.Number)
                pd.Media = rr.GetInt32() == 0 ? "SSD" : "HDD";

            if (r.TryGetProperty("ata_smart_attributes", out var ata) && ata.TryGetProperty("table", out var table))
            {
                pd.Attributes.Clear();
                foreach (var a in table.EnumerateArray())
                {
                    pd.Attributes.Add(new SmartAttribute
                    {
                        Id = a.GetProperty("id").GetInt32(),
                        Name = a.GetProperty("name").GetString()?.Replace('_', ' ') ?? "",
                        Value = a.GetProperty("value").GetInt32(),
                        Worst = a.GetProperty("worst").GetInt32(),
                        Threshold = a.TryGetProperty("thresh", out var th) ? th.GetInt32() : 0,
                        Raw = a.GetProperty("raw").GetProperty("string").GetString() ?? ""
                    });
                    if (a.GetProperty("id").GetInt32() is 231 or 233 && pd.Wear == null && pd.Media == "SSD")
                        pd.Wear = Math.Clamp(100 - a.GetProperty("value").GetInt32(), 0, 100);
                }
            }
            if (r.TryGetProperty("nvme_smart_health_information_log", out var nv))
            {
                pd.Attributes.Clear();
                foreach (var prop in nv.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                    pd.Attributes.Add(new SmartAttribute { Id = 0, Name = NvmeName(prop.Name), Raw = prop.Value.ToString(), Warn = (prop.Name is "critical_warning" or "media_errors") && prop.Value.GetInt64() > 0 });
                }
                if (nv.TryGetProperty("percentage_used", out var pu)) pd.Wear = pu.GetInt32();
                if (nv.TryGetProperty("critical_warning", out var cw) && cw.GetInt32() > 0) pd.Health = HealthLevel.Bad;
            }
        }
        catch { }
    }

    static string NvmeName(string n) => n switch
    {
        "critical_warning" => "Aviso crítico",
        "temperature" => "Temperatura",
        "available_spare" => "Reserva disponible (%)",
        "available_spare_threshold" => "Umbral de reserva (%)",
        "percentage_used" => "Desgaste (%)",
        "data_units_read" => "Unidades leídas",
        "data_units_written" => "Unidades escritas",
        "host_reads" => "Lecturas del host",
        "host_writes" => "Escrituras del host",
        "controller_busy_time" => "Tiempo ocupado",
        "power_cycles" => "Ciclos de encendido",
        "power_on_hours" => "Horas encendido",
        "unsafe_shutdowns" => "Apagados inseguros",
        "media_errors" => "Errores de medio",
        "num_err_log_entries" => "Entradas de error",
        _ => n.Replace('_', ' ')
    };
}
