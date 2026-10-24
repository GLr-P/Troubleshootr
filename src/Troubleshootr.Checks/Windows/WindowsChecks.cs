using Troubleshootr.Core;

namespace Troubleshootr.Checks.Windows;

/// <summary>WIN-002: Windows version no longer gets security updates.</summary>
public sealed class Win002OutOfSupport : Check
{
    private const int SoonDays = 90;

    public override string Id => "WIN-002";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.System)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var sys = s.System!.Value!;
        if (WindowsVersion.EndOfSupport(sys) is not { } end) yield break;
        var now = s.Meta.CollectedUtc;
        var name = WindowsVersion.Describe(sys);
        var upgrade = WindowsVersion.IsWindows11(sys)
            ? "Install the latest Windows 11 feature update through Windows Update (or the Installation Assistant)."
            : "Upgrade to Windows 11 if the hardware supports it; otherwise the PC needs Extended Security Updates or replacing. Don't use it for banking unprotected.";
        if (end <= now)
            yield return F(Severity.Warning, "This Windows version no longer gets security updates",
                $"{name}: support ended {end:yyyy-MM-dd}.", upgrade);
        else if ((end - now).TotalDays <= SoonDays)
            yield return F(Severity.Info, "Windows support ends soon", $"{name}: support ends {end:yyyy-MM-dd}.", upgrade);
    }
}

/// <summary>WIN-004: a restart is pending. Half-installed updates cause odd problems until the PC restarts.</summary>
public sealed class Win004PendingReboot : Check
{
    public override string Id => "WIN-004";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.System)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var reasons = s.System!.Value!.PendingRebootReasons;
        if (reasons.Count == 0) yield break;
        // Apps queue file renames for the next restart all the time, so that alone is only worth an Info.
        var severity = reasons.All(r => r == SystemInfo.PendingFileRenames) ? Severity.Info : Severity.Warning;
        yield return F(severity, "A restart is pending", $"Waiting on: {string.Join(", ", reasons)}.",
            "Restart (not shut down) before troubleshooting further; pending updates and driver installs can cause errors until then.");
    }
}

/// <summary>WIN-005: no restart for a long time. With Fast Startup, shutting down doesn't reset this.</summary>
public sealed class Win005LongUptime : Check
{
    private const int Days = 14;

    public override string Id => "WIN-005";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.System)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var sys = s.System!.Value!;
        var days = sys.UptimeSeconds / 86400.0;
        if (days < Days) yield break;
        var fast = sys.FastStartupEnabled == true ? " Fast Startup is on, so Shut down doesn't count; only Restart does." : "";
        yield return F(Severity.Info, "The PC hasn't restarted in a long time", $"Up for {days:0} days.{fast}",
            "Restart it: many slowdowns, memory leaks and update problems clear after a real restart.");
    }
}

/// <summary>WIN-011: WMI queries failed, so the WMI repository is probably damaged (and many tools will misbehave).</summary>
public sealed class Win011WmiBroken : Check, IRunsLast
{
    public override string Id => "WIN-011";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var failed = Snapshot.SectionNames
            .Select(n => (n, why: s.WhyMissing(n)))
            .Where(x => x.why is { } w && w.StartsWith("WMI: ", StringComparison.Ordinal))
            .ToList();
        if (failed.Count == 0) yield break;
        yield return F(Severity.Warning, "Windows Management Instrumentation (WMI) isn't working properly",
            string.Join("; ", failed.Select(x => $"{x.n}: {x.why}")),
            "Run 'winmgmt /verifyrepository' as admin; if inconsistent, 'winmgmt /salvagerepository'. Restart the WMI service. Broken WMI breaks many apps and management tools.");
    }
}

/// <summary>PER-007: BIOS/UEFI firmware is old. Updates fix stability, security (CPU microcode) and compatibility.</summary>
public sealed class Per007OldBios : Check, IPhysicalHardwareCheck
{
    private const int Years = 3;

    public override string Id => "PER-007";
    public override Category Category => Category.Peripherals;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Board)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var b = s.Board!.Value!;
        if (b.BiosDate is not { } date || date.Year < 1995) yield break; // some boards report 1980-01-01
        var age = (s.Meta.CollectedUtc - date).TotalDays / 365.25;
        if (age < Years) yield break;
        yield return F(Severity.Info, "The BIOS is old", $"{b.BiosVendor} {b.BiosVersion}, dated {date:yyyy-MM-dd} ({age:0.#} years old).",
            "Check the board or PC maker's support page for a newer BIOS, especially if there are crashes, USB problems or CPU security fixes. Never update during a storm or on battery.");
    }
}
