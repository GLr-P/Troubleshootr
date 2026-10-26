namespace Troubleshootr.Core;

/// <summary>Network adapters, Wi-Fi, proxy, hosts file and Winsock: what's configured (no traffic sent; see <see cref="ConnectivityInfo"/>).</summary>
public sealed class NetworkInfo
{
    /// <summary>Adapters Windows has loaded (GetAdaptersAddresses). Disabled adapters aren't here; they're in Devices with problem code 22.</summary>
    public List<NetAdapter> Adapters { get; set; } = new();
    public List<WifiConnection> Wifi { get; set; } = new();
    public ProxyInfo Proxy { get; set; } = new();
    /// <summary>Hosts file lines that map a name (localhost lines left out). At most 200 kept, suspicious ones first; <see cref="HostsCount"/> is the real total.</summary>
    public List<HostsEntry> Hosts { get; set; } = new();
    public int HostsCount { get; set; }
    /// <summary>Winsock provider DLLs (protocol and name-space catalogs), one per distinct path.</summary>
    public List<WinsockProvider> Winsock { get; set; } = new();

    /// <summary>
    /// Connected adapters that carry the PC's traffic: physical ones, cellular, and virtual ones that hold the route
    /// (a Hyper-V external switch or a bridge has the IP and gateway, not the network card under it). VPNs are left out.
    /// </summary>
    public List<NetAdapter> Active => Adapters.Where(a => a.IsUp && !a.IsVpn && (a.IsPhysical || a.Type == "Cellular" || a.Gateways.Count > 0 || a.HasIpv6Gateway)).ToList();
}

public sealed class NetAdapter
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>"Ethernet", "Wi-Fi", "Cellular", "Loopback", "Tunnel", "Other".</summary>
    public string Type { get; set; } = "Other";
    /// <summary>"Up", "Down", "Dormant", "NotPresent", "LowerLayerDown"...</summary>
    public string Status { get; set; } = "";
    public long SpeedMbps { get; set; }
    [Sensitive] public string? Mac { get; set; }
    public bool? Dhcp { get; set; }
    /// <summary>"192.168.1.20/24".</summary>
    public List<string> Ipv4 { get; set; } = new();
    /// <summary>Global (non link-local) IPv6 addresses; they identify the household, so the anonymizer blanks them.</summary>
    [Sensitive] public List<string> Ipv6 { get; set; } = new();
    /// <summary>IPv4 default gateways.</summary>
    public List<string> Gateways { get; set; } = new();
    public bool HasIpv6Gateway { get; set; }
    public List<string> DnsServers { get; set; } = new();
    /// <summary>DNS servers typed into the adapter's settings (not handed out by the router).</summary>
    public bool StaticDns { get; set; }
    [Sensitive] public string? DnsSuffix { get; set; }

    public bool IsUp => Status == "Up";
    public bool IsVirtual => !string.IsNullOrEmpty(Description) && VirtualWords.Any(w => Description!.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) || IsVpn;
    public bool IsVpn => VpnWords.Any(w => $"{Name} {Description}".IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
    public bool IsPhysical => (Type == "Ethernet" || Type == "Wi-Fi") && !IsVirtual;
    /// <summary>Has an IPv4 address that isn't the 169.254.x.x fallback, or a global IPv6 address.</summary>
    public bool HasUsableIp => Ipv4.Any(a => !a.StartsWith("169.254.")) || Ipv6.Count > 0;
    public bool OnlyApipa => Ipv4.Count > 0 && Ipv4.All(a => a.StartsWith("169.254.")) && Ipv6.Count == 0;

    private static readonly string[] VirtualWords =
        ["Hyper-V", "Virtual", "VMware", "VirtualBox", "Npcap", "Loopback", "Kernel Debug", "WAN Miniport", "Bluetooth", "Teredo", "6to4", "ISATAP", "Wi-Fi Direct", "Microsoft Network Adapter Multiplexor"];
    private static readonly string[] VpnWords =
        ["VPN", "TAP-", "TAP ", "Wintun", "WireGuard", "Tailscale", "ZeroTier", "OpenVPN", "AnyConnect", "PANGP", "Fortinet", "NordLynx", "Mullvad", "Juniper", "SonicWall", "Cloudflare WARP"];
}

public sealed class WifiConnection
{
    public string? Adapter { get; set; }
    /// <summary>"connected", "disconnected", "authenticating"...</summary>
    public string State { get; set; } = "";
    [Sensitive] public string? Ssid { get; set; }
    [Sensitive] public string? Bssid { get; set; }
    /// <summary>Windows' signal quality, 0-100.</summary>
    public int SignalPercent { get; set; }
    public int? Rssi { get; set; }
    public int? Channel { get; set; }
    public int? FrequencyMHz { get; set; }
    /// <summary>DOT11_PHY_TYPE: 7 = 802.11n, 8 = ac, 10 = ax, 11 = be.</summary>
    public int PhyType { get; set; }
    public int RxMbps { get; set; }
    public int TxMbps { get; set; }
    /// <summary>DOT11_AUTH_ALGORITHM: 1 open, 4 WPA-Personal, 7 WPA2-Personal, 9 WPA3-Personal, 6/11 Enterprise.</summary>
    public int? Auth { get; set; }

