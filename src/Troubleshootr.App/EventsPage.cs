using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>The Events &amp; Crashes tab: blue screens, unexpected shutdowns, app crashes, hardware errors and the error timeline.</summary>
public static class EventsPage
{
    private static readonly string[] HardwareProviders =
    [
        "Microsoft-Windows-WHEA-Logger", "disk", "storahci", "stornvme", "Ntfs", "Microsoft-Windows-Ntfs",
        "Display", "nvlddmkm", "amdkmdag", "Microsoft-Windows-MemoryDiagnostics-Results",
    ];

    public static List<InfoGroup> Build(Snapshot s)
    {
        var groups = new List<InfoGroup>();
        var ev = s.Events?.Value;
        var crashes = s.Crashes?.Value;
        if (ev is null && crashes is null)
            return [new InfoGroup("Events & Crashes") { Note = $"Not available: {s.WhyMissing(nameof(Snapshot.Events))}. Run a scan with Crashes & errors selected." }];

        var bsods = BlueScreens.From(s);
        var blue = new InfoGroup("Blue screens") { Columns = ["When", "Code", "Name", "Driver", "From"] };
        foreach (var b in bsods)
            blue.Table.Add([When(b.TimeUtc), BugChecks.Hex(b.Code), BugChecks.Name(b.Code), b.Driver ?? "", b.Source]);
        var notes = new List<string>();
        if (bsods.FirstOrDefault()?.Info is { } latest) notes.Add($"Latest: {latest.Meaning}");
        if (crashes?.DumpsUnavailable is { } why && crashes.Dumps.Count > 0) notes.Add($"{crashes.Dumps.Count} dump files; {why} (Restart as admin to see the driver at fault).");
        if (notes.Count > 0) blue.Note = string.Join(" ", notes);
        groups.Add(blue);

        if (ev is not null)
        {
            var shutdowns = new InfoGroup("Unexpected shutdowns") { Columns = ["When", "Event", "Details"] };
            foreach (var e in ev.Find(BlueScreens.KernelPower, 41).Where(e => e.Get("BugcheckCode") is null or "0").Concat(ev.Find("EventLog", 6008)).OrderByDescending(e => e.TimeUtc))
                shutdowns.Table.Add([When(e.TimeUtc), e.Id == 41 ? "Kernel-Power 41" : "EventLog 6008",
                    e.Get("LongPowerButtonPressDetected") == "true" ? "Power button held" : e.Id == 41 ? "Lost power, froze or was reset" : "Previous shutdown was unexpected"]);
            groups.Add(shutdowns);

            var apps = new InfoGroup($"App crashes and hangs (last {ev.Days} days)") { Columns = ["App", "Crashes", "Hangs", "Last", "Crashed in"] };
            foreach (var g in AppCrashes.All(ev).GroupBy(e => AppCrashes.App(e) ?? "?", StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
            {
                var module = g.Select(e => e.Get("ModuleName")).Where(m => m is { Length: > 0 }).GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(m => m.Count()).FirstOrDefault()?.Key ?? "";
                apps.Table.Add([g.Key, g.Count(e => e.Id != 1002).ToString(), g.Count(e => e.Id == 1002).ToString(), When(g.Max(e => e.TimeUtc)), module]);
            }
            groups.Add(apps);

            var hardware = new InfoGroup("Hardware and driver errors") { Columns = ["When", "Source", "ID", "Message"] };
            foreach (var e in ev.Events.Where(e => HardwareProviders.Contains(e.Provider, StringComparer.OrdinalIgnoreCase)).Take(100))
                hardware.Table.Add([When(e.TimeUtc), Short(e.Provider), e.Id.ToString(), e.Message ?? ""]);
            groups.Add(hardware);

            var services = new InfoGroup("Service failures") { Columns = ["Service", "Failures", "Last"] };
            foreach (var g in ev.Find("Service Control Manager").GroupBy(e => (e.Id == 7009 ? e.Get("param2") : e.Get("param1")) ?? "?", StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
                services.Table.Add([g.Key, g.Count().ToString(), When(g.Max(e => e.TimeUtc))]);
            if (services.Table.Count > 0) groups.Add(services);
        }

        var settings = new InfoGroup("Crash settings and reliability")
            .Row("Crash dumps", crashes?.CrashDumpEnabled switch
            {
                0 => "Off (blue screens leave no dump)", 1 => "Complete memory dump", 2 => "Kernel memory dump", 3 => "Small memory dump",
                7 => "Automatic memory dump", null => null, var x => $"Type {x}",
            })
            .Row("Restart after a blue screen", crashes?.AutoReboot)
            .Row("Dump files", crashes is null ? null : crashes.Dumps.Count.ToString())
            .Row("Error reports (30 days)", crashes is null ? null : $"{crashes.Reports.Count} ({crashes.QueuedReports} waiting to send)");
        if (s.Health?.Value is { } h)
            settings.Row("Stability index", h.StabilityIndex is { } i ? $"{i:0.#} of 10{(h.StabilityLowest30Days is { } low ? $" (lowest in 30 days: {low:0.#})" : "")}" : null);
        if (ev is { Unreadable.Count: > 0 }) settings.Row("Logs not read", string.Join("; ", ev.Unreadable));
        groups.Add(settings);

        if (ev is not null)
        {
            var timeline = new InfoGroup($"Error timeline (last {ev.TimelineDays} days)") { Columns = ["When", "Level", "Source", "ID", "Message"], Filterable = true };
            foreach (var e in ev.Timeline)
                timeline.Table.Add([When(e.TimeUtc), e.LevelName, Short(e.Provider), e.Id.ToString(), e.Message ?? ""]);
            groups.Add(timeline);
        }
        return groups;
    }

    private static string When(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string Short(string provider) => provider.StartsWith("Microsoft-Windows-", StringComparison.Ordinal) ? provider.Substring(18) : provider;
}
