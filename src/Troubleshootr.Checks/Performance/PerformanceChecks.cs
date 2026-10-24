using Troubleshootr.Core;

namespace Troubleshootr.Checks.Performance;

internal static class Perf
{
    public static string Mb(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";
    public static string Rate(long bytesPerSec) => bytesPerSec >= 1048576 ? $"{bytesPerSec / 1048576.0:0.#} MB/s" : $"{bytesPerSec / 1024} KB/s";
    public static string Gb(long bytes) => $"{bytes / 1073741824.0:0.#} GB";
}

/// <summary>PRF-001: lots of programs start with Windows (each one slows sign-in and uses memory all day).</summary>
public sealed class Prf001StartupPrograms : Check
{
    private const int Many = 8;
    private const int TooMany = 15;

    public override string Id => "PRF-001";
    public override Category Category => Category.Performance;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Startup)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var on = s.Startup!.Value!.Entries.Where(e => e.Enabled && !e.IsTask).ToList();
        if (on.Count < Many) yield break;
        yield return F(on.Count > TooMany ? Severity.Warning : Severity.Info, $"{on.Count} programs start with Windows",
            string.Join(", ", on.Take(12).Select(e => e.Name)) + (on.Count > 12 ? $" and {on.Count - 12} more." : "."),
            "Turn off the ones that don't need to run all the time: Task Manager > Startup apps (right-click > Disable). Chat apps, game launchers and updaters are usually safe to switch off.");
    }
}

/// <summary>PRF-003: one program is hogging the CPU, memory or disk right now.</summary>
public sealed class Prf003ResourceHogs : Check
{
    private const double CpuShare = 50;      // % of the whole PC
    private const double MemoryShare = 0.4;  // of usable RAM
    private const long DiskRate = 50L * 1048576;

    public override string Id => "PRF-003";
    public override Category Category => Category.Performance;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Performance)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Performance!.Value!;
        var usableMb = s.Memory?.Value?.UsableMB ?? 0;
        var hogs = new List<string>();
        foreach (var x in p.Processes)
        {
            if (x.CpuPercent >= CpuShare) hogs.Add($"{x.Name} is using {x.CpuPercent:0}% of the CPU");
            if (usableMb > 0 && x.PrivateMB >= usableMb * MemoryShare) hogs.Add($"{x.Name} is using {Perf.Mb(x.PrivateMB)} of memory ({100.0 * x.PrivateMB / usableMb:0}% of RAM)");
            if (x.DiskBytesPerSec >= DiskRate) hogs.Add($"{x.Name} is reading/writing {Perf.Rate(x.DiskBytesPerSec)}");
        }
        if (hogs.Count == 0) yield break;
        yield return F(Severity.Warning, "A program is hogging the PC", string.Join("; ", hogs.Take(4)) + $" (sampled for {p.SampleSeconds:0} s).",
            "If it isn't something you're using on purpose (a game, a video export, a backup), close it or end it in Task Manager. Repeated hogs can be malware, a stuck update or a buggy app worth reinstalling.");
    }
}

/// <summary>CPU-002: the processor is maxed out.</summary>
public sealed class Cpu002HighCpu : Check
{
    private const double Busy = 85;

    public override string Id => "CPU-002";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Performance)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Performance!.Value!;
        if (p.CpuPercent < Busy) yield break;
        var top = p.Processes.Where(x => x.CpuPercent >= 1).OrderByDescending(x => x.CpuPercent).Take(3).Select(x => $"{x.Name} {x.CpuPercent:0}%");
        yield return F(Severity.Warning, "The processor is maxed out", $"CPU {p.CpuPercent:0}% busy over {p.SampleSeconds:0} s. Top: {string.Join(", ", top)}.",
            "Close what's using it, or check the Processes in Task Manager. A busy CPU with nothing open points to updates, an antivirus scan, or malware (crypto miners hide as system-sounding names).");
    }
}

