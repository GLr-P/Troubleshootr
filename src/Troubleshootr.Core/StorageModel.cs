namespace Troubleshootr.Core;

public sealed class StorageInfo
{
    public List<DiskInfo> Disks { get; set; } = new();
    public List<PartitionInfo> Partitions { get; set; } = new();
    /// <summary>Storage Spaces / RAID virtual disks (Windows 8+).</summary>
    public List<VirtualDiskInfo> VirtualDisks { get; set; } = new();
    /// <summary>NTFS DisableDeleteNotify = 1 means TRIM is off for every SSD.</summary>
    public bool? TrimDisabled { get; set; }
}

public sealed class DiskInfo
{
    /// <summary>Disk number (\\.\PhysicalDriveN).</summary>
    public int Number { get; set; }
    public string? Model { get; set; }
    [Sensitive] public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    /// <summary>NVMe, SATA, ATA, USB, SCSI, SAS, RAID, SD, MMC, Virtual…</summary>
    public string? BusType { get; set; }
    /// <summary>SSD, HDD or Unknown.</summary>
    public string? MediaType { get; set; }
    public long SizeBytes { get; set; }
    public int? SpindleRpm { get; set; }
    public string? PnpDeviceId { get; set; }
    /// <summary>Holds the Windows partition.</summary>
    public bool IsBoot { get; set; }
    public List<string> DriveLetters { get; set; } = new();
    /// <summary>Windows Storage health: Healthy, Warning, Unhealthy, Unknown.</summary>
    public string? Health { get; set; }
    public List<string> OperationalStatus { get; set; } = new();

    public AtaSmart? Ata { get; set; }
    public NvmeHealth? Nvme { get; set; }
    public ReliabilityInfo? Reliability { get; set; }
    /// <summary>Why SMART/NVMe health couldn't be read ("needs admin", "USB bridge hides it"…).</summary>
    public string? HealthDataUnavailable { get; set; }

    public string Name => $"Disk {Number} ({Model ?? "unknown model"})";
    public bool IsSsd => MediaType == "SSD" || BusType == "NVMe";
}

public sealed class AtaSmart
{
    public bool? PredictFailure { get; set; }
    public List<SmartAttribute> Attributes { get; set; } = new();

    public SmartAttribute? this[int id] => Attributes.FirstOrDefault(a => a.Id == id);
}

public sealed class SmartAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Normalized value (usually 100 or 200 = new, falling towards the threshold).</summary>
    public int Current { get; set; }
    public int Worst { get; set; }
    /// <summary>0 = no threshold (informational attribute).</summary>
    public int Threshold { get; set; }
    /// <summary>Raw value, decoded per attribute (count, hours, °C…).</summary>
    public long Raw { get; set; }
    public string RawHex { get; set; } = "";

    public bool BelowThreshold => Threshold > 0 && Current > 0 && Current <= Threshold;
}

/// <summary>NVMe SMART / Health Information log page (02h), NVMe base spec §5.16.1.3.</summary>
public sealed class NvmeHealth
{
    /// <summary>Bit 0 spare low, 1 temperature, 2 reliability degraded, 3 read-only, 4 volatile backup failed, 5 PMR read-only.</summary>
    public int CriticalWarning { get; set; }
    public int TemperatureC { get; set; }
    public int AvailableSparePercent { get; set; }
    public int SpareThresholdPercent { get; set; }
    /// <summary>Estimate of rated life used; can exceed 100.</summary>
    public int PercentageUsed { get; set; }
    /// <summary>In units of 512,000 bytes.</summary>
    public long DataUnitsRead { get; set; }
    public long DataUnitsWritten { get; set; }
    public long PowerCycles { get; set; }
    public long PowerOnHours { get; set; }
    public long UnsafeShutdowns { get; set; }
    public long MediaErrors { get; set; }
    public long ErrorLogEntries { get; set; }
    public long WarningTempMinutes { get; set; }
    public long CriticalTempMinutes { get; set; }

    public double TerabytesWritten => DataUnitsWritten * 512000.0 / 1e12;
}

/// <summary>Windows MSFT_StorageReliabilityCounter (Windows 8+, admin). Works for NVMe and many SATA drives.</summary>
public sealed class ReliabilityInfo
{
    public int? TemperatureC { get; set; }
    public int? TemperatureMaxC { get; set; }
    /// <summary>Percent of rated life used (SSD).</summary>
    public int? Wear { get; set; }
    public long? PowerOnHours { get; set; }
    public long? ReadErrorsTotal { get; set; }
    public long? ReadErrorsUncorrected { get; set; }
    public long? WriteErrorsTotal { get; set; }
    public long? WriteErrorsUncorrected { get; set; }
    public long? StartStopCycles { get; set; }
    public long? LoadUnloadCycles { get; set; }
}

