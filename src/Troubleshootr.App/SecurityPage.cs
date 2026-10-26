using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>The Security tab: protection, threats, firewall and sharing, accounts, encryption, remote access, browser locks and suspicious tasks.</summary>
public static class SecurityPage
{
    public static List<InfoGroup> Build(Snapshot s)
    {
        if (s.Security?.Value is not { } x)
            return [new InfoGroup("Security") { Note = $"Not available: {s.WhyMissing(nameof(Snapshot.Security))}. Run a scan with Security selected." }];
        var groups = new List<InfoGroup>();

        var av = new InfoGroup("Antivirus") { Columns = ["Product", "Status", "Definitions"] };
        if (!x.SecurityCenter) av.Note = "Windows Security Center isn't available (Windows Server).";
        foreach (var a in x.Antivirus) av.Table.Add([a.Name, a.Enabled ? "On" : "Off", a.UpToDate ? "Up to date" : "Out of date"]);
        groups.Add(av);

        if (x.Defender is { } d)
            groups.Add(new InfoGroup("Microsoft Defender")
                .Row("Mode", d.RunningMode switch { "Normal" => "Active (the main antivirus)", "Passive Mode" or "SxS Passive Mode" => "Passive (another antivirus is in charge)", var m => m })
                .Row("Real-time protection", d.RealTimeProtection)
                .Row("Tamper protection", d.TamperProtection)
                .Row("Definitions updated", d.SignaturesUpdatedUtc is { } u ? $"{s.Meta.Local(u):yyyy-MM-dd HH:mm}{(d.SignatureAgeDays is { } age ? $" ({age} days ago)" : "")}" : null));

        var threats = new InfoGroup("Threats found (last 30 days)") { Columns = ["Threat", "Found", "Result", "Where"] };
        foreach (var t in x.Threats)
            threats.Table.Add([t.Name, t.DetectedUtc is { } w ? s.Meta.Local(w).ToString("yyyy-MM-dd HH:mm") : "", t.Active ? "Still present" : t.Removed == false ? "Removal failed" : "Removed", t.Location ?? ""]);
        groups.Add(threats);

        groups.Add(new InfoGroup("Firewall and sharing")
            .Row("Windows Firewall", $"Public {OnOff(x.FirewallPublic)}, private {OnOff(x.FirewallPrivate)}, domain {OnOff(x.FirewallDomain)}")
            .Row("Other firewall", x.Firewalls.Count > 0 ? string.Join(", ", x.Firewalls) : "None")
            .Row("Remote Desktop", x.RdpEnabled switch { true => $"On{(x.RdpNla == false ? ", without Network Level Authentication" : "")}{(x.RdpPort is { } p && p != 3389 ? $", port {p}" : "")}", false => "Off", null => null })
            .Row("SMB1 (old file sharing)", x.Smb1Server == true || x.Smb1Client == true ? $"On ({string.Join(", ", new[] { x.Smb1Server == true ? "server" : null, x.Smb1Client == true ? "client" : null }.Where(v => v is not null))})" : "Off")
            .Row("Listening ports", x.ListeningPorts.Count > 0 ? string.Join(", ", x.ListeningPorts) : "None of the watched ones")
            .Row("User Account Control", x.UacOn == false ? "Off" : x.UacAdminPrompt == 0 ? "On, never asks" : "On"));

        var hw = new InfoGroup("Encryption and hardware")
            .Row("TPM", x.TpmVersion switch { null => "Unknown", "" => "None found", var v => v })
            .Row("Secure Boot", s.Board?.Value?.SecureBoot)
            .Row("Firmware", s.Board?.Value?.FirmwareType);
        foreach (var b in x.BitLocker) hw.Row($"BitLocker {b.Drive}", b.Status);
        groups.Add(hw);

        var accounts = new InfoGroup("Local accounts") { Columns = ["Account", "Type", "Status", "Last sign-in"] };
        foreach (var a in x.Accounts.OrderBy(a => a.Disabled).ThenByDescending(a => a.Admin))
            accounts.Table.Add([a.Name, a.Admin ? "Administrator" : a.Rid == 501 ? "Guest" : "Standard", a.Disabled ? "Disabled" : "Enabled", a.LastLogonUtc is { } l ? s.Meta.Local(l).ToString("yyyy-MM-dd") : ""]);
        groups.Add(accounts);

        var remote = new InfoGroup("Remote-access apps") { Columns = ["App", "Installed", "Running"] };
        foreach (var (name, p, running) in RemoteTools.Find(s.Software?.Value?.Programs ?? [], x.RemoteToolsRunning))
            remote.Table.Add([name, p is null ? "" : p.InstallDate?.ToString("yyyy-MM-dd") ?? "Yes", running ? "Yes" : "No"]);
        groups.Add(remote);

        if (x.BrowserPolicies.Count > 0)
        {
            var bp = new InfoGroup("Browser policies") { Columns = ["Browser", "Setting", "Value"] };
            foreach (var p in x.BrowserPolicies) bp.Table.Add([p.Browser, p.Kind, p.Value]);
            groups.Add(bp);
        }
        var tasks = new InfoGroup("Suspicious scheduled tasks") { Columns = ["Task", "Why", "Command"] };
        foreach (var t in x.SuspiciousTasks) tasks.Table.Add([t.Name, t.Reason, t.Command]);
        groups.Add(tasks);
        return groups;
    }

    private static string OnOff(bool? v) => v switch { true => "on", false => "OFF", null => "unknown" };
}