/// <summary>CPU-003: under load and on mains power, the processor runs far below its base speed (thermal or power throttling).</summary>
public sealed class Cpu003Throttled : Check
{
    private const double Loaded = 50;
    private const double Slow = 50;

    public override string Id => "CPU-003";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Performance), nameof(Snapshot.Tuning)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Performance!.Value!;
        var t = s.Tuning!.Value!;
        if (t.OnAcPower != true || t.BatterySaverOn == true || p.CpuPercent < Loaded || p.ProcessorPerformancePercent is not { } speed || speed >= Slow) yield break;
        yield return F(Severity.Info, "The processor is running slowly under load",
            $"CPU {p.CpuPercent:0}% busy but at only {speed:0}% of its base speed, while plugged in.",
            "Usually heat (dust, dried thermal paste, blocked vents) or a power limit (weak charger, BIOS or maker's power settings). Check temperatures with HWiNFO and clean the cooler.");
    }
}

/// <summary>CPU-008: the power plan holds the processor back (Power saver, or a maximum processor state under 100% on mains power).</summary>
public sealed class Cpu008PowerPlan : Check
{
    public override string Id => "CPU-008";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Tuning), nameof(Snapshot.System)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var t = s.Tuning!.Value!;
        var laptop = s.System!.Value!.IsLaptop;
        var problems = new List<string>();
        if (string.Equals(t.PowerPlanGuid, TuningInfo.PowerSaverGuid, StringComparison.OrdinalIgnoreCase)) problems.Add("the Power saver plan is active");
        if (t.MaxCpuOnAc is { } max and < 100) problems.Add($"the processor is capped at {max}% while plugged in");
        if (problems.Count == 0) yield break;
        yield return F(laptop ? Severity.Info : Severity.Warning, "The power plan slows the processor down",
            $"{string.Join("; ", problems)} (plan: {t.PowerPlan ?? t.PowerPlanGuid}).",
            "Control Panel > Power Options: choose Balanced (or High performance on a desktop), and set Processor power management > Maximum processor state to 100% on mains.");
    }
}

/// <summary>STO-018: space that cleaning up would give back (temp files, old Windows, update downloads).</summary>
public sealed class Sto018Reclaimable : Check
{
    private const long Worth = 5L * 1073741824;
    private const long ListOver = 512L * 1048576;

    public override string Id => "STO-018";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Tuning)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var items = s.Tuning!.Value!.Cleanup;
        var total = items.Sum(i => i.Bytes);
        if (total < Worth) yield break;
        yield return F(Severity.Info, $"About {Perf.Gb(total)} could be freed up",
            string.Join("; ", items.Where(i => i.Bytes >= ListOver).OrderByDescending(i => i.Bytes).Select(i => $"{i.Name}: {(i.AtLeast ? "at least " : "")}{Perf.Gb(i.Bytes)}")) + ".",
            "Run Disk Cleanup > Clean up system files (or Settings > System > Storage > Temporary files). Windows.old is only needed to go back to the previous Windows version.");
    }
}

/// <summary>STO-019: no page file, or a page file on a removable drive (apps crash when RAM fills, no crash dumps).</summary>
public sealed class Sto019PageFile : Check
{
    public override string Id => "STO-019";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Tuning)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var t = s.Tuning!.Value!;
        if (t.PageFileSettings.Count == 0 && t.PageFiles.Count == 0)
        {
            yield return F(Severity.Warning, "There's no page file", "Virtual memory is turned off (no paging file on any drive).",
                "System Properties > Advanced > Performance > Settings > Advanced > Virtual memory: choose 'Automatically manage'. Without one, programs crash when RAM fills and blue screens leave no crash dump.");
            yield break;
        }
        var removable = (s.Volumes?.Value ?? []).Where(v => v.DriveType == "Removable").Select(v => v.Root.Substring(0, 1)).ToList();
        var bad = t.PageFiles.Where(p => p.Path.Length > 0 && removable.Contains(p.Path.Substring(0, 1), StringComparer.OrdinalIgnoreCase)).ToList();
        if (bad.Count > 0)
            yield return F(Severity.Warning, "The page file is on a removable drive", $"{string.Join(", ", bad.Select(p => p.Path))}.",
                "Move the page file to the system drive (Virtual memory settings); if the removable drive is unplugged Windows loses its virtual memory.");
    }
}

