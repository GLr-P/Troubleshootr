using System.Text.RegularExpressions;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Updates;

/// <summary>UPD-001: Windows itself hasn't installed an update in a long time (Defender and Store updates don't count).</summary>
public sealed class Upd001LastUpdate : Check
{
    private const int Days = 60;

    public override string Id => "UPD-001";
    public override Category Category => Category.Updates;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Updates)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var history = s.Updates!.Value!.History;
        if (history.Count == 0) yield break;
        var last = history.Where(e => e.IsWindowsUpdate && e.Operation == 1 && e.Succeeded).Select(e => (DateTime?)e.DateUtc).FirstOrDefault();
        var since = last ?? history.Min(e => e.DateUtc);
        var age = (s.Meta.CollectedUtc - since).TotalDays;
        if (age <= Days) yield break;
        yield return F(Severity.Warning, "Windows hasn't installed updates in a long time",
            last is { } d ? $"Last successful Windows update: {s.Meta.Local(d):yyyy-MM-dd} ({age:0} days ago)." : $"No successful Windows update in the history (it goes back {age:0} days).",
            "Open Settings > Windows Update and install what's waiting. If updates keep failing, see the failed updates and errors here, run DISM + SFC, and reset Windows Update's cache.");
    }
}

/// <summary>UPD-002: updates that failed and haven't installed since.</summary>
public sealed class Upd002FailedUpdates : Check
{
    private const int Days = 30;

    public override string Id => "UPD-002";
    public override Category Category => Category.Updates;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Updates)];

    private static string Key(UpdateEntry e) => e.Kb ?? Regex.Replace(e.Title, @"\s*\(.*?\)\s*", " ").Trim();

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var history = s.Updates!.Value!.History.Where(e => e.IsWindowsUpdate && e.Operation == 1).ToList();
        var failed = history
            .Where(e => e.Failed && (s.Meta.CollectedUtc - e.DateUtc).TotalDays <= Days)
            // A failure followed by a success for the same update is just a retry that worked.
            .Where(f => !history.Any(x => x.Succeeded && x.DateUtc > f.DateUtc && Key(x) == Key(f)))
            .GroupBy(Key).Select(g => g.First()).ToList();
        if (failed.Count == 0) yield break;
        yield return F(Severity.Warning, failed.Count == 1 ? "An update keeps failing" : $"{failed.Count} updates failed to install",
            string.Join("; ", failed.Take(4).Select(e => $"{Short(e.Title)}: {UpdateErrors.Describe(e.HResult)}")) + ".",
            "Restart and try again. If it still fails: run 'DISM /Online /Cleanup-Image /RestoreHealth' and 'sfc /scannow' as admin, free up disk space, and reset the update cache (stop wuauserv and bits, rename C:\\Windows\\SoftwareDistribution).");
    }

    private static string Short(string title) => title.Length <= 90 ? title : title.Substring(0, 87) + "…";
}

/// <summary>UPD-003: updates are paused, turned off or redirected by policy.</summary>
public sealed class Upd003UpdatePolicy : Check
{
    public override string Id => "UPD-003";
    public override Category Category => Category.Updates;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Updates)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Updates!.Value!.Policy;
        var problems = new List<string>();
        if (p.AutoUpdateOff) problems.Add("automatic updates are turned off by policy (NoAutoUpdate)");
        if (p.WindowsUpdateBlocked) problems.Add("policy blocks access to Windows Update");
        if (p.PausedUntilUtc is { } until && until > s.Meta.CollectedUtc) problems.Add($"updates are paused until {s.Meta.Local(until):yyyy-MM-dd}");
        if (problems.Count > 0)
            yield return F(Severity.Warning, "Windows updates are blocked or paused", string.Join("; ", problems) + ".",
                "Resume updates in Settings > Windows Update. If a policy or a 'tweak' tool turned them off, undo it (gpedit, or the registry under Policies\\Microsoft\\Windows\\WindowsUpdate) unless the PC is managed by work.");
        else if (p.UsesWsus || p.TargetVersion is not null)
            yield return F(Severity.Info, "Windows Update is managed by policy",
                string.Join("; ", new[] { p.UsesWsus ? "updates come from a company update server (WSUS)" : null, p.TargetVersion is { } t ? $"pinned to version {t}" : null }.Where(x => x is not null)) + ".",
                "Normal on a work PC. On a home PC, someone or some tool set this; remove the policy to get updates directly from Microsoft.");
    }
}

/// <summary>UPD-005: the next feature update is blocked (safeguard hold, not enough space, incompatible hardware).</summary>
public sealed class Upd005FeatureUpdateBlocked : Check
{
    public override string Id => "UPD-005";
    public override Category Category => Category.Updates;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Updates), nameof(Snapshot.System)];

    private static readonly Regex Version = new(@"(\d{2})H(\d)", RegexOptions.IgnoreCase);

    private static int? Rank(string? v) => v is not null && Version.Match(v) is { Success: true } m ? int.Parse(m.Groups[1].Value) * 10 + int.Parse(m.Groups[2].Value) : null;

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var current = Rank(s.System!.Value!.WindowsDisplayVersion) ?? 0;
        var blocked = s.Updates!.Value!.UpgradeBlocks
            .Where(b => Rank(b.Target) is { } r && r > current)
            .Where(b => b.RedReason is { Length: > 0 } r && !r.Equals("None", StringComparison.OrdinalIgnoreCase) || b.GatedBlockId is { Length: > 0 } g && !g.Equals("None", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (blocked.Count == 0) yield break;
        yield return F(Severity.Info, "The next Windows version is blocked on this PC",
            string.Join("; ", blocked.Select(b => $"{Version.Match(b.Target).Value}: {Reason(b)}")) + ".",
            "Safeguard holds lift by themselves once Microsoft or the driver maker fixes the issue (often a driver or app). For space, free up the system drive; for hardware, see the Windows 11 readiness finding.");
    }

    private static string Reason(UpgradeBlock b) =>
        new[] { b.RedReason is { } r && !r.Equals("None", StringComparison.OrdinalIgnoreCase) ? r : null,
                b.GatedBlockId is { } g && !g.Equals("None", StringComparison.OrdinalIgnoreCase) ? $"safeguard hold {g}" : null }
            .Where(x => x is not null).Aggregate((a, c) => $"{a}, {c}")!;
}
