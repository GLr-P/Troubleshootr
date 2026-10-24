using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

public sealed class CpuCollector : Collector<CpuInfo>
{
    public override string Section => nameof(Snapshot.Cpu);

    protected override CpuInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var rows = Wmi.Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, " +
                             "SocketDesignation, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled FROM Win32_Processor");
        var info = new CpuInfo();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var regPath = $@"HARDWARE\DESCRIPTION\System\CentralProcessor\{i}";
            info.Packages.Add(new CpuPackage
            {
                Name = Reg.Str(RegistryHive.LocalMachine, regPath, "ProcessorNameString") ?? r.Str("Name"),
                Manufacturer = r.Str("Manufacturer"),
                Identifier = Reg.Str(RegistryHive.LocalMachine, regPath, "Identifier"),
                Cores = r.Int("NumberOfCores") ?? 0,
                Threads = r.Int("NumberOfLogicalProcessors") ?? 0,
                MaxClockMhz = r.Int("MaxClockSpeed") ?? 0,
                CurrentClockMhz = r.Int("CurrentClockSpeed") ?? 0,
                Socket = r.Str("SocketDesignation"),
                L2KB = r.Int("L2CacheSize") ?? 0,
                L3KB = r.Int("L3CacheSize") ?? 0,
                VirtualizationFirmwareEnabled = r.Bool("VirtualizationFirmwareEnabled"),
                Microcode = Microcode(Reg.Get(RegistryHive.LocalMachine, regPath, "Update Revision")),
            });
        }
        return info;
    }

    /// <summary>"Update Revision" is 8 bytes; Intel keeps the revision in the high dword, AMD in the low one.</summary>
    private static string? Microcode(object? raw)
    {
        if (raw is not byte[] { Length: 8 } b) return null;
        var high = BitConverter.ToUInt32(b, 4);
        var low = BitConverter.ToUInt32(b, 0);
        var rev = high != 0 ? high : low;
        return rev == 0 ? null : $"0x{rev:X}";
    }
}

public sealed class MemoryCollector : Collector<MemoryInfo>
{
    public override string Section => nameof(Snapshot.Memory);

    protected override MemoryInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var status = Native.MemoryStatus();
        var info = new MemoryInfo
        {
            InstalledMB = Native.InstalledMemoryKB() is { } kb ? (long)(kb / 1024) : null,
            UsableMB = status is { } s ? (long)(s.ullTotalPhys / 1048576) : 0,
            AvailableMB = status is { } a ? (long)(a.ullAvailPhys / 1048576) : 0,
            CommitLimitMB = status is { } c ? (long)(c.ullTotalPageFile / 1048576) : 0,
            CommitUsedMB = status is { } u ? (long)((u.ullTotalPageFile - u.ullAvailPageFile) / 1048576) : 0,
        };
        var arrays = Wmi.Query("SELECT MemoryDevices, MaxCapacityEx, MaxCapacity, Use FROM Win32_PhysicalMemoryArray");
        // Use = 3 is system memory (other arrays are flash, video, etc.).
        var system = arrays.Where(a => a.Int("Use") is null or 3).ToList();
        if (system.Count > 0)
        {
            info.SlotCount = system.Sum(a => a.Int("MemoryDevices") ?? 0);
            var maxKb = system.Sum(a => a.Long("MaxCapacityEx") ?? a.Long("MaxCapacity") ?? 0);
            info.MaxCapacityMB = maxKb > 0 ? maxKb / 1024 : null;
        }
        // SMBIOSMemoryType and ConfiguredClockSpeed don't exist on Windows 7; fall back to the basic column set.
        var modules = Wmi.QueryOptional("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SerialNumber, BankLabel, " +
                                        "DeviceLocator, SMBIOSMemoryType, MemoryType, FormFactor FROM Win32_PhysicalMemory");
        if (modules.Count == 0)
            modules = Wmi.Query("SELECT Capacity, Speed, Manufacturer, PartNumber, SerialNumber, BankLabel, DeviceLocator, MemoryType, FormFactor FROM Win32_PhysicalMemory");
        foreach (var m in modules)
        {
            info.Modules.Add(new MemoryModule
            {
                CapacityMB = (m.Long("Capacity") ?? 0) / 1048576,
                SpeedMts = NonZero(m.Int("Speed")),
                ConfiguredSpeedMts = NonZero(m.Int("ConfiguredClockSpeed")),
                Manufacturer = m.Str("Manufacturer"),
                PartNumber = m.Str("PartNumber"),
                SerialNumber = m.Str("SerialNumber"),
                BankLabel = m.Str("BankLabel"),
                Locator = m.Str("DeviceLocator"),
                Type = MemoryType(m.Int("SMBIOSMemoryType") ?? m.Int("MemoryType")),
                FormFactor = m.Int("FormFactor") switch { 8 => "DIMM", 12 => "SODIMM", 0 or null => null, var f => $"form factor {f}" },
            });
        }
        return info;
    }

    private static int? NonZero(int? v) => v is null or 0 ? null : v;

    /// <summary>SMBIOS memory type codes (DMTF SMBIOS spec, type 17).</summary>
    private static string? MemoryType(int? code) => code switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3",
        30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => null,
    };
}

