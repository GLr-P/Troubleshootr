using System.Net;
using System.Text;

namespace Troubleshootr.Core;

/// <summary>
/// One self-contained, printable HTML page: the device, its hardware at a glance, every finding with evidence and advice,
/// fixes run and what wasn't checked. Inkjet-friendly: white page, black text, colour only in thin bars and small labels.
/// </summary>
public static class HtmlReport
{
    public static string Write(Report r)
    {
        var d = r.Device;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{E(d.DeviceName ?? "PC")} - Troubleshootr report</title><style>{Css}</style></head><body>");

        sb.Append("<header><div><h1>").Append(E(d.DeviceName ?? "This PC")).Append("</h1><p class=dim>")
          .Append(E(Join(" ", d.Manufacturer, d.Model))).Append("</p></div><div class=\"stamp dim\">Troubleshootr ")
          .Append(E(r.ToolVersion)).Append("<br>").Append(r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
          .Append("<br>").Append(r.Full ? "Full scan" : r.Categories is { Count: > 0 } ? "Selected areas" : "Quick scan")
          .Append(d.IsAdmin ? "" : ", not as admin").Append("</div></header>");

        sb.Append("<table class=kv>");
        Row(sb, "Windows", d.Windows);
        Row(sb, "Processor", d.Cpu);
        Row(sb, "Memory", Memory(r.Snapshot, d));
        Row(sb, "Graphics", d.Gpu);
        Row(sb, "Drives", Drives(r.Snapshot));
        Row(sb, "Battery", Battery(r.Snapshot));
        Row(sb, "BIOS", r.Snapshot?.Board?.Value is { } b ? Join(" ", b.BiosVendor, b.BiosVersion, b.BiosDate?.ToString("(yyyy-MM-dd)")) : null);
        Row(sb, "Up for", d.UptimeSeconds is { } up ? Uptime(up) : null);
        sb.Append("</table>");

        var crit = r.Findings.Count(f => f.Severity == Severity.Critical);
        var warn = r.Findings.Count(f => f.Severity == Severity.Warning);
        var info = r.Findings.Count(f => f.Severity == Severity.Info);
        sb.Append("<h2>Findings</h2><p class=counts>")
          .Append($"<span class=\"tag Critical\">{crit} critical</span> <span class=\"tag Warning\">{warn} warning{(warn == 1 ? "" : "s")}</span> <span class=\"tag Info\">{info} note{(info == 1 ? "" : "s")}</span></p>");
        if (r.Findings.Count == 0) sb.Append("<p>No problems found in what was checked.</p>");
        foreach (var f in r.Findings)
        {
            sb.Append($"<div class=\"finding {f.Severity}\"><div class=top><b>{E(f.Title)}</b><span class=dim>{f.Severity} &middot; {E(f.CheckId)}</span></div>");
            sb.Append($"<p class=evidence>{E(f.Evidence)}</p><p><b>What to do:</b> {E(f.Advice)}</p></div>");
        }

        if (r.FixesRun.Count > 0)
        {
            sb.Append("<h2>Fixes run</h2>");
            foreach (var x in r.FixesRun)
            {
                sb.Append($"<div class=\"finding {(x.Ok ? "Ok" : "Critical")}\"><div class=top><b>{E(x.Title)}</b><span class=dim>{x.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}, {Seconds(x.DurationMs)}</span></div>");
                sb.Append($"<p>{(x.Ok ? "" : "<b>Failed:</b> ")}{E(x.Summary)}</p>");
                if (!string.IsNullOrWhiteSpace(x.Output)) sb.Append($"<details><summary class=dim>Output</summary><pre>{E(x.Output!.Trim())}</pre></details>");
                sb.Append("</div>");
            }
        }

        if (r.NotChecked.Count > 0)
        {
            sb.Append("<h2>Not checked</h2><ul class=\"notchecked dim\">");
            foreach (var g in r.NotChecked.GroupBy(n => n.Reason).OrderByDescending(g => g.Count()))
                sb.Append($"<li>{E(string.Join(", ", g.Select(n => n.CheckId)))}: {E(g.Key)}</li>");
            sb.Append("</ul>");
        }
        sb.Append($"<footer class=dim>Made with Troubleshootr {E(r.ToolVersion)}, a free diagnostic tool: github.com/GLr-P/Troubleshootr{(r.Snapshot?.Meta.Anonymized == true ? ". Names, serial numbers and network names removed." : ".")}</footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string? Memory(Snapshot? s, DeviceSummary d)
    {
        if (s?.Memory?.Value is not { } m) return d.RamGB is { } gb ? $"{gb} GB" : null;
        var mods = m.Modules;
        var speed = mods.Select(x => x.ConfiguredSpeedMts ?? x.SpeedMts).FirstOrDefault(x => x is > 0);
        return Join(", ", $"{(m.InstalledMB ?? m.UsableMB) / 1024.0:0.#} GB", mods.FirstOrDefault()?.Type,
            speed is { } sp ? $"{sp} MT/s" : null,
            mods.Count > 0 ? $"{mods.Count} module{(mods.Count == 1 ? "" : "s")}{(m.SlotCount is { } slots && slots >= mods.Count ? $" in {slots} slots" : "")}" : null);
    }

    private static string? Drives(Snapshot? s)
    {
        if (s?.Storage?.Value is not { Disks.Count: > 0 } st) return null;
        return string.Join("; ", st.Disks.Select(x =>
        {
            var (state, _) = DiskHealth.Assess(x);
            var wear = x.Nvme?.PercentageUsed is { } used ? $", {used}% worn" : "";
            return $"{x.Model ?? "Unknown drive"} ({x.SizeBytes / 1e9:0} GB {x.BusType}{wear}, health {(state == DiskState.Unknown ? "unknown" : state.ToString().ToLowerInvariant())})";
        }));
    }

    private static string? Battery(Snapshot? s) =>
        s?.Power?.Value is { Batteries.Count: > 0 } p
            ? string.Join("; ", p.Batteries.Select(b => Join(", ", b.HealthPercent is { } h ? $"{h:0}% of its original capacity" : "capacity unknown",
                b.CycleCount is > 0 ? $"{b.CycleCount} cycles" : null)))
            : null;

    private static void Row(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.Append($"<tr><th>{label}</th><td>{E(value)}</td></tr>");
    }

    private static string Uptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min" : $"{t.Minutes} min";
    }

    private static string Seconds(long ms) => ms >= 60000 ? $"{ms / 60000.0:0} min" : $"{ms / 1000.0:0.#} s";

    private static string Join(string sep, params string?[] parts) => string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        *{box-sizing:border-box}
        body{font:14px/1.45 "Segoe UI",Arial,sans-serif;color:#111;background:#fff;max-width:900px;margin:24px auto;padding:0 20px}
        header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;border-bottom:2px solid #111;padding-bottom:10px;margin-bottom:14px}
        h1{font-size:22px;margin:0}h2{font-size:16px;margin:22px 0 8px;border-bottom:1px solid #bbb;padding-bottom:3px}
        p{margin:4px 0}.dim{color:#555}.stamp{text-align:right;font-size:12px}
        table.kv{border-collapse:collapse;width:100%}table.kv th{text-align:left;font-weight:600;width:110px;vertical-align:top;padding:2px 10px 2px 0}table.kv td{padding:2px 0}
        .counts{margin-bottom:10px}.tag{font-size:12px;font-weight:600;border:1px solid currentColor;border-radius:9px;padding:1px 8px}
        .finding{border:1px solid #ccc;border-left:5px solid #888;border-radius:4px;padding:7px 11px;margin:0 0 8px;page-break-inside:avoid;break-inside:avoid}
        .finding .top{display:flex;justify-content:space-between;gap:12px}.finding .top span{font-size:12px;white-space:nowrap}
        .evidence{color:#333}
        .Critical{border-left-color:#c62828}.tag.Critical{color:#c62828}
        .Warning{border-left-color:#e08a00}.tag.Warning{color:#a86400}
        .Info{border-left-color:#1e6fd9}.tag.Info{color:#1e6fd9}
        .Ok{border-left-color:#2e7d32}
        pre{white-space:pre-wrap;font:11px Consolas,monospace;max-height:320px;overflow:auto;background:#f6f6f6;padding:6px}
        .notchecked{font-size:12px;padding-left:18px}
        footer{margin-top:26px;font-size:11px;border-top:1px solid #bbb;padding-top:6px}
        @media print{body{margin:0;max-width:none}details{display:none}a{color:#111}}
        """;
}
