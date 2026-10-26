using Troubleshootr.Collectors;
using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>The Network tab: the connection ladder, a speed test, Wi-Fi, adapters, proxy/DNS, hosts file and Winsock.</summary>
public static class NetPages
{
    public static List<InfoGroup> Network(Snapshot s)
    {
        var groups = new List<InfoGroup>();
        var n = s.Network?.Value;
        var c = s.Connectivity?.Value;
        if (n is null)
            return [new InfoGroup("Network") { Note = $"Not available: {s.WhyMissing(nameof(Snapshot.Network))}. Run a scan with Network selected." }];

        if (c is not null)
        {
            var steps = NetLadder.Build(n, c);
            var fail = NetLadder.FirstFailure(steps);
            var ladder = new InfoGroup("Connection test")
            {
                Badge = fail is null ? "Working" : $"Fails at: {fail.Name}",
                BadgeKey = fail is null ? "Ok" : "Critical",
            };
            foreach (var step in steps)
                ladder.Row(step.Name, step.Result switch
                {
                    NetLadder.Result.Pass => $"OK. {step.Detail}",
                    NetLadder.Result.Fail => $"Failed. {step.Detail}",
                    NetLadder.Result.NoReply => $"No reply. {step.Detail}",
                    _ => "Not tested",
                });
            ladder.Row("Router replies", Ping(c.GatewayPing))
                .Row("Internet replies", Ping(c.InternetPing))
                .Row("Clock", c.ClockOffsetSeconds is { } off ? $"{(Math.Abs(off) < 1 ? "correct" : $"{(Math.Abs(off) >= 120 ? $"{Math.Abs(off) / 60:0} minutes" : $"{Math.Abs(off):0.#} s")} {(off > 0 ? "fast" : "slow")}")} (checked against {c.ClockSource})" : null)
                .Row("Time sync", c.TimeServiceStart == 4 ? "Off (Windows Time service disabled)" : c.TimeSyncType switch { "NoSync" => "Off", "NT5DS" => "From the domain", null => null, var t => $"On ({t})" });
            groups.Add(ladder);
        }
        else groups.Add(new InfoGroup("Connection test") { Note = $"Not run: {s.WhyMissing(nameof(Snapshot.Connectivity))}." });

        groups.Add(new InfoGroup("Download speed")
        {
            Note = "Downloads test data from Cloudflare for up to 10 seconds (about 10-60 MB on a fast line). Only runs when you press the button.",
            Action = ("Test download speed", async ct =>
            {
                var (mbps, mb) = await SpeedTest.RunAsync(ct);
                return $"{mbps:0.#} Mb/s ({mb:0.#} MB downloaded). Wi-Fi and other devices using the internet lower the result.";
            }),
        });

        foreach (var w in n.Wifi.Where(w => w.Connected && w.Problem is not null))
            groups.Add(new InfoGroup("Wi-Fi") { Note = w.Problem }.Row("Status", "Connected").Row("Adapter", w.Adapter));
        foreach (var w in n.Wifi.Where(w => w.Connected && w.Problem is null))
            groups.Add(new InfoGroup("Wi-Fi")
                .Row("Network", w.Ssid)
                .Row("Signal", $"{w.SignalPercent}%{(w.Rssi is { } r ? $" ({r} dBm)" : "")}")
                .Row("Band and channel", $"{w.Band}{(w.Channel is { } ch ? $", channel {ch}" : "")}")
                .Row("Standard", w.Standard)
                .Row("Link speed", $"{w.RxMbps} Mb/s down, {w.TxMbps} Mb/s up")
                .Row("Security", w.Security)
                .Row("Access point", w.Bssid)
                .Row("Adapter", w.Adapter));
        foreach (var w in n.Wifi.Where(w => !w.Connected))
            groups.Add(new InfoGroup("Wi-Fi").Row("Status", $"Not connected ({w.State})").Row("Adapter", w.Adapter));

        var adapters = new InfoGroup("Adapters") { Columns = ["Name", "Type", "Status", "Speed", "IP address", "Gateway", "DNS servers", "Adapter"] };
        foreach (var a in n.Adapters)
            adapters.Table.Add([
                a.Name, a.IsVpn ? "VPN" : a.IsVirtual ? "Virtual" : a.Type, a.IsUp ? "Connected" : "Not connected",
                a.SpeedMbps > 0 ? NetLadder.Speed(a.SpeedMbps) : "",
                // A disconnected adapter keeps a leftover 169.254 address; it means nothing, so it isn't shown.
                a.IsUp ? string.Join(", ", a.Ipv4.Concat(a.Ipv6)) + (a.Dhcp == true && a.Ipv4.Count > 0 ? " (automatic)" : "") : "",
                string.Join(", ", a.Gateways), string.Join(", ", a.DnsServers) + (a.StaticDns ? " (set by hand)" : ""),
                (a.Description ?? "") + (a.Mac is { } mac ? $"\nMAC {mac}" : ""),
            ]);
        var switchedOff = (s.Devices?.Value?.Devices ?? []).Where(d => d.Class == "Net" && d.ProblemCode == 22).Select(d => d.Name).ToList();
        if (switchedOff.Count > 0) adapters.Note = $"Switched off: {string.Join(", ", switchedOff)}.";
        groups.Add(adapters);

        var p = n.Proxy;
        groups.Add(new InfoGroup("Proxy")
            .Row("Proxy server", p.Enabled && !string.IsNullOrEmpty(p.Server) ? p.Server : "None")
            .Row("Setup script (PAC)", p.AutoConfigUrl ?? "None")
            .Row("Detect settings automatically", p.AutoDetect)
            .Row("System proxy (WinHTTP)", p.WinHttp ?? "None (direct)"));

        if (n.HostsCount > 0)
        {
            var hosts = new InfoGroup($"Hosts file ({n.HostsCount} entries)") { Columns = ["Name", "Goes to"], Filterable = n.HostsCount > 20 };
            foreach (var h in n.Hosts) hosts.Table.Add([h.Host, h.Address + (h.Blocks ? " (blocked)" : "")]);
            if (n.HostsCount > n.Hosts.Count) hosts.Note = $"Showing the first {n.Hosts.Count}.";
            groups.Add(hosts);
        }

        var ws = new InfoGroup("Winsock providers") { Columns = ["Catalog", "File", "Source"] };
        foreach (var w in n.Winsock) ws.Table.Add([w.Catalog, w.Path, w.Standard ? "Windows" : "Third-party"]);
        groups.Add(ws);
        return groups;
    }

    private static string? Ping(PingStats? p) => p switch
    {
        null => null,
        { Received: 0 } => $"{p.Target}: no replies to {p.Sent} pings",
        _ => $"{p.Target}: {p.Received} of {p.Sent}, average {p.AvgMs:0} ms, slowest {p.MaxMs} ms",
    };
}
