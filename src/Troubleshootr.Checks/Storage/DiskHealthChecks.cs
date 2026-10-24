using Troubleshootr.Core;

namespace Troubleshootr.Checks.Storage;

/// <summary>Base for checks that look at each physical disk.</summary>
public abstract class DiskCheck : Check
{
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Storage)];
    public override IEnumerable<Finding> Run(Snapshot s) => s.Storage!.Value!.Disks.SelectMany(d => RunDisk(d, s));
    protected abstract IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s);

    protected const string BackUp = "Back up important files now.";
}

/// <summary>STO-001: the drive's own SMART verdict, or any attribute at/below its failure threshold.</summary>
public sealed class Sto001SmartFailure : DiskCheck
{
    public override string Id => "STO-001";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (d.Ata is not { } a) yield break;
        var failing = a.Attributes.Where(x => x.BelowThreshold).ToList();
        if (a.PredictFailure != true && failing.Count == 0) yield break;
        var evidence = a.PredictFailure == true ? "The drive reports SMART failure predicted. " : "";
        evidence += string.Join("; ", failing.Select(x => $"{x.Name} ({x.Id:X2}) at {x.Current}, threshold {x.Threshold}"));
        yield return F(Severity.Critical, $"{d.Name} is failing", evidence.Trim(),
            $"{BackUp} Replace the drive; don't run repairs or chkdsk /r on it first, which can finish it off.");
    }
}

/// <summary>STO-002: reallocated, pending and uncorrectable sectors (ATA 05, C5, C6), the classic dying-disk signs.</summary>
public sealed class Sto002BadSectors : DiskCheck
{
    public override string Id => "STO-002";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (d.Ata is not { } a) yield break;
        var realloc = a[DiskHealth.ReallocatedSectors]?.Raw ?? 0;
        var pending = a[DiskHealth.PendingSectors]?.Raw ?? 0;
        var uncorrectable = a[DiskHealth.UncorrectableSectors]?.Raw ?? 0;
        if (realloc == 0 && pending == 0 && uncorrectable == 0) yield break;
        var evidence = $"Reallocated: {realloc}, pending: {pending}, uncorrectable: {uncorrectable}.";
        if (pending > 0 || uncorrectable > 0)
            yield return F(Severity.Critical, $"{d.Name} has unreadable sectors", evidence,
                $"{BackUp} Files on those sectors may already be damaged. Plan to replace the drive.");
        else
            yield return F(Severity.Warning, $"{d.Name} has replaced bad sectors", evidence,
                "The drive has swapped out bad areas. Back up and watch it: if the count keeps rising, replace the drive.");
    }
}

/// <summary>STO-003: NVMe critical warning byte, spare capacity and media errors.</summary>
public sealed class Sto003NvmeHealth : DiskCheck
{
    public override string Id => "STO-003";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (d.Nvme is not { } n) yield break;
        var flags = new List<string>();
        if ((n.CriticalWarning & 0x01) != 0) flags.Add("spare capacity low");
        if ((n.CriticalWarning & 0x04) != 0) flags.Add("reliability degraded");
        if ((n.CriticalWarning & 0x08) != 0) flags.Add("drive switched to read-only");
        if ((n.CriticalWarning & 0x10) != 0) flags.Add("volatile memory backup failed");
        if (n.SpareThresholdPercent > 0 && n.AvailableSparePercent < n.SpareThresholdPercent && !flags.Contains("spare capacity low"))
            flags.Add($"available spare {n.AvailableSparePercent}% is below the {n.SpareThresholdPercent}% threshold");
        if (flags.Count > 0)
        {
            yield return F(Severity.Critical, $"{d.Name} reports a critical health warning",
                $"{string.Join(", ", flags)} (critical warning 0x{n.CriticalWarning:X2}).", $"{BackUp} Replace the SSD.");
            yield break;
        }
        if (n.MediaErrors > 0)
            yield return F(Severity.Warning, $"{d.Name} has recorded media errors",
                $"{n.MediaErrors} media and data integrity errors; spare {n.AvailableSparePercent}%.",
                "Back up and keep an eye on it. A growing count means the SSD is failing.");
    }
}

/// <summary>STO-004: SSD wear (percentage of rated write endurance used).</summary>
public sealed class Sto004SsdWear : DiskCheck
{
    public override string Id => "STO-004";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (DiskHealth.Wear(d) is not { } used || used < DiskHealth.WearCaution) yield break;
        var written = d.Nvme is { } n ? $" {n.TerabytesWritten:0.#} TB written." : "";
        if (used >= DiskHealth.WearBad)
            yield return F(Severity.Critical, $"{d.Name} has used up its rated life", $"{used}% of rated endurance used.{written}",
                $"{BackUp} Worn-out SSDs can turn read-only or fail suddenly. Replace it.");
        else
            yield return F(Severity.Warning, $"{d.Name} is wearing out", $"{used}% of rated endurance used.{written}",
                "Plan a replacement and keep backups current.");
    }
}

