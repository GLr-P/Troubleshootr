using Troubleshootr.Checks.Crashes;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Windows;

/// <summary>WIN-006: SFC or DISM recorded system-file or component-store damage in CBS.log.</summary>
public sealed class Win006SystemFiles : Check
{
    public override string Id => "WIN-006";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Health)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        if (s.Health!.Value!.Cbs is not { Unavailable: null } c) yield break;
        if (c.CannotRepair > 0 || c.StoreCorrupt > 0)
            yield return F(Severity.Warning, "Windows system files are damaged",
                $"CBS.log (last 90 days): {(c.CannotRepair > 0 ? $"{N(c.CannotRepair, "file")} SFC couldn't repair" : "")}{(c.CannotRepair > 0 && c.StoreCorrupt > 0 ? "; " : "")}{(c.StoreCorrupt > 0 ? $"{N(c.StoreCorrupt, "component store corruption entry", "component store corruption entries")}" : "")}.",
                "As admin, run 'DISM /Online /Cleanup-Image /RestoreHealth', then 'sfc /scannow', and restart. If DISM fails, an in-place upgrade (run Windows setup from the ISO, keep files and apps) repairs it.", fix: "system-files");
        else if (c.Repaired > 0)
            yield return F(Severity.Info, "SFC repaired system files recently", $"CBS.log (last 90 days): {N(c.Repaired, "file")} repaired.",
                "Nothing to do unless problems continue; if they do, run DISM RestoreHealth and SFC again.");
    }
}

/// <summary>WIN-007: the PC shut down without warning (power loss, a hang, or holding the power button). Blue screens are CRA-001.</summary>
public sealed class Win007UnexpectedShutdowns : Check
{
    private const int CriticalCount = 5;

    public override string Id => "WIN-007";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var ev = s.Events!.Value!;
        var all41 = ev.Find(BlueScreens.KernelPower, 41).ToList();
        // Repeated instant power losses on a desktop are BAT-005's finding; don't report them twice.
        var cuts = Battery.Bat005PowerCuts.Cuts(s);
        var kp41 = all41.Within(s, Ev.Window).Where(e => e.Get("BugcheckCode") is null or "0").Except(cuts).ToList();
        // EventLog 6008 is logged at the same boot as Kernel-Power 41; count each restart once, and not if it was a blue screen.
        var e6008 = ev.Find("EventLog", 6008).Within(s, Ev.Window)
            .Where(e => !all41.Any(k => Math.Abs((k.TimeUtc - e.TimeUtc).TotalMinutes) < 10)).ToList();
        var count = kp41.Count + e6008.Count;
        if (count == 0) yield break;
        var button = kp41.Count(e => e.Get("LongPowerButtonPressDetected") == "true");
        var severity = count > CriticalCount ? Severity.Critical : count >= 2 ? Severity.Warning : Severity.Info;
        yield return F(severity, count == 1 ? "The PC shut down unexpectedly" : $"The PC shut down unexpectedly {count} times",
            $"{count} unexpected shutdowns in 30 days (Kernel-Power 41 / EventLog 6008){(button > 0 ? $"; {button} by holding the power button" : "")}. Latest {s.Meta.Local(kp41.Concat(e6008).Max(e => e.TimeUtc)):yyyy-MM-dd HH:mm}.",
            "If nobody held the power button or pulled the plug: check the power supply and cables (desktops), the battery and charger (laptops), and temperatures. Freezes that need a hard reset count here too.");    }
}

/// <summary>WIN-008: slow boot (Diagnostics-Performance 100), naming what slowed it (101–110). That log needs admin.</summary>
public sealed class Win008SlowBoot : Check
{
    private const int SlowMs = 60000;

    public override string Id => "WIN-008";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var ev = s.Events!.Value!;
        const string provider = "Microsoft-Windows-Diagnostics-Performance";
        var boot = ev.Find(provider, 100).Within(s, Ev.Window).OrderByDescending(e => e.TimeUtc).FirstOrDefault();
        if (boot is null || !long.TryParse(boot.Get("BootTime"), out var ms) || ms < SlowMs) yield break;
        var culprits = ev.Find(provider, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110).Within(s, Ev.Window)
            .Select(e => (name: e.Get("FriendlyName") is { Length: > 0 } f ? f : e.Get("Name"), ms: long.TryParse(e.Get("DegradationTime"), out var d) ? d : 0))
            .Where(x => x.name is { Length: > 0 })
            .GroupBy(x => x.name!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, ms: g.Max(x => x.ms))).OrderByDescending(x => x.ms).Take(3).ToList();
        yield return F(Severity.Warning, "Windows takes a long time to start",
            $"Last boot took {ms / 1000.0:0} s.{(culprits.Count > 0 ? $" Slowest to start: {string.Join(", ", culprits.Select(c => $"{c.Key} (+{c.ms / 1000.0:0.#} s)"))}." : "")}",
            "Disable unneeded startup apps (Task Manager > Startup apps), update or remove the drivers and programs named, and check the system drive's health and free space.");
    }
}