/// <summary>STO-020: a disk stayed busy the whole sample ("disk at 100%" in Task Manager).</summary>
public sealed class Sto020DiskBusy : Check
{
    private const double Busy = 90;

    public override string Id => "STO-020";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Performance)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Performance!.Value!;
        if (p.DiskActivePercent is not { } active || active < Busy) yield break;
        var top = p.Processes.Where(x => x.DiskBytesPerSec > 0).OrderByDescending(x => x.DiskBytesPerSec).Take(3).Select(x => $"{x.Name} {Perf.Rate(x.DiskBytesPerSec)}").ToList();
        yield return F(Severity.Warning, "A disk is at 100%", $"Disk active {active:0}% over {p.SampleSeconds:0} s.{(top.Count > 0 ? $" Busiest: {string.Join(", ", top)}." : "")}",
            "If it lasts, check the drive's health on the Storage tab. On a hard drive this is the usual cause of a slow PC: an SSD fixes it. Otherwise look for updates, indexing, antivirus scans or sync apps running.");
    }
}

/// <summary>PRF-005: "PC optimizers", driver updaters and adware that slow PCs down or nag for payment.</summary>
public sealed class Prf005Bloatware : Check
{
    private static readonly string[] Known =
    [
        "Driver Booster", "Advanced SystemCare", "IObit Uninstaller", "Smart Defrag", "Reimage", "Restoro", "PC Optimizer", "PC Accelerate",
        "DriverPack", "Driver Easy", "Driver Genius", "DriverMax", "Smart Driver Care", "Driver Updater", "Wise Care 365", "Glary Utilities",
        "SlimCleaner", "Auslogics BoostSpeed", "Auslogics Driver Updater", "MyPC Utilities", "OneSafe", "Segurazo", "ByteFence", "Web Companion",
        "WebAdvisor", "Avast Cleanup", "AVG TuneUp", "Norton Utilities", "PC Cleaner", "Total System Care", "Advanced PC Cleaner", "Mac Booster",
        "Shield Antivirus", "Wave Browser", "OneLaunch", "WebDiscover", "Opera GX Ad",
    ];

    public override string Id => "PRF-005";
    public override Category Category => Category.Performance;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Software)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var found = s.Software!.Value!.Programs.Where(p => Known.Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).Select(p => p.Name).Distinct().ToList();
        if (found.Count == 0) yield break;
        yield return F(Severity.Info, "\"Optimizer\" or adware programs are installed", string.Join(", ", found) + ".",
            "These rarely help and often slow the PC, nag for payment or install other software. Uninstall them; Windows keeps itself tidy and Windows Update handles drivers.");
    }
}

/// <summary>PRF-009: browsers loaded with extensions (a common cause of slowness and adware).</summary>
public sealed class Prf009Browsers : Check
{
    private const int ManyExtensions = 20;

    public override string Id => "PRF-009";
    public override Category Category => Category.Performance;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Software)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var heavy = s.Software!.Value!.BrowserExtensions.Where(kv => kv.Value >= ManyExtensions).ToList();
        if (heavy.Count == 0) yield break;
        yield return F(Severity.Info, "A browser has a lot of extensions", string.Join("; ", heavy.Select(kv => $"{kv.Key}: {kv.Value} extensions")) + ".",
            "Remove extensions that aren't used (each one uses memory, and unknown ones can inject ads or track browsing). Type chrome://extensions or edge://extensions in the address bar.");
    }
}