/// <summary>STO-005: drive temperature now.</summary>
public sealed class Sto005Temperature : DiskCheck
{
    private const int HddWarning = 50, SsdWarning = 70, Critical = 80;

    public override string Id => "STO-005";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (DiskHealth.TemperatureC(d) is not { } t) yield break;
        var limit = d.IsSsd ? SsdWarning : HddWarning;
        var throttled = d.Nvme is { CriticalTempMinutes: > 0 } n ? $" It has spent {n.CriticalTempMinutes} minutes above its critical temperature." : "";
        if (t >= Critical || d.Nvme is { } hot && (hot.CriticalWarning & 0x02) != 0)
            yield return F(Severity.Critical, $"{d.Name} is overheating", $"{t} °C now.{throttled}",
                "Improve cooling: clean dust, check case fans, add a heatsink for an M.2 SSD. Heat throttles the drive and shortens its life.");
        else if (t >= limit)
            yield return F(Severity.Warning, $"{d.Name} runs hot", $"{t} °C now (comfortable below {limit} °C).{throttled}",
                "Check airflow around the drive; an M.2 SSD under a GPU often benefits from a heatsink.");
    }
}

/// <summary>STO-006: very high power-on hours on a spinning disk.</summary>
public sealed class Sto006PowerOnHours : DiskCheck
{
    private const long HddWarningHours = 40000; // ~4.5 years always on: past most HDD design life

    public override string Id => "STO-006";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (d.IsSsd || d.MediaType != "HDD" || DiskHealth.PowerOnHoursOf(d) is not { } h || h < HddWarningHours) yield break;
        yield return F(Severity.Warning, $"{d.Name} is an old hard drive", $"{h:N0} power-on hours ({h / 8766.0:0.#} years of running).",
            "Old hard drives fail without warning. Back up, and consider replacing it with an SSD.");
    }
}

/// <summary>STO-010: Windows runs from a hard drive, the most common cause of a slow PC.</summary>
public sealed class Sto010SystemOnHdd : DiskCheck
{
    public override string Id => "STO-010";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (!d.IsBoot || d.MediaType != "HDD" || d.BusType == "NVMe") yield break;
        yield return F(Severity.Warning, "Windows runs from a hard drive", $"{d.Name} is a spinning disk{(d.SpindleRpm is { } rpm ? $" ({rpm} rpm)" : "")}.",
            "Moving Windows to an SSD is the single biggest speed-up for this PC (boot, updates, apps).");
    }
}

/// <summary>STO-013: Windows' own disk health verdict and uncorrected I/O errors.</summary>
public sealed class Sto013WindowsDiskHealth : DiskCheck
{
    public override string Id => "STO-013";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        var status = d.OperationalStatus.Where(x => x != "OK").ToList();
        var statusText = status.Count > 0 ? $" Status: {string.Join(", ", status)}." : "";
        if (d.Health == "Unhealthy")
            yield return F(Severity.Critical, $"Windows reports {d.Name} as unhealthy", $"Health: Unhealthy.{statusText}", $"{BackUp} Replace the drive.");
        else if (d.Health == "Warning")
            yield return F(Severity.Warning, $"Windows reports a health warning on {d.Name}", $"Health: Warning.{statusText}",
                "Back up, then check the drive's details on the Storage tab.");
        if (d.Reliability is { } r && (r.ReadErrorsUncorrected > 0 || r.WriteErrorsUncorrected > 0))
            yield return F(Severity.Warning, $"{d.Name} has uncorrected read/write errors",
                $"Uncorrected read errors: {r.ReadErrorsUncorrected ?? 0}, write errors: {r.WriteErrorsUncorrected ?? 0}.",
                "Back up. Errors the drive couldn't correct mean data may have been lost; check the cable (SATA) or replace the drive.");
    }
}

/// <summary>STO-016: a drive behind a USB adapter, whose health can't be read.</summary>
public sealed class Sto016UsbBridge : DiskCheck
{
    public override string Id => "STO-016";

    protected override IEnumerable<Finding> RunDisk(DiskInfo d, Snapshot s)
    {
        if (d.BusType != "USB" || d.Ata is not null || d.Nvme is not null || d.Reliability is not null) yield break;
        yield return F(Severity.Info, $"Health of {d.Name} can't be read over USB", "The USB adapter or enclosure hides the drive's SMART data.",
            "To check this drive's health, connect it directly (SATA or M.2) or use an adapter that passes SMART through.");
    }
}
