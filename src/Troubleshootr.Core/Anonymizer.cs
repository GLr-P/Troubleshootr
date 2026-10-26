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

    private static readonly string[] PublicIpv6Dns = ["2606:4700:", "2001:4860:", "2620:fe:", "2620:119:"];

    private static bool IsPersonalIpv6(string ip) =>
        ip.Contains(':') && !ip.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase) && !ip.StartsWith("fd", StringComparison.OrdinalIgnoreCase)
        && !PublicIpv6Dns.Any(p => ip.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>100.64.0.0/10: carrier-grade NAT, which Tailscale and other mesh VPNs use for device addresses.</summary>
    private static bool IsCgnat(string ip) => ip.StartsWith("100.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 64 and <= 127;

    private static readonly System.Text.RegularExpressions.Regex UserPath =
        new(@"(?<=\\Users\\)(?!Public\\|Default\\|All Users\\)[^\\""<>|]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
        foreach (var n in names)
            text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(n), "[redacted]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
