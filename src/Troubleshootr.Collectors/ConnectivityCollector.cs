using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>
/// Live connection tests, all run at once so the whole thing takes about 1.5 s: 20 pings each to the router and the
/// internet, a TCP connection to 1.1.1.1:443, a DNS lookup, Microsoft's connection-test page (the one Windows uses to
/// show "No internet"), and the clock offset from time.windows.com.
/// </summary>
public sealed class ConnectivityCollector : Collector<ConnectivityInfo>
{
    private const int Pings = 20;
    private const int PingSpacingMs = 50;
    private const int PingTimeoutMs = 1000;
    private static readonly string[] InternetTargets = ["1.1.1.1", "8.8.8.8"];
    private const string TestUrl = "http://" + ConnectivityInfo.TestHost + "/connecttest.txt";
    private const string TestText = "Microsoft Connect Test";

    public override string Section => nameof(Snapshot.Connectivity);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(8);

    protected override ConnectivityInfo Collect(CollectContext ctx, CancellationToken ct) => CollectAsync(ct).GetAwaiter().GetResult();

    private static async Task<ConnectivityInfo> CollectAsync(CancellationToken ct)
    {
        var c = new ConnectivityInfo { Gateway = DefaultGateway() };

        var gateway = c.Gateway is null ? Task.FromResult<PingStats?>(null) : PingMany(c.Gateway, ct);
        var internet = InternetTargets.Select(t => PingMany(t, ct)).ToList();
        var tcp = TcpReachable("1.1.1.1", 443);
        var dns = Task.Run(() => DnsTest());
        var web = WebTest(ct);
        var clock = Sntp("time.windows.com");

        // Every test has its own time limit; this deadline is the backstop, so one stuck test can't lose the whole section.
        var all = Task.WhenAll(new Task[] { gateway, tcp, dns, web, clock }.Concat(internet));
        await Task.WhenAny(all, Task.Delay(Deadline, ct)).ConfigureAwait(false);
        _ = all.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        c.GatewayPing = Done(gateway);
        c.InternetPing = internet.Select(Done).Where(p => p is not null).OrderByDescending(p => p!.Received).ThenBy(p => p!.AvgMs).FirstOrDefault();
        c.InternetTcp = tcp.Status == TaskStatus.RanToCompletion ? tcp.Result : null;
        if (Done(dns) is { } d) (c.DnsOk, c.DnsMs, c.DnsError) = d;
        if (Done(web) is { } w) (c.WebOk, c.WebProblem) = (w.Ok, w.Problem);
        if (Done(clock) is { } offset) { c.ClockOffsetSeconds = offset; c.ClockSource = "time.windows.com (NTP)"; }
        else if (Done(web)?.Date is { } date) { c.ClockOffsetSeconds = Math.Round((DateTimeOffset.UtcNow - date).TotalSeconds, 1); c.ClockSource = "web server date"; }

        c.TimeSyncType = Reg.Str(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "Type");
        c.TimeServiceStart = (int?)Reg.Num(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time", "Start");
        return c;
    }

    private const int Deadline = 6500;

    private static T? Done<T>(Task<T> t) => t.Status == TaskStatus.RanToCompletion ? t.Result : default;

    private static Exception Innermost(Exception e) { while (e.InnerException is { } i) e = i; return e; }

    /// <summary>Looks the test name up through Windows' own resolver (so DoH and VPN DNS count) but skips the cache, which NCSI keeps warm.</summary>
    private static (bool? Ok, long? Ms, string? Error)? DnsTest()
    {
        var sw = Stopwatch.StartNew();
        var lookup = Task.Run(() => Native.DnsLookupUncached(ConnectivityInfo.TestHost));
        if (!lookup.Wait(3000)) return (false, null, "no answer within 3 s");
        var code = lookup.Result;
        return code == 0 ? (true, Timing.Ms(sw), null) : (false, null, new System.ComponentModel.Win32Exception(code).Message);
    }

    private sealed record WebResult(bool Ok, string? Problem, DateTimeOffset? Date);

    /// <summary>Fetches the page Windows uses for its "No internet" icon, redirects off, so a sign-in page shows up as a redirect.</summary>
    private static async Task<WebResult> WebTest(CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync(TestUrl, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is >= 300 and < 400)
                return new WebResult(false, $"The test page was redirected{(response.Headers.Location is { } to ? $" to {(to.IsAbsoluteUri ? to.Host : to.ToString())}" : "")}: a sign-in page (hotel, airport or cafe Wi-Fi) or a filter is in the way.", response.Headers.Date);
            var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
            var ok = code == 200 && body.StartsWith(TestText, StringComparison.Ordinal);
            return new WebResult(ok, ok ? null : code == 200 ? "The test page came back changed: a sign-in page, proxy or filter is rewriting web pages." : $"The test page returned error {code}.", response.Headers.Date);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WebException or IOException)
        {
            return new WebResult(false, e is TaskCanceledException ? "The test page didn't load within 4 s." : $"The test page didn't load: {Innermost(e).Message}", null);
        }
    }