public sealed class PartitionInfo
{
    public int DiskNumber { get; set; }
    public int PartitionNumber { get; set; }
    public string? DriveLetter { get; set; }
    public long OffsetBytes { get; set; }
    public long SizeBytes { get; set; }
    public string? Type { get; set; }
}

public sealed class VirtualDiskInfo
{
    public string? Name { get; set; }
    public string? Health { get; set; }
    public string? Resiliency { get; set; }
    public List<string> OperationalStatus { get; set; } = new();
}

public enum DiskState { Unknown, Good, Caution, Bad }

/// <summary>
/// CrystalDiskInfo-style overall verdict per drive, shared by the Storage tab and the storage checks so they agree.
/// Bad = failure predicted or data at risk now; Caution = early signs; Good = health data read and clean.
/// </summary>
public static class DiskHealth
{
    public const int ReallocatedSectors = 0x05, PendingSectors = 0xC5, UncorrectableSectors = 0xC6, Temperature = 0xC2, PowerOnHours = 0x09;
    public const int WearCaution = 80;
    public const int WearBad = 100;

    public static (DiskState State, List<string> Reasons) Assess(DiskInfo d)
    {
        var bad = new List<string>();
        var caution = new List<string>();
        if (d.Health == "Unhealthy") bad.Add("Windows reports the disk as unhealthy");
        if (d.Health == "Warning") caution.Add("Windows reports a disk health warning");
        if (d.Ata is { } a)
        {
            if (a.PredictFailure == true) bad.Add("the drive predicts its own failure (SMART)");
            foreach (var attr in a.Attributes.Where(x => x.BelowThreshold)) bad.Add($"SMART {attr.Name} is at or below its failure threshold");
            if (a[PendingSectors] is { Raw: > 0 } p) bad.Add($"{p.Raw} sectors waiting to be reallocated");
            if (a[UncorrectableSectors] is { Raw: > 0 } u) bad.Add($"{u.Raw} uncorrectable sectors");
            if (a[ReallocatedSectors] is { Raw: > 0 } r) caution.Add($"{r.Raw} reallocated sectors");
        }
        if (d.Nvme is { } n)
        {
            if ((n.CriticalWarning & ~0x02) != 0) bad.Add($"NVMe critical warning 0x{n.CriticalWarning:X2}");
            if (n.SpareThresholdPercent > 0 && n.AvailableSparePercent < n.SpareThresholdPercent) bad.Add("spare blocks below the threshold");
            if (n.MediaErrors > 0) caution.Add($"{n.MediaErrors} media errors");
        }
        if (d.Reliability is { } rel && (rel.ReadErrorsUncorrected > 0 || rel.WriteErrorsUncorrected > 0))
            caution.Add("uncorrected read/write errors recorded");
        if (Wear(d) is { } w)
        {
            if (w >= WearBad) bad.Add($"rated life used up ({w}%)");
            else if (w >= WearCaution) caution.Add($"{w}% of rated life used");
        }
        if (bad.Count > 0) return (DiskState.Bad, bad.Concat(caution).ToList());
        if (caution.Count > 0) return (DiskState.Caution, caution);
        var hasData = d.Ata is { Attributes.Count: > 0 } || d.Nvme is not null || d.Reliability is not null || d.Health is "Healthy";
        return (hasData ? DiskState.Good : DiskState.Unknown, []);
    }

    /// <summary>Percent of rated SSD life used, from NVMe, Windows counters, or the vendor's ATA wear attribute.</summary>
    public static int? Wear(DiskInfo d)
    {
        if (d.Nvme is { } n) return n.PercentageUsed;
        if (d.Reliability?.Wear is { } w && d.IsSsd) return w;
        if (d.Ata is { } a && d.IsSsd)
            // Normalized "life left" attributes (100 = new): Kingston/SandForce E7, Intel E9 media wearout, Micron/Crucial CA and AD, Samsung B1, Phison A9.
            foreach (var id in new[] { 0xE7, 0xE9, 0xCA, 0xB1, 0xAD, 0xA9 })
                if (a[id] is { Current: > 0 and <= 100 } attr) return 100 - attr.Current;
        return null;
    }

    public static int? TemperatureC(DiskInfo d) =>
        d.Nvme is { TemperatureC: > 0 and < 150 } n ? n.TemperatureC
        : d.Reliability?.TemperatureC is { } t and > 0 and < 150 ? t
        : d.Ata?[Temperature] is { } a && (a.Raw & 0xFF) is > 0 and < 150 ? (int)(a.Raw & 0xFF)
        : null;

    public static long? PowerOnHoursOf(DiskInfo d) =>
        d.Nvme?.PowerOnHours ?? d.Reliability?.PowerOnHours ?? d.Ata?[PowerOnHours]?.Raw;
}
