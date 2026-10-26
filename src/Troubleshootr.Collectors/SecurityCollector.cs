using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>
/// Security state without admin: Security Center products, Defender status and detections, firewall profiles, UAC,
/// TPM and BitLocker (Shell property), local accounts, SMB1/RDP, listening ports, browser policies, remote-access
/// tools running, and scheduled tasks that look like script persistence.
/// </summary>
public sealed class SecurityCollector : Collector<SecurityInfo>
{
    private const string SecurityCenter = @"root\SecurityCenter2";
    private const string Defender = @"root\Microsoft\Windows\Defender";
    private const int ThreatDays = 30;

    public override string Section => nameof(Snapshot.Security);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(8);

    protected override SecurityInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var s = new SecurityInfo();
        // The WMI parts are the slow ones (Security Center ~0.3 s, Defender ~0.5 s); run them beside the rest. Each fills
        // its own object, copied in only if it finished in time, so a hung WMI provider costs its part, not the section.
        var wmi = Task.Run(() => { var w = new SecurityInfo(); Products(w); DefenderStatus(w); return w; });
        var tasks = Task.Run(() => ScanTasks(ct));
        Firewall(s);
        Uac(s);
        s.TpmVersion = Native.TpmVersion();
        s.TpmPresent = !string.IsNullOrEmpty(s.TpmVersion);
        s.BitLocker = BitLocker();
        s.Accounts = Accounts();
        SharingAndRdp(s);
        s.BrowserPolicies = BrowserPolicies();
        s.RemoteToolsRunning = RunningRemoteTools();
        Task.WaitAll([wmi, tasks], PartsBudgetMs, ct);
        if (wmi.Status == TaskStatus.RanToCompletion)
        {
            var w = wmi.Result;
            (s.SecurityCenter, s.Antivirus, s.Firewalls, s.Defender, s.Threats) = (w.SecurityCenter, w.Antivirus, w.Firewalls, w.Defender, w.Threats);
        }
        if (tasks.Status == TaskStatus.RanToCompletion) s.SuspiciousTasks = tasks.Result;
        return s;
    }

    private const int PartsBudgetMs = 6000;

    private static void Products(SecurityInfo s)
    {
        try
        {
            var av = Wmi.Query("SELECT displayName, productState FROM AntiVirusProduct", SecurityCenter);
            s.SecurityCenter = true;
            foreach (var r in av)
            {
                var state = r.Int("productState") ?? 0;
                var (on, current) = AvProduct.Decode(state);
                s.Antivirus.Add(new AvProduct { Name = r.Str("displayName") ?? "Unknown", Enabled = on, UpToDate = current, State = state });
            }
        }
        catch (CollectorException) { s.SecurityCenter = false; return; }  // Windows Server: no Security Center
        try
        {
            foreach (var r in Wmi.QueryOptional("SELECT displayName, productState FROM FirewallProduct", SecurityCenter))
                if (AvProduct.Decode(r.Int("productState") ?? 0).Enabled && r.Str("displayName") is { } name) s.Firewalls.Add(name);
        }
        catch (CollectorException) { }
    }

    private static void DefenderStatus(SecurityInfo s)
    {
        try
        {
            var r = Wmi.QueryOptional("SELECT AMRunningMode, AntivirusEnabled, RealTimeProtectionEnabled, IsTamperProtected, AntivirusSignatureLastUpdated, AntivirusSignatureAge FROM MSFT_MpComputerStatus", Defender).FirstOrDefault();
            if (r is null) return;
            s.Defender = new DefenderStatus
            {
                RunningMode = r.Str("AMRunningMode"),
                AntivirusEnabled = r.Bool("AntivirusEnabled") == true,
                RealTimeProtection = r.Bool("RealTimeProtectionEnabled") == true,
                TamperProtection = r.Bool("IsTamperProtected"),
                SignaturesUpdatedUtc = Wmi.ParseCimDate(r.Str("AntivirusSignatureLastUpdated"))?.ToUniversalTime(),
                SignatureAgeDays = r.Int("AntivirusSignatureAge") is { } age and < 60000 ? age : null,
            };

            var names = Wmi.QueryOptional("SELECT ThreatID, ThreatName, SeverityID, IsActive FROM MSFT_MpThreat", Defender)
                .GroupBy(t => t.Long("ThreatID")).ToDictionary(g => g.Key ?? 0, g => g.First());
            var since = DateTime.UtcNow.AddDays(-ThreatDays);
            foreach (var d in Wmi.QueryOptional("SELECT ThreatID, InitialDetectionTime, ActionSuccess, Resources FROM MSFT_MpThreatDetection", Defender))
            {
                var when = Wmi.ParseCimDate(d.Str("InitialDetectionTime"))?.ToUniversalTime();
                if (when < since) continue;
                names.TryGetValue(d.Long("ThreatID") ?? 0, out var t);
                s.Threats.Add(new ThreatDetection
                {
                    Name = t?.Str("ThreatName") ?? $"Threat {d.Long("ThreatID")}",
                    Severity = t?.Int("SeverityID") ?? 0,
                    Active = t?.Bool("IsActive") == true,
                    DetectedUtc = when,
                    Removed = d.Bool("ActionSuccess"),
                    Where = (d["Resources"] as string[])?.FirstOrDefault(),
                });
            }
            s.Threats = s.Threats.OrderByDescending(t => t.DetectedUtc).Take(20).ToList();
        }
        catch (CollectorException) { }  // Defender removed or its WMI provider broken; Security Center still reports
    }

    private static void Firewall(SecurityInfo s)
    {
        bool? Profile(string name)
        {
            var policy = Reg.Num(RegistryHive.LocalMachine, $@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{name}", "EnableFirewall");
            var local = Reg.Num(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{name}", "EnableFirewall");
            return (policy ?? local) is { } v ? v != 0 : null;
        }
        s.FirewallDomain = Profile("DomainProfile");
        s.FirewallPrivate = Profile("StandardProfile");
        s.FirewallPublic = Profile("PublicProfile");
    }

    private static void Uac(SecurityInfo s)
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        s.UacOn = Reg.Num(RegistryHive.LocalMachine, key, "EnableLUA") is { } lua ? lua != 0 : true;  // absent = on
        s.UacAdminPrompt = (int?)Reg.Num(RegistryHive.LocalMachine, key, "ConsentPromptBehaviorAdmin");
    }

    /// <summary>The Shell's BitLocker property works without admin (Win32_EncryptableVolume needs it).</summary>
    private static List<BitLockerVolume> BitLocker()
    {
        var list = new List<BitLockerVolume>();
        try
        {
            var shell = Com.Create("Shell.Application");
            if (shell is null) return list;
            try
            {
                var computer = Com.Call(shell, "NameSpace", 17);  // ssfDRIVES
                if (computer is null) return list;
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
                {
                    var letter = drive.Name.TrimEnd('\\');
                    var item = Com.Call(computer, "ParseName", letter);
                    if (item is null) continue;
                    var value = Com.Call(item, "ExtendedProperty", "System.Volume.BitLockerProtection");
                    if (value is not null) list.Add(new BitLockerVolume { Drive = letter, Protection = Com.Int(value) });
                    Marshal.ReleaseComObject(item);
                }
                Marshal.ReleaseComObject(computer);
            }
            finally { Marshal.ReleaseComObject(shell); }
        }
        catch (Exception e) when (e is COMException or TargetInvocationException or InvalidCastException or FormatException) { }
        return list;
    }

    private static List<LocalAccount> Accounts()
    {
        const uint disabled = 0x2, noPasswordNeeded = 0x20;
        try
        {
            return (Native.LocalUsers() ?? []).Select(u => new LocalAccount
            {
                Name = u.name,
                Rid = (int)u.user_id,
                Admin = u.priv == 2,
                Disabled = (u.flags & disabled) != 0,
                PasswordNotRequired = (u.flags & noPasswordNeeded) != 0,
                LastLogonUtc = u.last_logon == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(u.last_logon).UtcDateTime,
            }).ToList();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return []; }
    }

    private static void SharingAndRdp(SecurityInfo s)
    {
        const string services = @"SYSTEM\CurrentControlSet\Services\";
        // SMB1 client: mrxsmb10 is only present when the SMB 1.0 feature is installed.
        s.Smb1Client = Reg.KeyExists(RegistryHive.LocalMachine, services + "mrxsmb10") ? Reg.Num(RegistryHive.LocalMachine, services + "mrxsmb10", "Start") != 4 : false;
        var serverValue = Reg.Num(RegistryHive.LocalMachine, services + @"LanmanServer\Parameters", "SMB1");
        var srvDriver = Reg.KeyExists(RegistryHive.LocalMachine, services + "srv") && Reg.Num(RegistryHive.LocalMachine, services + "srv", "Start") != 4;
        s.Smb1Server = serverValue is { } v ? v != 0 && srvDriver : srvDriver;

        const string ts = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
        const string policy = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";  // Group Policy wins over the local settings
        s.RdpEnabled = (Reg.Num(RegistryHive.LocalMachine, policy, "fDenyTSConnections") ?? Reg.Num(RegistryHive.LocalMachine, ts, "fDenyTSConnections")) is { } deny ? deny == 0 : null;
        s.RdpNla = (Reg.Num(RegistryHive.LocalMachine, policy, "UserAuthentication") ?? Reg.Num(RegistryHive.LocalMachine, ts + @"\WinStations\RDP-Tcp", "UserAuthentication")) is { } nla ? nla != 0 : null;
        s.RdpPort = (int?)Reg.Num(RegistryHive.LocalMachine, ts + @"\WinStations\RDP-Tcp", "PortNumber");
        try
        {
            s.ListeningPorts = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(e => (e.Address.Equals(System.Net.IPAddress.Any) || e.Address.Equals(System.Net.IPAddress.IPv6Any)) && SecurityInfo.WatchedPorts.Contains(e.Port))
                .Select(e => e.Port).Distinct().OrderBy(p => p).ToList();
        }
        catch (NetworkInformationException) { }
    }

    /// <summary>The policies adware sets to lock a browser to its home page, search engine or extension.</summary>
    private static List<BrowserPolicy> BrowserPolicies()
    {
        var list = new List<BrowserPolicy>();
        var browsers = new[] { ("Chrome", @"SOFTWARE\Policies\Google\Chrome"), ("Edge", @"SOFTWARE\Policies\Microsoft\Edge"), ("Brave", @"SOFTWARE\Policies\BraveSoftware\Brave") };
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var (browser, key) in browsers)
            {
                if (Reg.Str(hive, key, "HomepageLocation") is { } home) list.Add(new BrowserPolicy { Browser = browser, Kind = "Home page", Value = home });
                if (Reg.Str(hive, key, "DefaultSearchProviderSearchURL") is { } search) list.Add(new BrowserPolicy { Browser = browser, Kind = "Search engine", Value = search });
                foreach (var v in Values(hive, key + @"\RestoreOnStartupURLs")) list.Add(new BrowserPolicy { Browser = browser, Kind = "Startup pages", Value = v });
                foreach (var v in Values(hive, key + @"\ExtensionInstallForcelist")) list.Add(new BrowserPolicy { Browser = browser, Kind = "Forced extensions", Value = v });
            }
            const string firefox = @"SOFTWARE\Policies\Mozilla\Firefox";
            if (Reg.Str(hive, firefox + @"\Homepage", "URL") is { } ffHome) list.Add(new BrowserPolicy { Browser = "Firefox", Kind = "Home page", Value = ffHome });
            foreach (var v in Values(hive, firefox + @"\Extensions\Install")) list.Add(new BrowserPolicy { Browser = "Firefox", Kind = "Forced extensions", Value = v });
        }
        return list;
    }

    private static IEnumerable<string> Values(RegistryHive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? Reg.NativeView : RegistryView.Default);
            using var key = root.OpenSubKey(path);
            return key?.GetValueNames().Select(n => key.GetValue(n) as string).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList() ?? [];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    private static List<string> RunningRemoteTools()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var name = p.ProcessName;
                foreach (var tool in RemoteTools.Known)
                    if (tool.Processes.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase))) running.Add(tool.Name);
            }
        }
        return running.OrderBy(n => n).ToList();
    }

    /// <summary>Every task outside \Microsoft whose action looks like script-based persistence (SEC-010).</summary>
    private static List<SuspiciousTask> ScanTasks(CancellationToken ct)
    {
        var found = new List<SuspiciousTask>();
        TaskWalk.Each(task =>
        {
            foreach (var command in TaskWalk.Commands(Com.Get(task, "Definition")!))
                if (SuspiciousCommand.Why(command) is { } why)
                    found.Add(new SuspiciousTask { Name = Com.Get(task, "Path") as string ?? "", Command = command, Reason = why });
        }, ct);
        return found;
    }
}
