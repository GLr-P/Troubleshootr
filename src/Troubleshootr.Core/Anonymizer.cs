using System.Collections;
using System.Reflection;

namespace Troubleshootr.Core;

/// <summary>Blanks every [Sensitive] string in the snapshot, so reports and fixtures can be shared.</summary>
public static class Anonymizer
{
    public const string Redacted = "[redacted]";

    public static void Apply(Snapshot s)
    {
        // Free text (event messages, file paths) can mention the user or PC by name; replace those names wherever they appear.
        var sys = s.System?.Value;
        var names = new[] { sys?.UserName, sys?.DeviceName, sys?.Domain, Environment.UserName, Environment.MachineName }
            .Where(n => n is { Length: >= 3 } && n != Redacted).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length).ToList();
        Walk(s, names);
        // Bluetooth devices are named by their owners ("Sam's AirPods") and their ids hold MAC addresses.
        foreach (var d in (s.Devices?.Value?.Devices ?? []).Concat(s.Devices?.Value?.Hidden ?? []).Where(IsBluetoothPeer))
        {
            d.Name = "Bluetooth device";
            d.InstanceId = Redacted;
            d.HardwareId = Redacted;
        }
        // Network printers put the end of their MAC address in their name ("HP732C80", "[732C80]").
        foreach (var d in (s.Devices?.Value?.Devices ?? []).Concat(s.Devices?.Value?.Hidden ?? []))
            d.Name = PrinterMac.Replace(d.Name, m => m.Groups[1].Value + "XXXXXX" + m.Groups[2].Value);
        if (s.Peripherals?.Value is { } per)
        {
            // Bluetooth headsets and phones show up as sound devices under their owner's name ("Sam's iPhone Hands-Free").
            foreach (var a in per.Audio)
            {
                a.Name = Possessive.Replace(a.Name, "[redacted]'s ");
                if (a.Device is { } d) a.Device = Possessive.Replace(d, "[redacted]'s ");
            }
            per.Microphone.Devices = per.Microphone.Devices.Select(d => Possessive.Replace(d, "[redacted]'s ")).ToList();
            foreach (var p in per.Printers) p.Name = PrinterMac.Replace(p.Name, m => m.Groups[1].Value + "XXXXXX" + m.Groups[2].Value);
            per.Camera.Devices = per.Camera.Devices.Select(d => PrinterMac.Replace(d, m => m.Groups[1].Value + "XXXXXX" + m.Groups[2].Value)).ToList();
        }
        // A router's global IPv6 DNS address carries the household's prefix; a VPN address (and the tailnet hosts it maps) names the account.
        if (s.Network?.Value is { } net)
        {
            foreach (var a in net.Adapters)
            {
                a.DnsServers = a.DnsServers.Select(d => IsPersonalIpv6(d) ? Redacted : d).ToList();
                if (a.IsVpn) a.Ipv4 = a.Ipv4.Select(_ => Redacted).ToList();
            }
            foreach (var h in net.Hosts.Where(h => IsCgnat(h.Address))) h.Address = Redacted;
        }
        s.Meta.Anonymized = true;
    }

    /// <summary>
    /// An anonymized copy of a report (the original is untouched): the snapshot as <see cref="Apply(Snapshot)"/>, plus every
    /// name, serial or network name it held scrubbed out of the findings and fix output, which quote them.
    /// </summary>
    public static Report Apply(Report original)
    {
        var r = Json.Deserialize<Report>(Json.Serialize(original));
        var secrets = new List<string>();
        if (r.Snapshot is { } s)
        {
            Collect(s, secrets);
            // Short user and PC names ("Sam") are below Collect's 4-character floor; Apply(Snapshot) scrubs them from 3.
            if (s.System?.Value is { } sys) secrets.AddRange(new[] { sys.UserName, sys.DeviceName, sys.Domain }.Where(n => n is not null)!);
            // VPN and tailnet addresses aren't [Sensitive] (most addresses are harmless) but Apply blanks them, so findings must too.
            if (s.Network?.Value is { } net)
                secrets.AddRange(net.Hosts.Where(h => IsCgnat(h.Address)).Select(h => h.Address).Concat(net.Adapters.Where(a => a.IsVpn).SelectMany(a => a.Ipv4)));
            Apply(s);
        }
        secrets.AddRange(new[] { r.Device.DeviceName, Environment.UserName, Environment.MachineName }.Where(n => n is { Length: >= 3 })!);
        // Generic account names and firmware placeholders ("User", "Default string") would blank ordinary words in the advice.
        var names = secrets.Where(n => n.Length >= 3 && n != Redacted && !NotPersonal.Contains(n.Trim()) && n.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToList();
        r.Device.DeviceName = Redacted;
        r.Findings = r.Findings.Select(f => f with { Title = Scrub(f.Title, names), Evidence = Scrub(f.Evidence, names), Advice = Scrub(f.Advice, names) }).ToList();
        foreach (var x in r.FixesRun) { x.Summary = Scrub(x.Summary, names); if (x.Output is { } o) x.Output = Scrub(o, names); }
        return r;
    }

    /// <summary>Every [Sensitive] value in the snapshot (before it's blanked), so the same text can be found elsewhere.</summary>
    private static void Collect(object? node, List<string> found)
    {
        if (node is null || node is string || node.GetType().IsPrimitive || node is DateTime) return;
        if (node is IDictionary) return;
        if (node is IEnumerable list) { foreach (var item in list) Collect(item, found); return; }
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead || p.PropertyType.IsValueType) continue;
            var value = p.GetValue(node);
            if (p.GetCustomAttribute<SensitiveAttribute>() is not null)
            {
                // Very short values ("1", "N/A") would scrub ordinary numbers and words out of the findings.
                if (value is string { Length: >= 4 } str) found.Add(str);
                else if (value is List<string> strs) found.AddRange(strs.Where(x => x.Length >= 4));
            }
            else if (value is not string) Collect(value, found);
        }
    }

    private static readonly HashSet<string> NotPersonal = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "admin", "administrator", "owner", "guest", "home", "default", "workgroup", "default string", "none", "n/a",
        "to be filled by o.e.m.", "system serial number", "not specified", "not applicable", "chassis serial number", "base board serial number",
        "0123456789", "invalid", "unknown",
    };

    private static readonly string[] PublicIpv6Dns = ["2606:4700:", "2001:4860:", "2620:fe:", "2620:119:"];

    private static bool IsPersonalIpv6(string ip) =>
        ip.Contains(':') && !ip.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase) && !ip.StartsWith("fd", StringComparison.OrdinalIgnoreCase)
        && !PublicIpv6Dns.Any(p => ip.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>100.64.0.0/10: carrier-grade NAT, which Tailscale and other mesh VPNs use for device addresses.</summary>
    private static bool IsCgnat(string ip) => ip.StartsWith("100.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 64 and <= 127;

    private static readonly System.Text.RegularExpressions.Regex UserPath =
        new(@"(?<=\\Users\\)(?!Public\\|Default\\|All Users\\)[^\\""<>|]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"Alexander’s " / "Sam's ": a personal device name up to the possessive, at the start or inside "(…)".</summary>
    private static readonly System.Text.RegularExpressions.Regex Possessive = new(@"(?<=^|\()[^,()]{1,40}?['’]s\s");

    private static readonly System.Text.RegularExpressions.Regex PrinterMac = new(@"(\bHP|\[)[0-9A-F]{6}(\]|\b)");

    /// <summary>A user account's security id (S-1-5-21-domain-rid) identifies the PC and the user; task names and paths include it.</summary>
    private static readonly System.Text.RegularExpressions.Regex AccountSid = new(@"S-1-5-21(-\d+){1,5}");

    private static bool IsBluetoothPeer(DeviceEntry d) =>
        d.InstanceId is { } id && (id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase)
                                   || id.StartsWith(@"BTHLEDEVICE\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHHFENUM\", StringComparison.OrdinalIgnoreCase)
                                   || id.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("BTHLE", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>Removes user and device names from free text.</summary>
    public static string Scrub(string text, IReadOnlyList<string> names)
    {
        text = UserPath.Replace(text, "[user]");
        text = AccountSid.Replace(text, "S-1-5-21-[redacted]");
        // Whole words only, so a user named "Sam" doesn't turn "Samsung" into "[redacted]sung".
        foreach (var n in names)
            text = System.Text.RegularExpressions.Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(n)}(?![\p{{L}}\p{{N}}])", "[redacted]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    private static void Walk(object? node, IReadOnlyList<string> names)
    {
        if (node is null || node is string || node.GetType().IsPrimitive || node is DateTime) return;
        if (node is Dictionary<string, string> dict)
        {
            foreach (var k in dict.Keys.ToList()) dict[k] = Scrub(dict[k], names);
            return;
        }
        if (node is IEnumerable list) { foreach (var item in list) Walk(item, names); return; }
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            if (p.PropertyType == typeof(string))
            {
                if (!p.CanWrite || p.GetValue(node) is not string { Length: > 0 } value) continue;
                if (p.GetCustomAttribute<SensitiveAttribute>() is not null) p.SetValue(node, Redacted);
                else if (p.GetCustomAttribute<FreeTextAttribute>() is not null) p.SetValue(node, Scrub(value, names));
            }
            else if (p.PropertyType == typeof(List<string>) && p.GetCustomAttribute<FreeTextAttribute>() is not null && p.GetValue(node) is List<string> strings)
                for (var i = 0; i < strings.Count; i++) strings[i] = Scrub(strings[i], names);
            else if (p.PropertyType == typeof(List<string>) && p.GetCustomAttribute<SensitiveAttribute>() is not null && p.GetValue(node) is List<string> secret)
                for (var i = 0; i < secret.Count; i++) secret[i] = Redacted;
            else if (!p.PropertyType.IsValueType) Walk(p.GetValue(node), names);
        }
    }
}
