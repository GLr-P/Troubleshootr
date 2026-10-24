namespace Troubleshootr.Core;

/// <summary>
/// Raw facts about one PC, filled by collectors and read by checks. Property names are the section names that
/// checks list in <see cref="ICheck.Needs"/> and collectors fill in <see cref="ICollector.Section"/>.
/// Keep it plain data: it's serialized to JSON for fixtures and reports.
/// </summary>
public sealed class Snapshot
{
    public SnapshotMeta Meta { get; set; } = new();

    public Section<SystemInfo>? System { get; set; }
    public Section<CpuInfo>? Cpu { get; set; }
    public Section<MemoryInfo>? Memory { get; set; }
    public Section<BoardInfo>? Board { get; set; }
    public Section<List<VolumeInfo>>? Volumes { get; set; }
    public Section<List<GpuInfo>>? Gpus { get; set; }
    public Section<StorageInfo>? Storage { get; set; }

    public static readonly IReadOnlyList<string> SectionNames = typeof(Snapshot).GetProperties()
        .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Section<>))
        .Select(p => p.Name).ToList();

    /// <summary>Copies every section the other snapshot collected over this one (a category scan adds to what's known).</summary>
    public void MergeFrom(Snapshot other)
    {
        foreach (var name in SectionNames)
        {
            var prop = typeof(Snapshot).GetProperty(name)!;
            if (prop.GetValue(other) is { } section) prop.SetValue(this, section);
        }
        Meta = other.Meta;
    }

    /// <summary>Null if the section has a value; otherwise why it doesn't.</summary>
    public string? WhyMissing(string section)
    {
        var prop = typeof(Snapshot).GetProperty(section) ?? throw new ArgumentException($"Unknown section {section}");
        var value = prop.GetValue(this);
        if (value is null) return "not collected in this scan";
        var unavailable = (string?)prop.PropertyType.GetProperty(nameof(Section<object>.Unavailable))!.GetValue(value);
        var hasValue = prop.PropertyType.GetProperty(nameof(Section<object>.Value))!.GetValue(value) is not null;
        return hasValue ? null : unavailable ?? "no data";
    }
}

public sealed class SnapshotMeta
{
    public string ToolVersion { get; set; } = "";
    public DateTime CollectedUtc { get; set; }
    public bool IsAdmin { get; set; }
    public bool Anonymized { get; set; }
    /// <summary>What the fixture shows (set by hand on fixtures).</summary>
    public string? Note { get; set; }
}

public sealed class SystemInfo
{
    [Sensitive] public string? DeviceName { get; set; }
    [Sensitive] public string? UserName { get; set; }
    [Sensitive] public string? Domain { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    [Sensitive] public string? SerialNumber { get; set; }
    /// <summary>Win32_ComputerSystem.PCSystemType: 1 desktop, 2 mobile, 3 workstation...</summary>
    public int? PcSystemType { get; set; }
    public bool? HasBattery { get; set; }

    public string? WindowsProductName { get; set; }
    public string? WindowsDisplayVersion { get; set; }
    public string? EditionId { get; set; }
    public int Build { get; set; }
    public int Ubr { get; set; }
    public int MajorVersion { get; set; }
    public int MinorVersion { get; set; }
    public DateTime? InstallDateUtc { get; set; }
    public bool Is64BitOs { get; set; }
    public string? Architecture { get; set; }

    public long UptimeSeconds { get; set; }
    public bool? FastStartupEnabled { get; set; }
    public List<string> PendingRebootReasons { get; set; } = new();
    public const string PendingFileRenames = "file operations waiting for a restart";

    public bool IsLaptop => PcSystemType == 2 || HasBattery == true;

    /// <summary>Hyper-V, VMware, VirtualBox, QEMU/KVM, Parallels, Xen: hardware checks (RAM sticks, BIOS age…) don't apply.</summary>
    public bool IsVirtualMachine =>
        $"{Manufacturer} {Model}" is var m && new[] { "Virtual Machine", "VMware", "VirtualBox", "innotek", "QEMU", "KVM", "Parallels", "Xen", "Bochs" }
            .Any(k => m.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
}

public sealed class CpuInfo
{
    public List<CpuPackage> Packages { get; set; } = new();
}

public sealed class CpuPackage
{
    public string? Name { get; set; }
    public string? Manufacturer { get; set; }
    public string? Identifier { get; set; }
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxClockMhz { get; set; }
    public int CurrentClockMhz { get; set; }
    public string? Socket { get; set; }
    public int L2KB { get; set; }
    public int L3KB { get; set; }
    public bool? VirtualizationFirmwareEnabled { get; set; }
    /// <summary>Hex, from the registry "Update Revision".</summary>
    public string? Microcode { get; set; }
}

public sealed class MemoryInfo
{
    /// <summary>RAM physically installed (from the firmware), MB.</summary>
    public long? InstalledMB { get; set; }
    /// <summary>RAM Windows can use, MB.</summary>
    public long UsableMB { get; set; }
    public long AvailableMB { get; set; }
    public long CommitUsedMB { get; set; }
    public long CommitLimitMB { get; set; }
    public int? SlotCount { get; set; }
    public long? MaxCapacityMB { get; set; }
    public List<MemoryModule> Modules { get; set; } = new();
}

public sealed class MemoryModule
{
    public long CapacityMB { get; set; }
    /// <summary>Speed the module reports (SMBIOS), MT/s.</summary>
    public int? SpeedMts { get; set; }
    /// <summary>Speed it actually runs at, MT/s.</summary>
    public int? ConfiguredSpeedMts { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    [Sensitive] public string? SerialNumber { get; set; }
    public string? BankLabel { get; set; }
    public string? Locator { get; set; }
    /// <summary>DDR3, DDR4, DDR5, LPDDR4…</summary>
    public string? Type { get; set; }
    /// <summary>DIMM, SODIMM…</summary>
    public string? FormFactor { get; set; }
}

public sealed class BoardInfo
{
    public string? BoardManufacturer { get; set; }
    public string? BoardProduct { get; set; }
    public string? BoardVersion { get; set; }
    [Sensitive] public string? BoardSerial { get; set; }
    public string? BiosVendor { get; set; }
    public string? BiosVersion { get; set; }
    public DateTime? BiosDate { get; set; }
    /// <summary>"UEFI" or "Legacy BIOS".</summary>
    public string? FirmwareType { get; set; }
    public bool? SecureBoot { get; set; }
}

public sealed class VolumeInfo
{
    /// <summary>"C:\".</summary>
    public string Root { get; set; } = "";
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    /// <summary>Fixed, Removable, Network, CDRom…</summary>
    public string? DriveType { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsSystem { get; set; }
    /// <summary>NTFS "dirty" flag: Windows will run chkdsk at the next boot. Null if it couldn't be read.</summary>
    public bool? Dirty { get; set; }
}

public sealed class GpuInfo
{
    public string? Name { get; set; }
    public long? VramBytes { get; set; }
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    public string? PnpDeviceId { get; set; }
    public int? ResolutionWidth { get; set; }
    public int? ResolutionHeight { get; set; }
    public int? RefreshHz { get; set; }
    public string? Status { get; set; }
    public int? ErrorCode { get; set; }
}