    /// <summary>The IPv4 gateway of the first connected physical adapter (the router).</summary>
    private static string? DefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => new NetAdapter
                {
                    Name = n.Name, Description = n.Description, Status = "Up",
                    Type = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet",
                    Gateways = n.GetIPProperties().GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).Select(a => a.ToString()).ToList(),
                })
                .Where(a => a.Gateways.Count > 0)
                .OrderByDescending(a => a.IsPhysical)
                .Select(a => a.Gateways[0]).FirstOrDefault();
        }
        catch (NetworkInformationException) { return null; }
    }

    /// <summary>Sends <see cref="Pings"/> pings 50 ms apart without waiting for each reply.</summary>
    private static async Task<PingStats?> PingMany(string target, CancellationToken ct)
    {
        var sends = new List<Task<long?>>();
        for (var i = 0; i < Pings && !ct.IsCancellationRequested; i++)
        {
            sends.Add(PingOnce(target));
            await Task.Delay(PingSpacingMs, ct).ConfigureAwait(false);
        }
        var replies = (await Task.WhenAll(sends).ConfigureAwait(false)).Where(r => r is not null).Select(r => r!.Value).ToList();
        return new PingStats
        {
            Target = target, Sent = sends.Count, Received = replies.Count,
            AvgMs = replies.Count == 0 ? 0 : Math.Round(replies.Average(), 1), MaxMs = replies.Count == 0 ? 0 : replies.Max(),
        };
    }

    private static async Task<long?> PingOnce(string target)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(target, PingTimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception e) when (e is PingException or InvalidOperationException or SocketException) { return null; }
    }

    private static async Task<bool> TcpReachable(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(2500)).ConfigureAwait(false) != connect)
            {
                _ = connect.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }
            await connect.ConfigureAwait(false);
            return client.Connected;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>PC clock minus NTP time, seconds. Null if the server doesn't answer (UDP 123 blocked, no internet, DNS down).</summary>
    public static async Task<double?> Sntp(string server)
    {
        // Resolve with a time limit first: UdpClient.Connect(name) would block on a broken DNS for 10 s or more.
        IPAddress? ip;
        try
        {
            var lookup = Dns.GetHostAddressesAsync(server);
            if (await Task.WhenAny(lookup, Task.Delay(2000)).ConfigureAwait(false) != lookup)
            {
                _ = lookup.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }
            ip = (await lookup.ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException) { return null; }
        return ip is null ? null : await Task.Run(() => SntpQuery(ip)).ConfigureAwait(false);
    }

    private static double? SntpQuery(IPAddress server)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            udp.Connect(server, 123);
            var request = new byte[48];
            request[0] = 0x1B;  // version 3, client mode
            var t1 = DateTime.UtcNow;
            udp.Send(request, request.Length);
            IPEndPoint? from = null;
            var reply = udp.Receive(ref from);
            var t4 = DateTime.UtcNow;
            if (reply.Length < 48) return null;
            var t2 = NtpTime(reply, 32);
            var t3 = NtpTime(reply, 40);
            var serverMinusPc = ((t2 - t1) + (t3 - t4)).TotalSeconds / 2;
            return Math.Round(-serverMinusPc, 2);
        }
        catch (SocketException) { return null; }
    }

    private static DateTime NtpTime(byte[] b, int offset)
    {
        ulong seconds = (uint)(b[offset] << 24 | b[offset + 1] << 16 | b[offset + 2] << 8 | b[offset + 3]);
        ulong fraction = (uint)(b[offset + 4] << 24 | b[offset + 5] << 16 | b[offset + 6] << 8 | b[offset + 7]);
        var ms = seconds * 1000 + fraction * 1000 / 0x100000000UL;
        return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
    }
}

/// <summary>The user-started download test on the Network tab (NET-010). Not part of a scan: it moves real data.</summary>
public static class SpeedTest
{
    private const string Url = "https://speed.cloudflare.com/__down?bytes=50000000";
    private const int MaxSeconds = 10;
    private const int StallMs = 5000;

    /// <summary>Downloads for up to 10 s and returns megabits per second plus the megabytes received.</summary>
    public static async Task<(double Mbps, double MB)> RunAsync(CancellationToken ct)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(MaxSeconds + 10) };
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < MaxSeconds)
        {
            // HttpClient's timeout doesn't cover body reads, and .NET Framework ignores the token here; a stalled read ends
            // the test, and disposing the response aborts the connection.
            var read = stream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (await Task.WhenAny(read, Task.Delay(StallMs, ct)).ConfigureAwait(false) != read)
            {
                _ = read.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                if (total == 0) throw new TimeoutException("the download stalled");
                break;
            }
            var n = await read.ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        var seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        return (total * 8 / seconds / 1_000_000, total / 1_000_000.0);
    }
}
