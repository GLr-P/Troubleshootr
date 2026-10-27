using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Troubleshootr.Core;

/// <summary>The result of a scan. Its JSON is the contract for Plannr and the Claude panel: only add fields.</summary>
public sealed class Report
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ToolVersion { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public long DurationMs { get; set; }
    public bool Full { get; set; }
    public List<Category>? Categories { get; set; }
    public DeviceSummary Device { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    public List<NotChecked> NotChecked { get; set; } = new();
    public Dictionary<string, long> TimingsMs { get; set; } = new();
    /// <summary>Fix actions run on this PC in this session, oldest first.</summary>
    public List<FixRecord> FixesRun { get; set; } = new();
    public Snapshot? Snapshot { get; set; }

    public static Report Build(Snapshot s, List<Finding> findings, List<NotChecked> notChecked,
        Dictionary<string, long> timings, ScanOptions o, DateTime started, long durationMs) => new()
    {
        ToolVersion = o.ToolVersion,
        StartedUtc = started,
        DurationMs = durationMs,
        Full = o.Full,
        Categories = o.Categories?.ToList(),
        Device = DeviceSummary.From(s),
        Findings = findings.OrderByDescending(f => f.Severity).ThenBy(f => f.CheckId, StringComparer.Ordinal).ToList(),
        NotChecked = notChecked.OrderBy(n => n.CheckId, StringComparer.Ordinal).ToList(),
        TimingsMs = timings,
        Snapshot = o.KeepSnapshot ? s : null,
    };
}

/// <summary>One fix action that ran: when, whether it worked, and the tail of its output.</summary>
public sealed class FixRecord
{
    public const int MaxOutput = 6000;

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public long DurationMs { get; set; }
    public bool Ok { get; set; }
    /// <summary>One plain line: "Flushed the DNS cache.", "SFC found no problems."</summary>
    public string Summary { get; set; } = "";
    [FreeText] public string? Output { get; set; }

    /// <summary>Keeps the end of long output (DISM and SFC print hundreds of progress lines).</summary>
    public static string? Tail(string? text) =>
        text is null || text.Length <= MaxOutput ? text : "…" + text.Substring(text.Length - MaxOutput);
}

public sealed class DeviceSummary
{
    public string? DeviceName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Windows { get; set; }
    public string? Cpu { get; set; }
    public double? RamGB { get; set; }
    public string? Gpu { get; set; }
    public long? UptimeSeconds { get; set; }
    public bool? IsLaptop { get; set; }
    public bool IsAdmin { get; set; }

    public static DeviceSummary From(Snapshot s)
    {
        var sys = s.System?.Value;
        return new DeviceSummary
        {
            DeviceName = sys?.DeviceName,
            Manufacturer = sys?.Manufacturer,
            Model = sys?.Model,
            Windows = sys is null ? null : WindowsVersion.Describe(sys),
            Cpu = s.Cpu?.Value?.Packages.FirstOrDefault()?.Name,
            RamGB = s.Memory?.Value is { } m ? Math.Round((m.InstalledMB ?? m.UsableMB) / 1024.0, 1) : null,
            Gpu = s.Gpus?.Value is { Count: > 0 } g ? string.Join(" + ", g.Select(x => x.Name)) : null,
            UptimeSeconds = sys?.UptimeSeconds,
            IsLaptop = sys?.IsLaptop,
            IsAdmin = s.Meta.IsAdmin,
        };
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Computed properties (IsLaptop, LevelName, Vendor…) are recalculated on load; don't bloat reports with them.
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("empty JSON");
}

public static class TextReport
{
    public static string Write(Report r, bool timings = false)
    {
        var sb = new StringBuilder();
        var d = r.Device;
        sb.AppendLine($"{d.DeviceName} | {d.Manufacturer} {d.Model}");
        sb.AppendLine($"{d.Windows} | {d.Cpu} | {d.RamGB} GB RAM | {d.Gpu}");
        sb.AppendLine($"Scan: {(r.Full ? "full" : "quick")}, {r.DurationMs} ms, {(d.IsAdmin ? "admin" : "not admin")}");
        sb.AppendLine();
        if (r.Findings.Count == 0) sb.AppendLine("No problems found.");
        foreach (var f in r.Findings)
        {
            sb.AppendLine($"[{f.Severity.ToString().ToUpperInvariant()}] {f.CheckId} {f.Title}");
            sb.AppendLine($"    {f.Evidence}");
            sb.AppendLine($"    -> {f.Advice}");
        }
        if (r.FixesRun.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Fixes run:");
            foreach (var x in r.FixesRun) sb.AppendLine($"    {x.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} {x.Title}: {(x.Ok ? "" : "FAILED: ")}{x.Summary}");
        }
        if (r.NotChecked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not checked:");
            foreach (var n in r.NotChecked) sb.AppendLine($"    {n.CheckId}: {n.Reason}");
        }
        if (timings)
        {
            sb.AppendLine();
            sb.AppendLine("Collector timings:");
            foreach (var t in r.TimingsMs.OrderByDescending(t => t.Value)) sb.AppendLine($"    {t.Key,-12} {t.Value,6} ms");
        }
        return sb.ToString();
    }

    /// <summary>Short plain text to paste into a ticket: the device, then the problems (Critical and Warning in full).</summary>
    public static string Ticket(Report r)
    {
        var d = r.Device;
        var sb = new StringBuilder();
        sb.AppendLine($"Device: {d.DeviceName} ({d.Manufacturer} {d.Model})");
        sb.AppendLine($"Windows: {d.Windows}");
        sb.AppendLine("Specs: " + string.Join(", ", new[] { d.Cpu, d.RamGB is { } gb ? $"{gb} GB RAM" : null, d.Gpu }.Where(x => !string.IsNullOrEmpty(x))));
        sb.AppendLine($"Scanned: {r.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} with Troubleshootr {r.ToolVersion}{(d.IsAdmin ? "" : " (not as admin)")}");
        var serious = r.Findings.Where(f => f.Severity >= Severity.Warning).ToList();
        sb.AppendLine(serious.Count == 0 ? "Problems found: none" : "Problems found:");
        foreach (var f in serious) sb.AppendLine($"- [{f.Severity}] {f.Title}: {f.Evidence}");
        var info = r.Findings.Count(f => f.Severity == Severity.Info);
        if (info > 0) sb.AppendLine($"Also {info} informational note{(info == 1 ? "" : "s")}: {string.Join("; ", r.Findings.Where(f => f.Severity == Severity.Info).Select(f => f.Title))}.");
        if (r.FixesRun.Count > 0) sb.AppendLine("Done: " + string.Join("; ", r.FixesRun.Select(x => $"{x.Title} ({(x.Ok ? x.Summary.TrimEnd('.') : "failed")})")) + ".");
        return sb.ToString();
    }

    /// <summary>Stable one-line-per-finding form used by the golden tests.</summary>
    public static string Golden(Report r) => string.Join("\n",
        r.Findings.Select(f => $"{f.Severity} {f.CheckId}: {f.Title} | {f.Evidence}")
            .Concat(r.NotChecked.Select(n => $"NotChecked {n.CheckId}: {n.Reason}"))) + "\n";
}
