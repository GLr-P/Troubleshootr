using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Events;

/// <summary>
/// Reads only the event-log entries the checks use (one XPath query per provider, limited to 30 days, newest first),
/// plus the last 7 days of critical/error events for the timeline. Never reads whole logs (ARCHITECTURE.md §4).
/// </summary>
public sealed class EventsCollector : Collector<EventsInfo>
{
    private const int Days = 30;
    private const int TimelineDays = 7;
    private const int MaxPerRule = 200;
    private const int MaxTimelinePerLog = 250;
    private const int MaxMessage = 600;
    private const string DiagPerf = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>Log, provider, event ids, and whether only warnings and worse count (Ntfs 98 is also logged as "volume is healthy").</summary>
    internal sealed record Rule(string Log, string Provider, int[] Ids, bool WarningsOnly = false);

    internal static readonly Rule[] Rules =
    [
        // Storage (STO-007)
        new("System", "disk", [7, 11, 51, 153]),
        new("System", "storahci", [129]),
        new("System", "stornvme", [11, 129]),
        new("System", "Ntfs", [55, 98], WarningsOnly: true),
        new("System", "Microsoft-Windows-Ntfs", [55, 98], WarningsOnly: true),
        // Crashes and shutdowns (CRA-001, WIN-007)
        new("System", BlueScreens.WerProvider, [1001]),
        new("System", BlueScreens.KernelPower, [41]),
        new("System", "EventLog", [6008]),
        // Hardware errors (MEM-007, MEM-008, CPU-005, CPU-006, GPU-004)
        new("System", "Microsoft-Windows-WHEA-Logger", [1, 17, 18, 19, 47]),
        new("System", "Microsoft-Windows-MemoryDiagnostics-Results", [1101, 1102, 1201, 1202]),
        new("System", "Microsoft-Windows-Kernel-Processor-Power", [37]),
        new("System", "Display", [4101]),
        new("System", "nvlddmkm", [13, 14, 153]),
        new("System", "amdkmdag", [], WarningsOnly: true),
        // Windows (WIN-009, WIN-013, WIN-014, WIN-015)
        new("System", "Service Control Manager", [7000, 7001, 7009, 7023, 7031, 7034]),
        new("System", "Microsoft-Windows-Resource-Exhaustion-Detector", [2004]),
        new("System", "Microsoft-Windows-GroupPolicy", [1085, 1096, 1129]),
        // Driver load failures (DRV-008)
        new("System", "Microsoft-Windows-Kernel-PnP", [219, 411]),
        // USB port problems and devices keeping a laptop awake (DRV-009)
        new("System", "Microsoft-Windows-USB-USBHUB3", [], WarningsOnly: true),
        new("System", "usbhub", [], WarningsOnly: true),
        new("Application", "Microsoft-Windows-User Profiles Service", [1511, 1515]),
        // App crashes (CRA-003, WIN-016)
        new("Application", "Application Error", [1000]),
        new("Application", "Application Hang", [1002]),
        new("Application", ".NET Runtime", [1026]),
        // Boot performance (WIN-008); this log needs admin
        new(DiagPerf, "Microsoft-Windows-Diagnostics-Performance", [100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110]),
    ];

    public override string Section => nameof(Snapshot.Events);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(8);