/// <summary>WIN-009: services that keep failing (Service Control Manager 7000–7034).</summary>
public sealed class Win009ServiceFailures : Check
{
    private const int Repeated = 3;

    public override string Id => "WIN-009";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var failing = s.Events!.Value!.Find("Service Control Manager").Within(s, Ev.Window)
            .Select(e => e.Id == 7009 ? e.Get("param2") : e.Get("param1") ?? e.Get("0")) // 7009's param1 is the timeout in ms
            .Where(n => n is { Length: > 0 })
            .GroupBy(n => n!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= Repeated).OrderByDescending(g => g.Count()).ToList();
        if (failing.Count == 0) yield break;
        yield return F(Severity.Warning, failing.Count == 1 ? $"A service keeps failing: {failing[0].Key}" : "Some services keep failing",
            string.Join("; ", failing.Take(5).Select(g => $"{g.Key} {g.Count()} times")) + " in 30 days.",
            "Look up what each service belongs to. Reinstall or update that program, or set the service to Manual if it isn't needed. A Windows service failing points to system file damage (DISM + SFC).");
    }
}

/// <summary>WIN-010: services Windows needs are disabled or not running.</summary>
public sealed class Win010ImportantServices : Check
{
    /// <summary>Should never be disabled.</summary>
    private static readonly string[] NeverDisabled = ["wuauserv", "BITS", "Winmgmt", "EventLog", "CryptSvc", "Audiosrv", "Dhcp", "Dnscache"];
    /// <summary>Should be running whenever they're set to Automatic.</summary>
    private static readonly string[] AlwaysRunning = ["Winmgmt", "EventLog", "CryptSvc", "Audiosrv", "AudioEndpointBuilder", "Dhcp", "Dnscache"];

    public override string Id => "WIN-010";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Health)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var services = s.Health!.Value!.Services.Where(x => x.Status is not null).ToList();
        var problems = new List<string>();
        foreach (var x in services)
        {
            var name = x.DisplayName ?? x.Name;
            if (x.StartType == "Disabled" && NeverDisabled.Contains(x.Name, StringComparer.OrdinalIgnoreCase)) problems.Add($"{name} is disabled");
            else if (x.StartType == "Automatic" && x.Status == "Stopped" && AlwaysRunning.Contains(x.Name, StringComparer.OrdinalIgnoreCase)) problems.Add($"{name} isn't running");
        }
        if (problems.Count > 0)
            yield return F(Severity.Warning, "Important Windows services are off", string.Join("; ", problems) + ".",
                "Open Services (services.msc), set each one back to its default start type and start it. If they were turned off by a 'tweak' or debloat tool, undo it; if they won't start, run DISM + SFC.");
        if (services.FirstOrDefault(x => x.Name == "Spooler") is { StartType: "Disabled" } spooler)
            yield return F(Severity.Info, "Printing is turned off", $"{spooler.DisplayName ?? "Print Spooler"} is disabled.",
                "If the PC needs to print, set the Print Spooler service to Automatic and start it.");
    }
}

