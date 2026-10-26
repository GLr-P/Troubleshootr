using System.Text;
using Troubleshootr.Checks;
using Troubleshootr.Checks.Network;
using Troubleshootr.Checks.Windows;
using Troubleshootr.Collectors;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 7: adapters, the connection ladder, Wi-Fi, redirection, Winsock and the clock.</summary>
public class NetworkTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();
    private static Severity? Sev(ICheck check, Snapshot s) => Run(check, s).SingleOrDefault()?.Severity;
    private static NetworkInfo Net(Snapshot s) => s.Network!.Value!;
    private static ConnectivityInfo Conn(Snapshot s) => s.Connectivity!.Value!;
    private static NetAdapter Eth(Snapshot s) => Net(s).Adapters[0];

    [Fact]
    public void HealthySnapshotHasNoNetworkFindings()
    {
        var s = Snap.Healthy();
        foreach (var check in CheckRegistry.All.Where(c => c.Category == Category.Network || c.Id == "WIN-012"))
            Assert.Empty(Run(check, s));
        Assert.Null(NetLadder.FirstFailure(NetLadder.Build(Net(s), Conn(s))));
    }

    [Fact]
    public void Net002Apipa()
    {
        var s = Snap.Healthy().With(x => { Eth(x).Ipv4 = ["169.254.10.3/16"]; Eth(x).Gateways = []; });
        var f = Assert.Single(Run(new Net002NoIp(), s));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains("169.254.10.3", f.Evidence);
        // Wi-Fi works, the Ethernet cable goes to a device: only a warning.
        s.With(x => Net(x).Adapters.Add(new NetAdapter { Name = "Wi-Fi", Type = "Wi-Fi", Status = "Up", Ipv4 = ["192.168.1.30/24"], Gateways = ["192.168.1.1"] }));
        Assert.Equal(Severity.Warning, Sev(new Net002NoIp(), s));
        // Down adapters keep stale 169.254 addresses; they don't count.
        var down = Snap.Healthy().With(x => Net(x).Adapters.Add(new NetAdapter { Name = "Wi-Fi 2", Type = "Wi-Fi", Status = "Down", Ipv4 = ["169.254.198.11/16"] }));
        Assert.Empty(Run(new Net002NoIp(), down));
    }

    [Fact]
    public void BridgesAndCellularCountAsConnected()
    {
        // Hyper-V external switch: the card has no IP, the vEthernet adapter has it.
        var hyperV = Snap.Healthy().With(x =>
        {
            Eth(x).Ipv4 = []; Eth(x).Gateways = []; Eth(x).DnsServers = [];
            Net(x).Adapters.Add(new NetAdapter { Name = "vEthernet (External)", Description = "Hyper-V Virtual Ethernet Adapter", Type = "Ethernet", Status = "Up", Ipv4 = ["192.168.1.20/24"], Gateways = ["192.168.1.1"] });
        });
        Assert.Empty(Run(new Net002NoIp(), hyperV));
        Assert.Empty(Run(new Net003Connection(), hyperV));
        var cellular = Snap.Healthy().With(x => Net(x).Adapters = [new NetAdapter { Name = "Cellular", Type = "Cellular", Status = "Up", Ipv4 = ["10.40.1.2/30"], Gateways = ["10.40.1.1"] }]);
        Assert.Empty(Run(new Net003Connection(), cellular));
        // IPv6-only or NAT64: no IPv4 gateway, but the internet works.
        var v6 = Snap.Healthy().With(x => Conn(x).Gateway = null);
        Assert.Empty(Run(new Net003Connection(), v6));
    }

    [Fact]
    public void Net003SaysWhichStepFails()
    {
        string? Title(Action<Snapshot> change) => Run(new Net003Connection(), Snap.Healthy().With(change)).SingleOrDefault()?.Title;

        Assert.Equal("Not connected to any network", Title(x => Eth(x).Status = "Down"));
        Assert.Equal("The router doesn't answer", Title(x =>
        {
            Conn(x).GatewayPing!.Received = 0; Conn(x).InternetPing!.Received = 0; Conn(x).InternetTcp = false; Conn(x).DnsOk = false; Conn(x).WebOk = false;
        }));
        Assert.Equal("Connected to the router, but no internet", Title(x =>
        {
            Conn(x).InternetPing!.Received = 0; Conn(x).InternetTcp = false; Conn(x).DnsOk = false; Conn(x).WebOk = false;
        }));
        Assert.Equal("The internet works but website names don't (DNS)", Title(x => { Conn(x).DnsOk = false; Conn(x).WebOk = false; }));
        Assert.Equal("Something is getting in the way of web pages", Title(x => { Conn(x).WebOk = false; Conn(x).WebProblem = "redirected"; }));

        // Routers that ignore ping and networks that block ICMP aren't failures when the internet works.
        Assert.Null(Title(x => Conn(x).GatewayPing!.Received = 0));
        Assert.Null(Title(x => { Conn(x).GatewayPing!.Received = 0; Conn(x).InternetPing!.Received = 0; }));
        // A failed IP step is NET-002's finding.
        Assert.Null(Title(x => Eth(x).Ipv4 = ["169.254.1.1/16"]));
    }

    [Fact]
    public void Net004LossAndLatency()
    {
        Assert.Empty(Run(new Net004LatencyLoss(), Snap.Healthy().With(x => Conn(x).GatewayPing!.Received = 19)));  // one lost ping is normal
        var lossy = Assert.Single(Run(new Net004LatencyLoss(), Snap.Healthy().With(x => Conn(x).GatewayPing!.Received = 16)));
        Assert.Equal("The connection to the router is unreliable", lossy.Title);
        Assert.Contains("20% of pings lost", lossy.Evidence);
        Assert.Equal("The internet connection is dropping or slow", Assert.Single(Run(new Net004LatencyLoss(), Snap.Healthy().With(x => Conn(x).InternetPing!.AvgMs = 240))).Title);
        Assert.Empty(Run(new Net004LatencyLoss(), Snap.Healthy().With(x => Conn(x).InternetPing!.Received = 0)));  // ping blocked
    }

    [Fact]
    public void Net005GigabitAt100()
    {
        Assert.Equal(Severity.Warning, Sev(new Net005SlowLink(), Snap.Healthy().With(x => Eth(x).SpeedMbps = 100)));
        Assert.Empty(Run(new Net005SlowLink(), Snap.Healthy().With(x => { Eth(x).SpeedMbps = 100; Eth(x).Description = "USB 10/100 LAN"; })));
    }

    [Fact]
    public void Net006WeakWifi()
    {
        WifiConnection W(int signal) => new() { State = "connected", SignalPercent = signal, Channel = 6, RxMbps = 58, TxMbps = 24 };
        Assert.Empty(Run(new Net006WifiSignal(), Snap.Healthy().With(x => Net(x).Wifi.Add(W(72)))));
        Assert.Contains("on 2.4 GHz", Assert.Single(Run(new Net006WifiSignal(), Snap.Healthy().With(x => Net(x).Wifi.Add(W(31))))).Evidence);
        Assert.Equal("6 GHz", new WifiConnection { Channel = 37, FrequencyMHz = 6135 }.Band);
        Assert.Equal("5 GHz", new WifiConnection { Channel = 161, FrequencyMHz = 5805 }.Band);
    }

    [Fact]
    public void Net007ProxyHostsAndDns()
    {
        Assert.Equal("Web traffic goes through a proxy", Assert.Single(Run(new Net007Redirection(), Snap.Healthy().With(x => { Net(x).Proxy.Enabled = true; Net(x).Proxy.Server = "127.0.0.1:8080"; }))).Title);

        var hijack = Snap.Healthy().With(x => { Net(x).Hosts = [new() { Address = "0.0.0.0", Host = "update.microsoft.com" }, new() { Address = "185.1.2.3", Host = "www.paypal.com" }]; Net(x).HostsCount = 2; });
        var f = Assert.Single(Run(new Net007Redirection(), hijack));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("www.paypal.com -> 185.1.2.3", f.Evidence);
        var own = Snap.Healthy().With(x => { Net(x).Hosts = [new() { Address = "100.108.4.54", Host = "desktop" }]; Net(x).HostsCount = 1; });
        Assert.Equal(Severity.Info, Sev(new Net007Redirection(), own));
        // Ad-block lists block ad and telemetry hosts under google/facebook/microsoft: not a hijack.
        var adblock = Snap.Healthy().With(x =>
        {
            Net(x).Hosts = [new() { Address = "0.0.0.0", Host = "pixel.facebook.com" }, new() { Address = "0.0.0.0", Host = "vortex.data.microsoft.com" }, new() { Address = "0.0.0.0", Host = "ads.google.com" }];
            Net(x).HostsCount = 4200;
        });
        Assert.Contains("ad-blocking", Assert.Single(Run(new Net007Redirection(), adblock)).Advice);
        // A hijack at the end of a long file still makes the 200 kept.
        var text = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"0.0.0.0 ad{i}.example.com")) + "\n45.83.10.2 www.paypal.com";
        var kept = NetParsers.Hosts(text).OrderByDescending(h => h.Suspicious).Take(200).ToList();
        Assert.Equal("www.paypal.com", kept[0].Host);

        Assert.Empty(Run(new Net007Redirection(), Snap.Healthy().With(x => { Eth(x).StaticDns = true; Eth(x).DnsServers = ["1.1.1.1", "8.8.8.8"]; })));
        Assert.Equal("DNS servers were set by hand", Assert.Single(Run(new Net007Redirection(), Snap.Healthy().With(x => { Eth(x).StaticDns = true; Eth(x).DnsServers = ["85.255.112.10"]; }))).Title);
        Assert.Empty(Run(new Net007Redirection(), Snap.Healthy().With(x => Eth(x).DnsServers = ["85.255.112.10"])));  // from the router: the provider's DNS
    }

    [Fact]
    public void Net008And009()
    {
        var s = Snap.Healthy().With(x =>
        {
            Net(x).Adapters.Add(new NetAdapter { Name = "Tailscale", Description = "Tailscale Tunnel", Status = "Up", Type = "Other" });
            x.Devices!.Value!.Devices.Add(new DeviceEntry { Name = "Realtek USB GbE", Class = "Net", ProblemCode = 22 });
        });
        Assert.Equal(["1 network adapter is switched off", "A VPN is connected"], Run(new Net008OtherAdapters(), s).Select(f => f.Title));
        Assert.False(Net(s).Adapters[1].IsPhysical);

        var lsp = Snap.Healthy().With(x => Net(x).Winsock.Add(new WinsockProvider { Catalog = "Protocol", Path = @"C:\Program Files\Accel\lsp.dll" }));
        Assert.Equal(Severity.Warning, Sev(new Net009Winsock(), lsp));
        var bonjour = Snap.Healthy().With(x => Net(x).Winsock.Add(new WinsockProvider { Catalog = "Name space", Path = @"C:\Program Files\Bonjour\mdnsNSP.dll" }));
        Assert.Equal(Severity.Info, Sev(new Net009Winsock(), bonjour));
    }

    [Fact]
    public void Win012Clock()
    {
        Assert.Equal("The clock is 5 minutes fast", Assert.Single(Run(new Win012Clock(), Snap.Healthy().With(x => Conn(x).ClockOffsetSeconds = 300))).Title);
        Assert.Equal(Severity.Critical, Sev(new Win012Clock(), Snap.Healthy().With(x => Conn(x).ClockOffsetSeconds = -3 * 86400)));
        Assert.Equal("Windows doesn't keep the clock in sync", Assert.Single(Run(new Win012Clock(), Snap.Healthy().With(x => Conn(x).TimeServiceStart = 4))).Title);
        Assert.Empty(Run(new Win012Clock(), Snap.Healthy().With(x => Conn(x).ClockOffsetSeconds = null)));
    }

    [Fact]
    public void Parsers()
    {
        var direct = new byte[] { 0x28, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Null(NetParsers.WinHttpProxy(direct));
        var proxy = "proxy.corp:8080";
        var bytes = new List<byte> { 0x28, 0, 0, 0, 5, 0, 0, 0, 3, 0, 0, 0 };
        bytes.AddRange(BitConverter.GetBytes(proxy.Length));
        bytes.AddRange(Encoding.ASCII.GetBytes(proxy));
        bytes.AddRange(new byte[4]);
        Assert.Equal(proxy, NetParsers.WinHttpProxy(bytes.ToArray()));

        var hosts = NetParsers.Hosts("# comment\r\n127.0.0.1 localhost\r\n::1 localhost\r\n0.0.0.0 ads.example.com tracker.example.com # block\r\n10.0.0.5\tnas\r\nnot-an-ip foo\r\n");
        Assert.Equal(["ads.example.com", "tracker.example.com", "nas"], hosts.Select(h => h.Host));
        Assert.True(hosts[0].Blocks);

        var packed = new byte[400];
        Encoding.ASCII.GetBytes(@"%SystemRoot%\system32\mswsock.dll").CopyTo(packed, 0);
        Assert.Equal(@"%SystemRoot%\system32\mswsock.dll", NetParsers.WinsockPath(packed));
        Assert.True(NetParsers.IsStandardWinsock(@"%SystemRoot%\System32\winrnr.dll"));
        Assert.False(NetParsers.IsStandardWinsock(@"C:\Program Files\Bonjour\mdnsNSP.dll"));
        Assert.False(NetParsers.IsStandardWinsock(@"C:\Temp\mswsock.dll"));
    }

    [Fact]
    public void AnonymizerBlanksNetworkIdentifiers()
    {
        var s = Snap.Healthy().With(x =>
        {
            Eth(x).Mac = "10:91:d1:f1:f7:2a"; Eth(x).Ipv6 = ["2001:db8::1234"];
            Net(x).Wifi.Add(new WifiConnection { State = "connected", Ssid = "Home", Bssid = "74:90:bc:19:9a:a9" });
            Net(x).Hosts = [new() { Address = "100.108.4.54", Host = "mydesktop" }];
            Eth(x).DnsServers = ["2001:569:abcd:1200::1", "2606:4700:4700::1111", "192.168.1.1"];
            Net(x).Adapters.Add(new NetAdapter { Name = "Tailscale", Description = "Tailscale Tunnel", Status = "Up", Ipv4 = ["100.111.199.65/32"] });
        });
        Anonymizer.Apply(s);
        Assert.Equal([Anonymizer.Redacted, "2606:4700:4700::1111", "192.168.1.1"], Eth(s).DnsServers);
        Assert.Equal([Anonymizer.Redacted], Net(s).Adapters[1].Ipv4);
        Assert.Equal(Anonymizer.Redacted, Net(s).Hosts[0].Address);
        Assert.Equal(Anonymizer.Redacted, Eth(s).Mac);
        Assert.Equal([Anonymizer.Redacted], Eth(s).Ipv6);
        Assert.Equal(Anonymizer.Redacted, Net(s).Wifi[0].Ssid);
        Assert.Equal(Anonymizer.Redacted, Net(s).Hosts[0].Host);
        Assert.Equal(["192.168.1.20/24"], Eth(s).Ipv4);
    }
}