    /// <summary>Why the connection details couldn't be read (Windows 11 24H2+ hides them until location access is allowed).</summary>
    public string? Problem { get; set; }

    public bool Connected => State == "connected";

    public string? Band => FrequencyMHz switch
    {
        >= 5925 => "6 GHz", >= 4900 => "5 GHz", >= 2400 => "2.4 GHz",
        _ => Channel switch { null => null, <= 14 => "2.4 GHz", _ => "5 GHz" },
    };

    public string Standard => PhyType switch
    {
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "Wi-Fi 4 (802.11n)", 8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)", 11 => "Wi-Fi 7 (802.11be)", _ => "unknown",
    };

    public string? Security => Auth switch
    {
        null => null, 1 => "Open (no password)", 2 => "WEP", 3 => "WPA-Enterprise", 4 => "WPA-Personal", 6 => "WPA2-Enterprise", 7 => "WPA2-Personal",
        8 or 11 => "WPA3-Enterprise", 9 => "WPA3-Personal", 10 => "Enhanced Open (OWE)", var a => $"type {a}",
    };
}

public sealed class ProxyInfo
{
    /// <summary>Browsers and most apps (WinINET, per user).</summary>
    public bool Enabled { get; set; }
    [FreeText] public string? Server { get; set; }
    [FreeText] public string? AutoConfigUrl { get; set; }
    public bool? AutoDetect { get; set; }
    /// <summary>System services and Windows Update (WinHTTP, machine-wide). Null = direct.</summary>
    [FreeText] public string? WinHttp { get; set; }
    public bool Any => Enabled && !string.IsNullOrEmpty(Server) || !string.IsNullOrEmpty(AutoConfigUrl) || !string.IsNullOrEmpty(WinHttp);
}

public sealed class HostsEntry
{
    public string Address { get; set; } = "";
    /// <summary>Often names the owner's own devices, so the anonymizer blanks it.</summary>
    [Sensitive] public string Host { get; set; } = "";
    /// <summary>0.0.0.0 / 127.x / ::1: the name is blocked rather than redirected.</summary>
    public bool Blocks => Address is "0.0.0.0" or "::" or "::1" || Address.StartsWith("127.");

    /// <summary>Sending these elsewhere is how malware fakes sign-in and banking pages.</summary>
    private static readonly string[] RedirectWatch =
    [
        "microsoft.com", "windowsupdate.com", "windows.com", "live.com", "office.com", "google.com", "bing.com", "facebook.com", "apple.com", "paypal.com", "amazon.com",
    ];

    /// <summary>Blocking these is how malware stops Windows Update and antivirus. Ad-block lists block ad and telemetry hosts, not these.</summary>
    private static readonly string[] BlockWatch =
    [
        "windowsupdate.com", "update.microsoft.com", "download.microsoft.com", "wustat.windows.com", "go.microsoft.com",
        "symantec.com", "norton.com", "mcafee.com", "kaspersky.com", "avast.com", "avg.com", "eset.com", "bitdefender.com", "malwarebytes.com", "sophos.com", "trendmicro.com", "avira.com",
    ];

    private bool Under(string domain) => Host == domain || Host.EndsWith("." + domain, StringComparison.Ordinal) || Host == "www." + domain;

    /// <summary>An important site sent to another address, or an update/security site blocked.</summary>
    public bool Suspicious => Blocks ? BlockWatch.Any(Under) : RedirectWatch.Any(Under);
}

public sealed class WinsockProvider
{
    /// <summary>"Protocol" or "Name space".</summary>
    public string Catalog { get; set; } = "";
    [FreeText] public string Path { get; set; } = "";
    public bool Standard { get; set; }
}

/// <summary>Live tests: ping the router and the internet, look up a name, fetch Microsoft's connection-test page, and read the clock offset.</summary>
public sealed class ConnectivityInfo
{
    public string? Gateway { get; set; }
    public PingStats? GatewayPing { get; set; }
    /// <summary>The better-answering of 1.1.1.1 and 8.8.8.8 (each gets 20 pings), so a network that filters one isn't counted as loss.</summary>
    public PingStats? InternetPing { get; set; }
    /// <summary>A TCP connection to 1.1.1.1:443 (networks that block ping still allow this).</summary>
    public bool? InternetTcp { get; set; }
    public bool? DnsOk { get; set; }
    public long? DnsMs { get; set; }
    public string? DnsError { get; set; }
    /// <summary>The test page came back with the expected text.</summary>
    public bool? WebOk { get; set; }
    /// <summary>What went wrong fetching it ("redirected to ...", "timed out").</summary>
    [FreeText] public string? WebProblem { get; set; }

