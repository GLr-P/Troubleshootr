using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Troubleshootr.Core;

/// <summary>
/// What the "Ask Claude" panel gives Claude: a fixed system prompt, the anonymized report as the opening context, and
/// two read-only tools into the anonymized snapshot. Pure (no network), so it's unit-tested; the HTTP side is ClaudeChat.
/// </summary>
public sealed class AssistantContext
{
    /// <summary>Longest tool result sent back, in characters (about 15k tokens); longer ones are cut with a note.</summary>
    public const int MaxToolResult = 60000;

    private readonly Report _report;
    private readonly Dictionary<string, string> _sections = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="report">Any report; it's anonymized here, so names, serials and network names never leave the PC.</param>
    public AssistantContext(Report report)
    {
        _report = Anonymizer.Apply(report);  // always: a snapshot marked anonymized can still sit under a report header with the PC name
        foreach (var p in SectionProperties())
            if (_report.Snapshot is { } s && p.GetValue(s) is { } section)
                _sections[p.Name] = Json.Serialize(section);
    }

    public Report Report => _report;

    /// <summary>Stable across conversations and PCs (it's the cached prefix): never put per-scan data here.</summary>
    public const string SystemPrompt = """
        You are the assistant inside Troubleshootr, a portable Windows diagnostic tool. A computer technician has just scanned a PC
        and is asking you about it. The first message holds the scan report: the device, every finding (severity, evidence, advice)
        and which data sections exist. Use the tools to read the raw data behind it (drives and SMART, event logs and crashes,
        drivers and devices, startup, network, security, battery and more) before answering anything the report alone doesn't settle.

        How to answer:
        - Lead with the most likely cause and the next concrete step. Be brief; the technician is at the PC.
        - Quote the actual values you relied on (error codes, temperatures, dates, counts). Never invent values; if the data
          isn't in the scan, say so and say how to get it.
        - Rank by likelihood and by cost to try: cheap, safe checks first.
        - Troubleshootr's Fixes tab can repair system files (DISM + SFC), delete temp files, flush DNS, reset networking, clear stuck
          print jobs and start Windows Memory Diagnostic; point to it when one of those fits.
        - Names, serial numbers and network names were removed before you saw the data ("[redacted]"); that's expected.
        - Plain text only: short paragraphs or simple "-" lists, no tables or headings.
        """;

    /// <summary>The opening user message: the anonymized report as text, and what each data section holds.</summary>
    public string OpeningContext()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Scan report:");
        sb.AppendLine(TextReport.Write(_report));
        sb.AppendLine("Data sections (get_section returns the raw JSON):");
        foreach (var p in SectionProperties())
        {
            var value = _report.Snapshot is { } s ? p.GetValue(s) : null;
            var why = value?.GetType().GetProperty("Unavailable")?.GetValue(value) as string;
            sb.AppendLine(value is null ? $"- {p.Name}: not collected in this scan"
                : why is not null ? $"- {p.Name}: unavailable ({why})"
                : $"- {p.Name}: {_sections[p.Name].Length / 1000.0:0.#}k characters");
        }
        return sb.ToString();
    }

    /// <summary>Tool definitions for the Messages API (JSON Schema inputs).</summary>
    public JsonArray Tools() =>
    [
        new JsonObject
        {
            ["name"] = "get_section",
            ["description"] = "Returns one section of the scanned PC's raw data as JSON (anonymized). Large sections are cut at about 60,000 characters; use search_scan to find specific entries in them.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["section"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(SectionProperties().Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
                        ["description"] = "Section name, as listed in the report's data sections.",
                    },
                },
                ["required"] = new JsonArray("section"),
                ["additionalProperties"] = false,
            },
            ["strict"] = true,
        },
        new JsonObject
        {
            ["name"] = "search_scan",
            ["description"] = "Searches all of the scanned PC's raw data (case-insensitive) and returns the matching lines with their section, for example a driver name, an event id, an error code or a program.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Text to look for, at least 2 characters." },
                },
                ["required"] = new JsonArray("text"),
                ["additionalProperties"] = false,
            },
            ["strict"] = true,
        },
    ];

    /// <summary>Runs a tool. Returns (text, isError); never throws.</summary>
    public (string Text, bool IsError) RunTool(string name, JsonNode? input)
    {
        try
        {
            switch (name)
            {
                case "get_section":
                {
                    var section = input?["section"]?.GetValue<string>() ?? "";
                    if (!SectionProperties().Any(p => p.Name.Equals(section, StringComparison.OrdinalIgnoreCase)))
                        return ($"Unknown section '{section}'. Sections: {string.Join(", ", SectionProperties().Select(p => p.Name))}.", true);
                    return _sections.TryGetValue(section, out var json) ? (Cut(json), false) : ($"{section} wasn't collected in this scan.", false);
                }
                case "search_scan":
                {
                    var text = input?["text"]?.GetValue<string>()?.Trim() ?? "";
                    if (text.Length < 2) return ("Search text must be at least 2 characters.", true);
                    var hits = new List<string>();
                    foreach (var s in _sections)
                        foreach (var line in s.Value.Split('\n'))
                            if (line.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                                hits.Add($"{s.Key}: {line.Trim()}");
                    if (hits.Count == 0) return ($"No matches for '{text}'.", false);
                    var shown = hits.Take(300).ToList();
                    return (Cut(string.Join("\n", shown) + (hits.Count > shown.Count ? $"\n…and {hits.Count - shown.Count} more matches; search for something more specific." : "")), false);
                }
                default:
                    return ($"Unknown tool '{name}'.", true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException)
        {
            return ($"Bad tool input: {e.Message}", true);
        }
    }

    private static string Cut(string text) => text.Length <= MaxToolResult ? text
        : text.Substring(0, MaxToolResult) + $"\n…cut here: {text.Length - MaxToolResult:N0} more characters. Use search_scan to find specific entries.";

    /// <summary>The Snapshot's data sections (System, Storage, Events…), in declaration order.</summary>
    private static IEnumerable<PropertyInfo> SectionProperties() =>
        typeof(Snapshot).GetProperties().Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Section<>));
}
