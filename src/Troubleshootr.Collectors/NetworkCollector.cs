using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Network configuration: adapters (GetAdaptersAddresses via NetworkInterface), Wi-Fi (wlanapi), proxy, hosts file, Winsock catalog.</summary>
public sealed class NetworkCollector : Collector<NetworkInfo>
{
    private const string Tcpip = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string WinsockRoot = @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\";

    public override string Section => nameof(Snapshot.Network);

    protected override NetworkInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new NetworkInfo { Adapters = Adapters() };
        try { info.Wifi = Wlan.Connections(); }
        catch (CollectorException) { }
        info.Proxy = Proxy();
        Hosts(info);
        info.Winsock = Winsock();
        return info;
    }

    private static List<NetAdapter> Adapters()
    {
        var list = new List<NetAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var a = new NetAdapter
            {
                Name = nic.Name,
                Description = nic.Description,
                Type = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Wman or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
                    NetworkInterfaceType.Tunnel => "Tunnel",
                    NetworkInterfaceType.Ppp => "Dial-up/VPN",
                    _ => "Other",
                },
                Status = nic.OperationalStatus.ToString(),
            };
            try
            {
                a.SpeedMbps = nic.Speed > 0 && nic.OperationalStatus == OperationalStatus.Up ? nic.Speed / 1_000_000 : 0;
                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length == 6) a.Mac = string.Join(":", mac.Select(b => b.ToString("x2")));
                var ip = nic.GetIPProperties();
                foreach (var u in ip.UnicastAddresses)
                {
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) a.Ipv4.Add($"{u.Address}/{Prefix(u)}");
                    else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6SiteLocal && !IsUniqueLocal(u.Address))
                        a.Ipv6.Add(u.Address.ToString());
                }
                foreach (var g in ip.GatewayAddresses)
                {
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)) a.Gateways.Add(g.Address.ToString());
                    else if (g.Address.AddressFamily == AddressFamily.InterNetworkV6) a.HasIpv6Gateway = true;
                }
                a.DnsServers = ip.DnsAddresses.Where(d => !d.IsIPv6SiteLocal).Select(d => d.ToString().Split('%')[0]).Distinct().ToList();
                a.DnsSuffix = string.IsNullOrWhiteSpace(ip.DnsSuffix) ? null : ip.DnsSuffix;
                try { a.Dhcp = ip.GetIPv4Properties()?.IsDhcpEnabled; }
                catch (NetworkInformationException) { }
                a.StaticDns = Reg.Str(RegistryHive.LocalMachine, Tcpip + nic.Id, "NameServer") is not null;
            }
            catch (NetworkInformationException) { }
            list.Add(a);
        }
        return list.OrderByDescending(a => a.IsUp).ThenByDescending(a => a.IsPhysical).ThenBy(a => a.Name).ToList();
    }

    private static int Prefix(UnicastIPAddressInformation u)
    {
        try { return u.PrefixLength; }
        catch (Exception e) when (e is NotImplementedException or PlatformNotSupportedException)
        {
            var bytes = u.IPv4Mask?.GetAddressBytes() ?? [];
            return bytes.Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
        }
    }

    private static bool IsUniqueLocal(System.Net.IPAddress a) => (a.GetAddressBytes()[0] & 0xFE) == 0xFC;  // fc00::/7

    private static ProxyInfo Proxy()
    {
        const string inet = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        var p = new ProxyInfo
        {
            Enabled = Reg.Num(RegistryHive.CurrentUser, inet, "ProxyEnable") == 1,
            Server = Reg.Str(RegistryHive.CurrentUser, inet, "ProxyServer"),
            AutoConfigUrl = Reg.Str(RegistryHive.CurrentUser, inet, "AutoConfigURL"),
        };
        if (Reg.Get(RegistryHive.CurrentUser, inet + @"\Connections", "DefaultConnectionSettings") is byte[] d && d.Length > 8)
            p.AutoDetect = (d[8] & 0x08) != 0;
        if (Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections", "WinHttpSettings") is byte[] w)
            p.WinHttp = NetParsers.WinHttpProxy(w);
        return p;
    }

    private static void Hosts(NetworkInfo info)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        try
        {
            var all = NetParsers.Hosts(File.ReadAllText(path));
            info.HostsCount = all.Count;
            info.Hosts = all.OrderByDescending(h => h.Suspicious).Take(200).ToList();  // stable sort: a hijack at line 5,000 still makes the cut
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static List<WinsockProvider> Winsock()
    {
        var seen = new Dictionary<string, WinsockProvider>(StringComparer.OrdinalIgnoreCase);
        void Add(string catalog, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || seen.ContainsKey(path!)) return;
            seen[path!] = new WinsockProvider { Catalog = catalog, Path = path!, Standard = NetParsers.IsStandardWinsock(path!) };
        }
        foreach (var cat in new[] { "Catalog_Entries", "Catalog_Entries64" })
        {
            var proto = WinsockRoot + @"Protocol_Catalog9\" + cat;
            foreach (var k in Reg.SubKeys(RegistryHive.LocalMachine, proto))
                if (Reg.Get(RegistryHive.LocalMachine, $@"{proto}\{k}", "PackedCatalogItem") is byte[] b) Add("Protocol", NetParsers.WinsockPath(b));
            var ns = WinsockRoot + @"NameSpace_Catalog5\" + cat;
            foreach (var k in Reg.SubKeys(RegistryHive.LocalMachine, ns))
                Add("Name space", Reg.Get(RegistryHive.LocalMachine, $@"{ns}\{k}", "LibraryPath") as string);
        }
        return seen.Values.OrderBy(p => p.Standard).ThenBy(p => p.Catalog).ThenBy(p => p.Path).ToList();
    }
}

