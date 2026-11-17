using System.Text.Json.Nodes;

namespace Troubleshootr.Core;

public enum PlannrTarget { NewTicket, Ticket, Note }

/// <summary>
/// What a report sent to Plannr contains (version 1; Plannr's services/troubleshootr.ts reads it). Only add fields.
/// Not anonymized: it goes to the technician's own Plannr, where the device and customer belong.
/// </summary>
public static class PlannrPayload
{
    public static JsonObject Build(Report r, PlannrTarget target, int? ticketNumber = null, string? customer = null)
    {
        var d = r.Device;
        var device = $"{d.Manufacturer} {d.Model}".Trim();
        return new JsonObject
        {
            ["v"] = 1,
            ["kind"] = "troubleshootr-report",
            ["sentAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["toolVersion"] = r.ToolVersion,
            ["target"] = new JsonObject
            {
                ["mode"] = target switch { PlannrTarget.Ticket => "ticket", PlannrTarget.Note => "note", _ => "new" },
                ["ticketNumber"] = target == PlannrTarget.Ticket ? ticketNumber : null,
                ["customer"] = target == PlannrTarget.NewTicket && !string.IsNullOrWhiteSpace(customer) ? customer!.Trim() : null,
            },
            ["device"] = new JsonObject
            {
                ["name"] = d.DeviceName, ["manufacturer"] = d.Manufacturer, ["model"] = d.Model, ["windows"] = d.Windows,
                ["cpu"] = d.Cpu, ["ramGB"] = d.RamGB, ["gpu"] = d.Gpu,
            },
            ["summary"] = TextReport.Ticket(r),
            ["findings"] = new JsonArray(r.Findings.Where(f => f.Severity != Severity.Ok).Select(f => (JsonNode)new JsonObject
            {
                ["severity"] = f.Severity.ToString(), ["checkId"] = f.CheckId, ["title"] = f.Title, ["evidence"] = f.Evidence, ["advice"] = f.Advice,
            }).ToArray()),
            ["html"] = HtmlReport.Write(r, full: true),
            ["fileName"] = $"Troubleshootr {(device.Length > 0 ? device : d.DeviceName ?? "PC")} {r.StartedUtc.ToLocalTime():yyyy-MM-dd HHmm}.html",
        };
    }
}
