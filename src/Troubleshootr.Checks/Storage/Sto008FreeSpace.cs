using Troubleshootr.Core;

namespace Troubleshootr.Checks.Storage;

/// <summary>STO-008: low free space. Windows Update needs about 10–20 GB, and a full system drive breaks updates, the pagefile and apps.</summary>
public sealed class Sto008FreeSpace : Check
{
    private const double SystemCriticalPercent = 5;
    private const long SystemCriticalBytes = 5L * 1073741824;
    private const double SystemWarningPercent = 15;
    private const long SystemWarningBytes = 20L * 1073741824; // a feature update wants ~20 GB free
    private const double OtherInfoPercent = 5;

    public override string Id => "STO-008";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Volumes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var v in s.Volumes!.Value!.Where(v => v.TotalBytes > 0 && v.DriveType == "Fixed"))
        {
            var percent = 100.0 * v.FreeBytes / v.TotalBytes;
            var evidence = $"{v.Root} has {Gb(v.FreeBytes)} free of {Gb(v.TotalBytes)} ({percent:0.#}%).";
            if (v.IsSystem)
            {
                if (percent < SystemCriticalPercent || v.FreeBytes < SystemCriticalBytes)
                    yield return F(Severity.Critical, $"System drive {v.Root} is almost full", evidence,
                        "Free up space now: Disk Cleanup (including system files), empty Downloads and the Recycle Bin, uninstall unused apps, or move files to another drive. Windows updates and apps fail when it's this full.");
                else if (percent < SystemWarningPercent || v.FreeBytes < SystemWarningBytes)
                    yield return F(Severity.Warning, $"System drive {v.Root} is low on space", evidence,
                        "Free up space soon: Windows feature updates need about 20 GB free, and performance drops on a full SSD.");
            }
            else if (percent < OtherInfoPercent)
                yield return F(Severity.Info, $"Drive {v.Root} is nearly full", evidence, "Move or delete files if this drive is still in use.");
        }
    }
}
