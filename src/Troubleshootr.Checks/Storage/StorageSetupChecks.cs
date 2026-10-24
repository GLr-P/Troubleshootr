using Troubleshootr.Core;

namespace Troubleshootr.Checks.Storage;

/// <summary>STO-009: a volume is flagged dirty, so Windows will run chkdsk at the next boot (file system errors were found).</summary>
public sealed class Sto009DirtyVolume : Check
{
    public override string Id => "STO-009";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Volumes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var v in s.Volumes!.Value!.Where(v => v.Dirty == true))
            yield return F(Severity.Warning, $"Drive {v.Root} has file system errors", $"{v.Root} is marked dirty; Windows will check it at the next restart.",
                "Restart and let the disk check finish. If it comes back, check the drive's health; repeated corruption points to a failing drive, RAM or sudden power loss.");
    }
}

/// <summary>STO-011: TRIM is turned off, so SSDs slow down over time.</summary>
public sealed class Sto011TrimDisabled : Check
{
    public override string Id => "STO-011";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Storage)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var st = s.Storage!.Value!;
        if (st.TrimDisabled != true || !st.Disks.Any(d => d.IsSsd)) yield break;
        yield return F(Severity.Warning, "TRIM is turned off for SSDs", "DisableDeleteNotification = 1 (NTFS).",
            "Turn it back on as admin: fsutil behavior set DisableDeleteNotify 0. Without TRIM, SSDs get slower and wear faster.");
    }
}

/// <summary>STO-012: a Storage Spaces / software RAID virtual disk is degraded.</summary>
public sealed class Sto012VirtualDiskDegraded : Check
{
    public override string Id => "STO-012";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Storage)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var v in s.Storage!.Value!.VirtualDisks.Where(v => v.Health is "Warning" or "Unhealthy"))
            yield return F(v.Health == "Unhealthy" ? Severity.Critical : Severity.Warning, $"Storage space \"{v.Name}\" is {v.Health!.ToLowerInvariant()}",
                $"{v.Resiliency} space; status: {string.Join(", ", v.OperationalStatus)}.",
                "Open Settings > Storage > Storage Spaces to see which drive failed, replace it and let the space repair. Back up first.");
    }
}

/// <summary>STO-014: a partition on an SSD that isn't 4 KiB aligned (old cloning tools), which costs speed and wear.</summary>
public sealed class Sto014Misaligned : Check
{
    public override string Id => "STO-014";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Storage)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var st = s.Storage!.Value!;
        foreach (var d in st.Disks.Where(d => d.IsSsd))
        {
            var bad = st.Partitions.Where(p => p.DiskNumber == d.Number && p.OffsetBytes > 0 && p.OffsetBytes % 4096 != 0).ToList();
            if (bad.Count == 0) continue;
            yield return F(Severity.Warning, $"Partitions on {d.Name} aren't aligned", string.Join("; ", bad.Select(p => $"{(p.PartitionNumber > 0 ? $"partition {p.PartitionNumber}" : "volume")}{(p.DriveLetter is { } l ? $" ({l})" : "")} starts at byte {p.OffsetBytes:N0}")),
                "Usually left by an old cloning tool. Re-clone with alignment, or use a partition tool's 'align' option, for full SSD speed.");
        }
    }
}
