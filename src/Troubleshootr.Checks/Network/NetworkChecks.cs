using Troubleshootr.Core;

namespace Troubleshootr.Checks.Network;

internal static class Net
{
    public static string Speed(long mbps) => NetLadder.Speed(mbps);

    public static List<NetAdapter> Active(NetworkInfo n) => n.Active;
}

/// <summary>NET-002: a connected adapter only has a 169.254.x.x address, so the router's DHCP didn't answer.</summary>
public sealed class Net002NoIp : Check
{
    public override string Id => "NET-002";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Network)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var n = s.Network!.Value!;
        var active = Net.Active(n);
        // Only 169.254.x.x counts: a card with no address at all is usually bound to a Hyper-V switch or a bridge that holds the IP.
        var broken = active.Where(a => a.OnlyApipa).ToList();
        if (broken.Count == 0) yield break;
        // If another adapter works, this one is probably a direct cable to a device (which also gets 169.254.x.x).
        var othersWork = active.Except(broken).Any(a => a.HasUsableIp && a.Gateways.Count > 0);
        var names = string.Join("; ", broken.Select(a => $"{a.Name}: {string.Join(", ", a.Ipv4)}{(a.Dhcp == false ? " (set by hand)" : "")}"));
        yield return F(othersWork ? Severity.Warning : Severity.Critical, "Connected, but no IP address from the router",
            $"{names}. A 169.254.x.x address means Windows asked the router for an address and got no answer.",
            "Restart the router and this PC. If it's a cable, try another port or cable. If it keeps happening: Settings > Network & internet > Advanced network settings > Network reset.");
    }
}

/// <summary>NET-003: the connection ladder; says at which step it breaks.</summary>
public sealed class Net003Connection : Check
{
    public override string Id => "NET-003";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Network), nameof(Snapshot.Connectivity)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var steps = NetLadder.Build(s.Network!.Value!, s.Connectivity!.Value!);
        var fail = NetLadder.FirstFailure(steps);
        if (fail is null || fail.Name == "IP address") yield break;  // NET-002 reports the IP step
        var evidence = string.Join(" ", steps.TakeWhile(x => x != fail).Select(x => $"{x.Name}: OK.")) + $" {fail.Name}: {fail.Detail}";
        yield return fail.Name switch
        {
            "Network adapter" => F(Severity.Critical, "Not connected to any network", fail.Detail,
                "Plug in the network cable or join a Wi-Fi network. If Wi-Fi networks don't show up, check that Wi-Fi and airplane mode aren't switched off (the network icon by the clock)."),
            "Router (gateway)" => F(Severity.Critical, "The router doesn't answer", evidence.Trim(),
                "Restart the router (unplug it for 30 seconds). Check the cable or move closer to the Wi-Fi. If other devices work, run Network reset on this PC (Settings > Network & internet > Advanced network settings).", fix: "network-reset"),
            "Internet" => F(Severity.Critical, "Connected to the router, but no internet", evidence.Trim(),
                "The problem is past this PC: restart the modem and router, and check whether other devices have internet. If none do, call the internet provider."),
            "DNS (name lookup)" => F(Severity.Critical, "The internet works but website names don't (DNS)", evidence.Trim(),
                "Websites won't load by name. Restart the router; if that doesn't help, set the DNS server to automatic in the adapter's settings, or try 1.1.1.1. Flushing the DNS cache (ipconfig /flushdns) can also help.", fix: "dns-flush"),
            _ => F(Severity.Warning, "Something is getting in the way of web pages", evidence.Trim(),
                "On public Wi-Fi, open a browser and accept the sign-in page. Otherwise check the proxy settings and any web filter or security software that inspects traffic."),
        };
    }
}

/// <summary>NET-004: packet loss or slow replies from the router or the internet.</summary>
public sealed class Net004LatencyLoss : Check
{
    private const int LostPings = 2;          // of 20; one lost ping is normal on Wi-Fi
    private const double SlowRouterMs = 50;   // the router is a few metres away: a healthy link answers in under 10 ms
    private const double SlowInternetMs = 150;