/// <summary>Parsers for network settings, kept separate so tests can feed them bytes and text.</summary>
public static class NetParsers
{
    /// <summary>WinHttpSettings: [size][counter][flags][proxy length][proxy ANSI][bypass length][bypass]. Flag 2 = proxy set. Null = direct.</summary>
    public static string? WinHttpProxy(byte[] b)
    {
        if (b.Length < 16) return null;
        var flags = BitConverter.ToInt32(b, 8);
        var len = BitConverter.ToInt32(b, 12);
        if ((flags & 2) == 0 || len <= 0 || 16 + len > b.Length) return null;
        return Encoding.ASCII.GetString(b, 16, len).Trim();
    }

    private static readonly HashSet<string> LocalNames = new(StringComparer.OrdinalIgnoreCase)
        { "localhost", "localhost.localdomain", "local", "ip6-localhost", "ip6-loopback", "broadcasthost" };

    /// <summary>Every name the hosts file maps, except the standard localhost lines.</summary>
    public static List<HostsEntry> Hosts(string text)
    {
        var list = new List<HostsEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ' ', '\t' },StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !System.Net.IPAddress.TryParse(parts[0], out _)) continue;
            foreach (var host in parts.Skip(1))
                if (!LocalNames.Contains(host)) list.Add(new HostsEntry { Address = parts[0], Host = host.ToLowerInvariant() });
        }
        return list;
    }

    /// <summary>PackedCatalogItem starts with the provider DLL path as a null-terminated ANSI string (MAX_PATH bytes).</summary>
    public static string? WinsockPath(byte[] b)
    {
        var end = Array.IndexOf(b, (byte)0);
        if (end <= 0) return null;
        return Encoding.Default.GetString(b, 0, Math.Min(end, 260));
    }

    private static readonly HashSet<string> WindowsProviders = new(StringComparer.OrdinalIgnoreCase)
        { "mswsock.dll", "napinsp.dll", "pnrpnsp.dll", "winrnr.dll", "nlaapi.dll", "nlansp_c.dll", "wshbth.dll", "vsocklib.dll", "rsvpsp.dll", "wshqos.dll", "hvsocket.dll", "wshhyperv.dll", "wshtcpip.dll" };

    /// <summary>A Windows provider: a known file name in the Windows system folder.</summary>
    public static bool IsStandardWinsock(string path)
    {
        var p = path.Replace('/', '\\');
        var name = p.Substring(p.LastIndexOf('\\') + 1);
        var dir = p.Substring(0, Math.Max(0, p.LastIndexOf('\\')));
        var inSystem = dir.EndsWith(@"\system32", StringComparison.OrdinalIgnoreCase) || dir.EndsWith(@"\SysWOW64", StringComparison.OrdinalIgnoreCase);
        return inSystem && WindowsProviders.Contains(name);
    }
}