    /// <summary>The PC clock minus internet time, seconds (positive = PC is ahead).</summary>
    public double? ClockOffsetSeconds { get; set; }
    /// <summary>"time.windows.com (NTP)" or "web server date".</summary>
    public string? ClockSource { get; set; }
    /// <summary>W32Time Parameters\Type: NTP, NT5DS (domain), AllSync, NoSync.</summary>
    public string? TimeSyncType { get; set; }
    /// <summary>Windows Time service start type: 2 automatic, 3 manual (trigger-start), 4 disabled.</summary>
    public int? TimeServiceStart { get; set; }

    public const string TestHost = "www.msftconnecttest.com";
}

public sealed class PingStats
{
    public string Target { get; set; } = "";
    public int Sent { get; set; }
    public int Received { get; set; }
    public double AvgMs { get; set; }
    public long MaxMs { get; set; }

    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>The connection ladder (adapter, IP, router, internet, DNS, web), shared by NET-003 and the Network tab.</summary>
public static class NetLadder
{
    public enum Result { Pass, Fail, NoReply, NotTested }

    public sealed record Step(string Name, Result Result, string Detail);

    public static List<Step> Build(NetworkInfo n, ConnectivityInfo c)
    {
        var steps = new List<Step>();
        var up = n.Active;
        steps.Add(up.Count > 0
            ? new Step("Network adapter", Result.Pass, string.Join(", ", up.Select(a => $"{a.Name} connected{(a.SpeedMbps > 0 ? $" at {Speed(a.SpeedMbps)}" : "")}")))
            : new Step("Network adapter", Result.Fail, n.Adapters.Any(a => a.IsPhysical) ? "No cable plugged in and no Wi-Fi network joined." : "No network adapter found."));

        var withIp = up.Where(a => a.HasUsableIp).ToList();
        steps.Add(up.Count == 0 ? new Step("IP address", Result.NotTested, "")
            : withIp.Count > 0 ? new Step("IP address", Result.Pass, string.Join(", ", withIp.SelectMany(a => a.Ipv4.Where(i => !i.StartsWith("169.254."))).DefaultIfEmpty("IPv6 only")))
            : new Step("IP address", Result.Fail, up.Any(a => a.OnlyApipa) ? "Only a 169.254.x.x address: the router didn't hand out an address (DHCP failed)." : "No IP address."));

        var internetOk = c.InternetPing is { Received: > 0 } || c.InternetTcp == true || c.WebOk == true;
        if (c.Gateway is null)
            steps.Add(withIp.Count == 0 ? new Step("Router (gateway)", Result.NotTested, "")
                : internetOk ? new Step("Router (gateway)", Result.NoReply, "No IPv4 router address (the internet works, for example over IPv6)")
                : new Step("Router (gateway)", Result.Fail, "No default gateway is set."));
        else if (c.GatewayPing is { Received: > 0 } g) steps.Add(new Step("Router (gateway)", Result.Pass, $"{c.Gateway} answered in {g.AvgMs:0} ms"));
        else steps.Add(new Step("Router (gateway)", internetOk ? Result.NoReply : Result.Fail, internetOk ? $"{c.Gateway} doesn't answer ping (many routers don't; the internet works)" : $"{c.Gateway} doesn't answer"));

        steps.Add(new Step("Internet", internetOk ? Result.Pass : Result.Fail,
            c.InternetPing is { Received: > 0 } p ? $"{p.Target} answered in {p.AvgMs:0} ms"
            : internetOk ? "Reached (ping is blocked on this network)"
            : "Nothing on the internet answers (ping 1.1.1.1 / 8.8.8.8 and a connection to 1.1.1.1 both failed)."));

        steps.Add(c.DnsOk switch
        {
            true => new Step("DNS (name lookup)", Result.Pass, $"{ConnectivityInfo.TestHost} found in {c.DnsMs} ms"),
            false => new Step("DNS (name lookup)", Result.Fail, $"Looking up {ConnectivityInfo.TestHost} failed{(c.DnsError is null ? "" : $": {c.DnsError}")}."),
            _ => new Step("DNS (name lookup)", Result.NotTested, ""),
        });

        steps.Add(c.WebOk switch
        {
            true => new Step("Web", Result.Pass, "Microsoft's connection test page loaded"),
            false => new Step("Web", Result.Fail, c.WebProblem ?? "The test page didn't load."),
            _ => new Step("Web", Result.NotTested, ""),
        });
        return steps;
    }

    /// <summary>The first step that failed, or null when the connection works.</summary>
    public static Step? FirstFailure(List<Step> steps) => steps.FirstOrDefault(s => s.Result == Result.Fail);

    public static string Speed(long mbps) => mbps >= 1000 ? $"{mbps / 1000.0:0.#} Gb/s" : $"{mbps} Mb/s";
}