/// <summary>WIN-013: the user profile failed to load (temporary profile) or Windows had to fall back to a backup.</summary>
public sealed class Win013UserProfile : Check
{
    public override string Id => "WIN-013";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Health), nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var h = s.Health!.Value!;
        var events = s.Events!.Value!.Find("Microsoft-Windows-User Profiles Service", 1511, 1515).Within(s, Ev.Window).ToList();
        if (h.TempProfile)
            yield return F(Severity.Critical, "Signed in with a temporary profile",
                "The current user's profile folder is a TEMP profile: their files, desktop and settings aren't loaded, and anything saved now is lost at sign-out.",
                "Don't save work. Restart once; if it persists, fix the profile in the registry (ProfileList: remove the duplicate .bak key) or copy the files to a new account. Back up C:\\Users\\<name> first.");
        else if (h.ProfileBackups > 0 || events.Count > 0)
            yield return F(Severity.Warning, "Windows had trouble loading a user profile",
                $"{(h.ProfileBackups > 0 ? $"{N(h.ProfileBackups, "profile backup (.bak) entry", "profile backup (.bak) entries")} in ProfileList" : "")}{(h.ProfileBackups > 0 && events.Count > 0 ? "; " : "")}{(events.Count > 0 ? $"{N(events.Count, "User Profile Service warning")} in 30 days" : "")}.",
                "If a user lost their desktop or settings, their profile is damaged: back up their files and repair or recreate the profile.");
    }
}

/// <summary>WIN-014: Windows ran low on virtual memory (Resource-Exhaustion-Detector 2004).</summary>
public sealed class Win014LowVirtualMemory : Check
{
    public override string Id => "WIN-014";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var events = s.Events!.Value!.Find("Microsoft-Windows-Resource-Exhaustion-Detector", 2004).Within(s, Ev.Window).ToList();
        if (events.Count == 0) yield break;
        var top = events[0].Message is { } m && m.IndexOf("consumed", StringComparison.OrdinalIgnoreCase) is var i and > 0
            ? " " + m.Substring(Math.Max(0, m.LastIndexOf('.', i) + 1)).Trim() : "";
        if (top.Length > 260) top = top.Substring(0, 260) + "…";
        yield return F(Severity.Warning, "Windows ran low on memory",
            $"Low virtual memory warnings {Ev.Times(events.Count)} in 30 days.{top}",
            "Close or fix the program using the most memory (often a browser with many tabs or a leaking app), let Windows manage the page file size, and add RAM if this happens with normal use.");
    }
}

/// <summary>WIN-015: Group Policy processing errors (mostly on work PCs that can't reach their domain).</summary>
public sealed class Win015GroupPolicy : Check
{
    public override string Id => "WIN-015";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var events = s.Events!.Value!.Find("Microsoft-Windows-GroupPolicy", 1085, 1096, 1129).Within(s, Ev.Window).ToList();
        if (events.Count == 0) yield break;
        yield return F(Severity.Info, "Group Policy is failing to apply",
            $"{N(events.Count, "Group Policy error")} in 30 days (ids {string.Join(", ", events.Select(e => e.Id).Distinct())}).",
            "On a work PC, connect to the office network or VPN and run 'gpupdate /force'. 1129 means the domain controller couldn't be reached.");
    }
}

/// <summary>WIN-016: Explorer, search or the Start menu crash repeatedly.</summary>
public sealed class Win016ShellCrashes : Check
{
    private const int Repeated = 3;
    private const int Days = 7;

    public override string Id => "WIN-016";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var ev = s.Events!.Value!;
        var groups = ev.Find("Application Error", 1000).Concat(ev.Find("Application Hang", 1002)).Within(s, Days)
            .Select(Ev.App).Where(a => a is not null && Ev.IsShell(a))
            .GroupBy(a => a!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= Repeated).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count == 0) yield break;
        yield return F(Severity.Warning, "Windows Explorer or the Start menu keeps crashing",
            string.Join("; ", groups.Select(g => $"{g.Key} {g.Count()} times")) + $" in the last {Days} days.",
            "Shell add-ons (context-menu extensions, cloud-drive and theming tools) are the usual cause: update or remove recently installed ones. Then run DISM + SFC.");
    }
}

/// <summary>WIN-017: Reliability Monitor's stability index (10 = no problems; it drops with every crash and failure).</summary>
public sealed class Win017Stability : Check
{
    private const double Poor = 3;
    private const double Fair = 5;

    public override string Id => "WIN-017";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Health)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var h = s.Health!.Value!;
        if (h.StabilityIndex is not { } index || index >= Fair) yield break;
        yield return F(index < Poor ? Severity.Warning : Severity.Info, "Windows has been unstable lately",
            $"Reliability Monitor stability index {index:0.#} of 10{(h.StabilityLowest30Days is { } low ? $" (lowest in 30 days: {low:0.#})" : "")}.",
            "Open Reliability Monitor (search \"reliability history\") to see which crashes and failures pulled it down, and the other findings here for the causes.");
    }
}