    protected override EventsInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new EventsInfo { Days = Days, TimelineDays = TimelineDays };
        var unreadable = new HashSet<string>();
        // Each query waits mostly on the event log service, so a few at a time is about 3x faster than one by one.
        var timeline = $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {TimelineDays * 86400000L}]]]";
        var jobs = Rules.Select(r => (Action)(() => Read(r.Log, XPath(r, Days), MaxPerRule, withData: true, info.Events, unreadable, ct)))
            .Concat(new[] { "System", "Application" }.Select(log => (Action)(() => Read(log, timeline, MaxTimelinePerLog, withData: false, info.Timeline, unreadable, ct))))
            .ToList();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, job => job());
        info.Events = info.Events.OrderByDescending(e => e.TimeUtc).ToList();
        info.Timeline = info.Timeline.OrderByDescending(e => e.TimeUtc).ToList();
        info.Unreadable = unreadable.OrderBy(x => x).ToList();
        // If even the System log is unreadable, the event log service itself is broken: report the section as missing.
        if (info.Unreadable.Any(u => u.StartsWith("System:", StringComparison.Ordinal)) && info.Events.Count == 0 && info.Timeline.Count == 0)
            throw new CollectorException($"event log: {string.Join("; ", info.Unreadable)}");
        return info;
    }

    internal static string XPath(Rule r, int days)
    {
        var ids = r.Ids.Length == 0 ? "" : " and (" + string.Join(" or ", r.Ids.Select(i => $"EventID={i}")) + ")";
        var level = r.WarningsOnly ? " and (Level=1 or Level=2 or Level=3)" : "";
        return $"*[System[Provider[@Name='{r.Provider}']{ids}{level} and TimeCreated[timediff(@SystemTime) <= {days * 86400000L}]]]";
    }

    private static void Read(string log, string xpath, int max, bool withData, List<LogEvent> into, HashSet<string> unreadable, CancellationToken ct)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
            for (var n = 0; n < max; n++)
            {
                ct.ThrowIfCancellationRequested();
                using var e = reader.ReadEvent();
                if (e is null) break;
                var item = new LogEvent
                {
                    Log = log == DiagPerf ? "Diagnostics-Performance" : log,
                    Provider = e.ProviderName ?? "",
                    Id = e.Id,
                    Level = e.Level ?? 4,
                    TimeUtc = e.TimeCreated?.ToUniversalTime() ?? default,
                    Message = Message(e),
                    Data = withData ? Data(e) : new(),
                };
                lock (into) into.Add(item);
            }
        }
        catch (EventLogNotFoundException) { } // the log doesn't exist on this Windows version
        catch (UnauthorizedAccessException) { lock (unreadable) unreadable.Add($"{ShortName(log)}: needs admin"); }
        catch (EventLogException e) { lock (unreadable) unreadable.Add($"{ShortName(log)}: {e.Message.Trim()}"); }
    }

    private static string ShortName(string log) => log == DiagPerf ? "Diagnostics-Performance" : log;

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    private static string? Message(EventRecord e)
    {
        string? text;
        try { text = e.FormatDescription(); }
        catch (EventLogException) { return null; }
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Spaces.Replace(text!, " ").Trim();
        return text.Length <= MaxMessage ? text : text.Substring(0, MaxMessage) + "…";
    }

    /// <summary>EventData (or UserData) values by name, from the event's XML. Unnamed values get their position as the name.</summary>
    private static Dictionary<string, string> Data(EventRecord e)
    {
        var data = new Dictionary<string, string>();
        try { Fill(XElement.Parse(e.ToXml()), data); }
        catch (Exception x) when (x is EventLogException or System.Xml.XmlException) { }
        return data;
    }

    internal static void Fill(XElement root, Dictionary<string, string> data)
    {
        var ns = root.Name.Namespace;
        IEnumerable<XElement> items = root.Element(ns + "EventData")?.Elements()
            ?? root.Element(ns + "UserData")?.Elements().FirstOrDefault()?.Elements()
            ?? [];
        var i = 0;
        foreach (var x in items)
        {
            var name = (string?)x.Attribute("Name") ?? (x.Name.LocalName == "Data" ? i.ToString() : x.Name.LocalName);
            i++;
            if (name is "RawData" or "Binary") continue; // hex blobs (WHEA records): big and unreadable
            var value = x.Value.Trim();
            if (value.Length > MaxMessage) value = value.Substring(0, MaxMessage);
            if (!data.ContainsKey(name)) data[name] = value;
        }
    }
}