public sealed class BoardCollector : Collector<BoardInfo>
{
    public override string Section => nameof(Snapshot.Board);

    protected override BoardInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var board = Wmi.Query("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        var secureBoot = Reg.Num(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        return new BoardInfo
        {
            BoardManufacturer = board?.Str("Manufacturer"),
            BoardProduct = board?.Str("Product"),
            BoardVersion = board?.Str("Version"),
            BoardSerial = board?.Str("SerialNumber"),
            BiosVendor = bios?.Str("Manufacturer"),
            BiosVersion = bios?.Str("SMBIOSBIOSVersion"),
            BiosDate = Wmi.ParseCimDate(bios?.Str("ReleaseDate")),
            FirmwareType = Native.FirmwareType(),
            SecureBoot = secureBoot is { } v ? v == 1 : null,
        };
    }
}

public sealed class VolumeCollector : Collector<List<VolumeInfo>>
{
    public override string Section => nameof(Snapshot.Volumes);

    protected override List<VolumeInfo> Collect(CollectContext ctx, CancellationToken ct)
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            // Network and optical drives can hang or have no media; skip them in the volume list.
            if (d.DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
            try
            {
                if (!d.IsReady) continue;
                list.Add(new VolumeInfo
                {
                    Root = d.Name,
                    Label = string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel,
                    FileSystem = d.DriveFormat,
                    DriveType = d.DriveType.ToString(),
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.AvailableFreeSpace,
                    IsSystem = string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                    Dirty = d.DriveType == DriveType.Fixed ? DeviceIo.VolumeDirty(d.Name) : null,
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }
}

public sealed class GpuCollector : Collector<List<GpuInfo>>
{
    // Display adapter class key. HardwareInformation.qwMemorySize has the real VRAM; WMI AdapterRAM caps at 4 GB.
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public override string Section => nameof(Snapshot.Gpus);

    protected override List<GpuInfo> Collect(CollectContext ctx, CancellationToken ct)
    {
        var rows = Wmi.Query("SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID, CurrentHorizontalResolution, " +
                             "CurrentVerticalResolution, CurrentRefreshRate, Status, ConfigManagerErrorCode FROM Win32_VideoController");
        var vram = RegistryVram();
        return rows.Select(r => new GpuInfo
        {
            Name = r.Str("Name"),
            VramBytes = r.Str("Name") is { } n && vram.TryGetValue(n, out var bytes) ? bytes : r.Long("AdapterRAM"),
            DriverVersion = r.Str("DriverVersion"),
            DriverDate = Wmi.ParseCimDate(r.Str("DriverDate")),
            PnpDeviceId = r.Str("PNPDeviceID"),
            ResolutionWidth = r.Int("CurrentHorizontalResolution"),
            ResolutionHeight = r.Int("CurrentVerticalResolution"),
            RefreshHz = r.Int("CurrentRefreshRate"),
            Status = r.Str("Status"),
            ErrorCode = r.Int("ConfigManagerErrorCode"),
        }).ToList();
    }

    private static Dictionary<string, long> RegistryVram()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in Reg.SubKeys(RegistryHive.LocalMachine, DisplayClass).Where(k => k.Length == 4 && k.All(char.IsDigit)))
        {
            var path = $@"{DisplayClass}\{sub}";
            var name = Reg.Str(RegistryHive.LocalMachine, path, "DriverDesc");
            var size = Reg.Get(RegistryHive.LocalMachine, path, "HardwareInformation.qwMemorySize") switch
            {
                long l => l,
                byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
                byte[] { Length: 4 } b => BitConverter.ToUInt32(b, 0),
                int i => (uint)i,
                _ => 0L,
            };
            if (name is not null && size > 0) result[name] = size;
        }
        return result;
    }
}
