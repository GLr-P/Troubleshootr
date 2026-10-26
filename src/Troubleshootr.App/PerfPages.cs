using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>The Performance, Startup and Software tabs.</summary>
public static class PerfPages
{
    private static InfoGroup? Missing(string title, Snapshot s, string section) =>
        s.WhyMissing(section) is { } why ? new InfoGroup(title) { Note = $"Not available: {why}. Run a scan with Performance selected." } : null;

    private static string Mb(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";
    private static string Rate(long b) => b <= 0 ? "" : b >= 1048576 ? $"{b / 1048576.0:0.#} MB/s" : $"{b / 1024} KB/s";
    private static string Gb(long bytes) => $"{bytes / 1073741824.0:0.##} GB";

    public static List<InfoGroup> Performance(Snapshot s)
    {
        var groups = new List<InfoGroup>();
        if (s.Performance?.Value is { } p)
        {
            groups.Add(new InfoGroup($"During the scan ({p.SampleSeconds:0} s sample)")
                .Row("CPU busy", $"{p.CpuPercent:0}%")
                .Row("Processor speed", p.ProcessorPerformancePercent is { } sp ? $"{sp:0}% of base speed" : null)
                .Row("Busiest disk", p.DiskActivePercent is { } d ? $"{d:0}% active" : null)
                .Row("Processes", p.ProcessCount.ToString()));
            var top = new InfoGroup("Busiest processes") { Columns = ["Process", "CPU", "Memory", "Disk", "Path"] };
            foreach (var x in p.Processes)
                top.Table.Add([x.Name, $"{x.CpuPercent:0.#}%", Mb(x.PrivateMB), Rate(x.DiskBytesPerSec), x.Path ?? ""]);
            groups.Add(top);
        }
        else if (Missing("Performance", s, nameof(Snapshot.Performance)) is { } m) groups.Add(m);

        if (s.Tuning?.Value is { } t)
        {
            groups.Add(new InfoGroup("Power and settings")
                .Row("Power plan", t.PowerPlan)
                .Row("Maximum processor state", t.MaxCpuOnAc is { } ac ? $"{ac}% plugged in{(t.MaxCpuOnBattery is { } dc ? $", {dc}% on battery" : "")}" : null)
                .Row("Power source", t.OnAcPower switch { true => "Plugged in", false => "Battery", null => null })
                .Row("Battery saver", t.BatterySaverOn)
                .Row("Page file", t.PageFileSettings.Count == 0 ? "None" : t.PageFileSettings.Any(x => x.StartsWith("?:")) ? "Managed by Windows" : string.Join("; ", t.PageFileSettings))
                .Row("Page file use", t.PageFiles.Count == 0 ? null : string.Join("; ", t.PageFiles.Select(f => $"{f.Path}: {Mb(f.CurrentMB)} of {Mb(f.AllocatedMB)} (peak {Mb(f.PeakMB)})")))
                .Row("Visual effects", t.VisualEffects switch { 0 => "Let Windows choose", 1 => "Best appearance", 2 => "Best performance", 3 => "Custom", _ => null })
                .Row("Transparency", t.Transparency)
                .Row("Game Mode", t.GameMode)
                .Row("Hardware GPU scheduling", t.GpuScheduling));
            var clean = new InfoGroup("Space that could be freed") { Columns = ["What", "Size", "Where"] };
            foreach (var c in t.Cleanup.OrderByDescending(c => c.Bytes))
                clean.Table.Add([c.Name, (c.AtLeast ? "at least " : "") + Gb(c.Bytes), c.Path]);
            groups.Add(clean);
        }
        return groups;
    }

    public static List<InfoGroup> Startup(Snapshot s)
    {
        if (Missing("Startup", s, nameof(Snapshot.Startup)) is { } m) return [m];
        var entries = s.Startup!.Value!.Entries;
        var apps = new InfoGroup($"Startup apps ({entries.Count(e => e.Enabled && !e.IsTask)} on)")
        {
            Note = "Turn these on or off in Task Manager > Startup apps. Troubleshootr only shows them.",
            Columns = ["Name", "Status", "Publisher", "From", "Command"],
        };
        foreach (var e in entries.Where(e => !e.IsTask).OrderByDescending(e => e.Enabled).ThenBy(e => e.Name))
            apps.Table.Add([e.Name, e.Enabled ? "On" : "Off", Signer(e), e.Location, e.Command ?? ""]);
        var tasks = new InfoGroup("Scheduled tasks that run at sign-in or startup") { Columns = ["Name", "Status", "Publisher", "Command"] };
        foreach (var e in entries.Where(e => e.IsTask).OrderBy(e => e.Name))
            tasks.Table.Add([e.Name, e.Enabled ? "On" : "Off", Signer(e), e.Command ?? ""]);
        return [apps, tasks];
    }

    /// <summary>The verified signer, "Not signed", "Windows" for files in the Windows folder (catalog-signed), or blank when unknown.</summary>
    private static string Signer(StartupEntry e) => e.Signed switch
    {
        true => e.Publisher ?? "Signed",
        false => "Not signed",
        null when e.File is { } f && StartupEntry.IsWindowsFile(f) => "Windows",
        _ => "",
    };

    public static List<InfoGroup> Software(Snapshot s)
    {
        if (Missing("Software", s, nameof(Snapshot.Software)) is { } m) return [m];
        var sw = s.Software!.Value!;
        var browsers = new InfoGroup("Browsers")
            .Row("Default browser", sw.DefaultBrowser switch
            {
                null => null, var x when x.StartsWith("ChromeHTML") => "Google Chrome", var x when x.StartsWith("MSEdge") => "Microsoft Edge",
                var x when x.StartsWith("FirefoxURL") => "Mozilla Firefox", var x when x.StartsWith("BraveHTML") => "Brave", var x when x.StartsWith("Opera") => "Opera", var x => x,
            });
        foreach (var kv in sw.BrowserExtensions) browsers.Row($"{kv.Key} extensions", kv.Value.ToString());
        var programs = new InfoGroup($"Installed programs ({sw.Programs.Count})") { Columns = ["Name", "Version", "Publisher", "Installed", "Size"], Filterable = true };
        foreach (var p in sw.Programs)
            programs.Table.Add([p.Name, p.Version ?? "", p.Publisher ?? "", p.InstallDate?.ToString("yyyy-MM-dd") ?? "", p.SizeKB is { } kb ? Mb(kb / 1024) : ""]);
        return [browsers, programs];
    }
}