    public override string Id => "NET-004";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Connectivity)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var c = s.Connectivity!.Value!;
        var problems = new List<string>();
        var local = false;
        // Zero replies means ping is blocked (or the link is down, which NET-003 reports); only partial loss counts.
        if (c.GatewayPing is { Received: > 0 } g)
        {
            if (g.Sent - g.Received >= LostPings) { problems.Add($"router {g.LossPercent:0}% of pings lost ({g.Sent - g.Received} of {g.Sent})"); local = true; }
            if (g.AvgMs > SlowRouterMs) { problems.Add($"router answers in {g.AvgMs:0} ms on average (max {g.MaxMs} ms)"); local = true; }
        }
        if (c.InternetPing is { Received: > 0 } i)
        {
            if (i.Sent - i.Received >= LostPings) problems.Add($"internet {i.LossPercent:0}% of pings lost ({i.Sent - i.Received} of {i.Sent})");
            if (i.AvgMs > SlowInternetMs) problems.Add($"internet answers in {i.AvgMs:0} ms on average (max {i.MaxMs} ms)");
        }
        if (problems.Count == 0) yield break;
        var wifi = s.Network?.Value is { } n && Net.Active(n).Any(a => a.Type == "Wi-Fi") && !Net.Active(n).Any(a => a.Type == "Ethernet");
        yield return F(Severity.Warning, local ? "The connection to the router is unreliable" : "The internet connection is dropping or slow",
            string.Join("; ", problems) + ".",
            local
                ? (wifi ? "Move closer to the router, or away from walls and microwaves; a 5 GHz network or a cable helps. Restarting the router clears most congestion." : "Try another network cable and router port, then restart the router.")
                : "The local network is fine, so the loss is at the provider or a busy line. Pause big downloads and streams, restart the modem, and call the provider if it keeps happening.");
    }
}

/// <summary>NET-005: a gigabit Ethernet adapter linked at 10 or 100 Mb/s (usually a damaged cable or a 2-pair cable).</summary>
public sealed class Net005SlowLink : Check
{
    private static readonly string[] GigabitWords = ["Gigabit", "GbE", "2.5G", "5G ", "10G", "I219", "I225", "I226", "I210", "I211", "Killer E", "RTL8125", "RTL8168", "AQC", "Aquantia", "Marvell AQtion", "1000"];

    public override string Id => "NET-005";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Network)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var a in Net.Active(s.Network!.Value!).Where(a => a.Type == "Ethernet" && a.SpeedMbps is > 0 and <= 100))
        {
            if (!GigabitWords.Any(w => a.Description?.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            yield return F(Severity.Warning, $"The network cable only runs at {Net.Speed(a.SpeedMbps)}",
                $"{a.Name} ({a.Description}) is a gigabit adapter but linked at {Net.Speed(a.SpeedMbps)}.",
                "A gigabit link needs all 8 wires: try another cable (Cat5e or better) and another port on the router. An old 10/100 switch in between also limits it.");
        }
    }
}

/// <summary>NET-006: weak Wi-Fi signal.</summary>
public sealed class Net006WifiSignal : Check
{
    private const int Weak = 40;   // Windows shows 1-2 bars below about 40%

    public override string Id => "NET-006";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Network)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var w in s.Network!.Value!.Wifi.Where(w => w.Connected && w.SignalPercent > 0 && w.SignalPercent < Weak))
            yield return F(Severity.Warning, "Weak Wi-Fi signal",
                $"Signal {w.SignalPercent}%{(w.Rssi is { } r ? $" ({r} dBm)" : "")}{(w.Band is { } b ? $" on {b}" : "")}, link {w.RxMbps} Mb/s down / {w.TxMbps} Mb/s up.",
                "Move closer to the router or clear what's between them (walls, metal, aquariums). For a fixed PC, a mesh point, a better antenna or a cable is the lasting fix.");
    }
}

/// <summary>NET-007: proxy, hosts file and hand-set DNS servers, the three ways adware and malware redirect browsing.</summary>
public sealed class Net007Redirection : Check
{
    /// <summary>Public resolvers people pick on purpose; a hand-set DNS outside this list (and outside the local network) is suspect.</summary>
    private static readonly HashSet<string> KnownDns =
    [
        "1.1.1.1", "1.0.0.1", "1.1.1.2", "1.0.0.2", "1.1.1.3", "1.0.0.3", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112", "9.9.9.10", "9.9.9.11",
        "208.67.222.222", "208.67.220.220", "208.67.222.123", "208.67.220.123", "94.140.14.14", "94.140.15.15", "76.76.2.0", "76.76.10.0",
        "185.228.168.9", "185.228.169.9", "2606:4700:4700::1111", "2606:4700:4700::1001", "2001:4860:4860::8888", "2001:4860:4860::8844", "2620:fe::fe", "2620:fe::9",
    ];

    public override string Id => "NET-007";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Network)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var n = s.Network!.Value!;
        var p = n.Proxy;
        if (p.Any)
        {
            var parts = new List<string>();
            if (p.Enabled && !string.IsNullOrEmpty(p.Server)) parts.Add($"proxy server {p.Server}");
            if (!string.IsNullOrEmpty(p.AutoConfigUrl)) parts.Add($"setup script {p.AutoConfigUrl}");
            if (!string.IsNullOrEmpty(p.WinHttp)) parts.Add($"system (WinHTTP) proxy {p.WinHttp}");
            yield return F(Severity.Warning, "Web traffic goes through a proxy", string.Join("; ", parts) + ".",
                "Fine on a work or school PC that needs it. Otherwise adware may be watching or changing your browsing: switch it off in Settings > Network & internet > Proxy, then scan for malware.");
        }

        var hijacked = n.Hosts.Where(h => h.Suspicious).ToList();
        if (hijacked.Count > 0)
            yield return F(Severity.Warning, "The hosts file redirects or blocks important websites",
                string.Join(", ", hijacked.Take(8).Select(h => $"{h.Host} -> {h.Address}")) + (hijacked.Count > 8 ? $" and {hijacked.Count - 8} more" : "") + ".",
                "Malware edits the hosts file to fake websites or to stop Windows Update and antivirus. Unless you added these yourself, remove them (C:\\Windows\\System32\\drivers\\etc\\hosts, edit as admin) and scan for malware.");
        else if (n.HostsCount > 0)
            yield return F(Severity.Info, $"The hosts file has {N(n.HostsCount, "custom entry", "custom entries")}",
                string.Join(", ", n.Hosts.Take(6).Select(h => $"{h.Host} -> {h.Address}")) + (n.HostsCount > 6 ? $" and {n.HostsCount - 6} more" : "") + ".",
                n.HostsCount > 1000 ? "This looks like an ad-blocking list. Harmless, but it can slow name lookups." : "These names skip normal DNS. Fine if you or your software added them on purpose (development, ad blocking).");

        var suspect = Net.Active(n).Where(a => a.StaticDns)
            .SelectMany(a => a.DnsServers.Where(d => d != Anonymizer.Redacted && !KnownDns.Contains(d) && !IsLocal(d) && !a.Gateways.Contains(d)).Select(d => (a.Name, d))).ToList();
        if (suspect.Count > 0)
            yield return F(Severity.Warning, "DNS servers were set by hand",
                string.Join("; ", suspect.Select(x => $"{x.Name}: {x.d}")) + ". They aren't from your router or a well-known public DNS service.",
                "Malware (DNSChanger-type) sets its own DNS servers to send you to fake sites. If you didn't set these, change DNS back to automatic in the adapter's settings.");
    }

    private static bool IsLocal(string ip) =>
        ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("127.") || ip.StartsWith("169.254.") || ip.StartsWith("100.100.100.100") ||
        ip.StartsWith("172.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 16 and <= 31 ||
        ip.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase) || ip.StartsWith("fd", StringComparison.OrdinalIgnoreCase) || ip.StartsWith("fc", StringComparison.OrdinalIgnoreCase) || ip == "::1";
}

/// <summary>NET-008: adapters switched off, and VPN/virtual adapters in use (they change routing and DNS).</summary>
public sealed class Net008OtherAdapters : Check
{
    public override string Id => "NET-008";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Network)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var n = s.Network!.Value!;
        var disabled = (s.Devices?.Value?.Devices ?? []).Where(d => d.Class == "Net" && d.ProblemCode == 22).Select(d => d.Name).ToList();
        if (disabled.Count > 0)
            yield return F(Severity.Info, $"{N(disabled.Count, "network adapter is", "network adapters are")} switched off",
                string.Join(", ", disabled) + ".",
                "Fine if you turned it off on purpose. To use it: Settings > Network & internet > Advanced network settings > Enable.");
        var vpn = n.Adapters.Where(a => a.IsUp && a.IsVpn).Select(a => a.Description ?? a.Name).ToList();
        if (vpn.Count > 0)
            yield return F(Severity.Info, "A VPN is connected", string.Join(", ", vpn) + ".",
                "A VPN changes where traffic and DNS lookups go. If websites or local devices (printers, shares) don't work, try with the VPN disconnected.");
    }
}

/// <summary>NET-009: third-party Winsock providers (old LSPs from antivirus, adware or "internet accelerators") that can break connections.</summary>
public sealed class Net009Winsock : Check
{
    public override string Id => "NET-009";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Network)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var extra = s.Network!.Value!.Winsock.Where(w => !w.Standard).ToList();
        var proto = extra.Where(w => w.Catalog == "Protocol").ToList();
        if (proto.Count > 0)
            yield return F(Severity.Warning, "Third-party software is hooked into Windows networking (Winsock)",
                string.Join(", ", proto.Select(w => w.Path)) + ".",
                "Old security suites, adware and \"internet accelerators\" install these, and broken ones stop connections working. If the internet misbehaves, uninstall that program, or reset Winsock (netsh winsock reset, then restart).", fix: "network-reset");
        var ns = extra.Except(proto).ToList();
        if (ns.Count > 0)
            yield return F(Severity.Info, $"{N(ns.Count, "extra name-lookup provider")} installed",
                string.Join(", ", ns.Select(w => w.Path)) + ".",
                "Usually harmless (Apple Bonjour, VPN clients). If names don't resolve, uninstall the program that added it.");
    }
}
