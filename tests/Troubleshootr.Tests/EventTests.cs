using System.Text;
using System.Xml.Linq;
using Troubleshootr.Checks;
using Troubleshootr.Checks.Crashes;
using Troubleshootr.Checks.Hardware;
using Troubleshootr.Checks.Windows;
using Troubleshootr.Collectors;
using Troubleshootr.Collectors.Crashes;
using Troubleshootr.Collectors.Events;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 4: event-log, crash-dump and Windows-health parsing and checks.</summary>
public class EventTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();
    private static Severity? Sev(ICheck check, Snapshot s) => Run(check, s).SingleOrDefault()?.Severity;

    // The owner's real blue screen (2026-09-23), as WER logged it.
    private const string RealParam1 = "0x00000119 (0x0000000000000001, 0x00000000005bfcb9, 0x00000000005bfd6d, 0xffff9e099bebe000)";

    private static Snapshot WithBsod(Snapshot s, uint code, double daysAgo, string? driver = null)
    {
        s.Crashes!.Value!.Dumps.Add(new CrashDump { File = $@"C:\Windows\Minidump\{daysAgo}.dmp", TimeUtc = Snap.Now.AddDays(-daysAgo), BugCheck = code, Parameters = [1, 2, 3, 4], Driver = driver });
        return s;
    }

    [Fact]
    public void ParsesWerBugcheckText()
    {
        var (code, ps) = BugChecks.ParseEventText(RealParam1)!.Value;
        Assert.Equal(0x119u, code);
        Assert.Equal([0x1UL, 0x5bfcb9UL, 0x5bfd6dUL, 0xffff9e099bebe000UL], ps);
        Assert.Null(BugChecks.ParseEventText("no code here"));
        Assert.Equal("VIDEO_SCHEDULER_INTERNAL_ERROR", BugChecks.Name(0x119));
        Assert.Equal("0x7E", BugChecks.Hex(0x1000007E));
    }

    [Fact]
    public void BlueScreenFromDumpEventAndKernelPowerIsListedOnce()
    {
        var crash = Snap.Now.AddDays(-2);
        var s = Snap.Healthy();
        s.Crashes!.Value!.Dumps.Add(new CrashDump { File = @"C:\Windows\Minidump\092326-15703-01.dmp", TimeUtc = crash, BugCheck = 0x119, Parameters = [1, 0x5bfcb9, 0x5bfd6d, 0xffff9e099bebe000] });
        s.Evs().Events.Add(new LogEvent { Provider = BlueScreens.WerProvider, Id = 1001, TimeUtc = crash.AddSeconds(2), Data = new() { ["param1"] = RealParam1, ["param2"] = @"C:\Windows\Minidump\092326-15703-01.dmp" } });
        s.Evs().Events.Add(new LogEvent { Provider = BlueScreens.KernelPower, Id = 41, TimeUtc = crash.AddSeconds(-7), Data = new() { ["BugcheckCode"] = "281" } });
        var b = Assert.Single(BlueScreens.From(s));
        Assert.Equal("dump file", b.Source);

        // Without admin there's no parsed dump: the event alone still counts, once.
        var noAdmin = Snap.Healthy();
        noAdmin.Evs().Events.AddRange(s.Evs().Events);
        Assert.Equal("event log", Assert.Single(BlueScreens.From(noAdmin)).Source);
    }

    [Fact]
    public void Cra001SeverityByCount()
    {
        Assert.Null(Sev(new Cra001BlueScreens(), Snap.Healthy()));
        Assert.Equal(Severity.Warning, Sev(new Cra001BlueScreens(), WithBsod(Snap.Healthy(), 0x119, 3)));
        Assert.Null(Sev(new Cra001BlueScreens(), WithBsod(Snap.Healthy(), 0x119, 40))); // older than 30 days
        var three = WithBsod(WithBsod(WithBsod(Snap.Healthy(), 0x0A, 1), 0x50, 5), 0x3B, 9);
        Assert.Equal(Severity.Critical, Sev(new Cra001BlueScreens(), three));
    }

    [Fact]
    public void Cra002AndMem009Patterns()
    {
        var mixed = WithBsod(WithBsod(WithBsod(Snap.Healthy(), 0x0A, 1), 0x50, 5), 0x3B, 9);
        Assert.Contains("different causes", Assert.Single(Run(new Cra002CrashPattern(), mixed)).Title);
        Assert.Equal(Severity.Warning, Sev(new Mem009MemoryCrashes(), mixed)); // one memory code plus a spread

        var same = WithBsod(WithBsod(Snap.Healthy(), 0x116, 1), 0x116, 4);
        Assert.Contains("same blue screen", Assert.Single(Run(new Cra002CrashPattern(), same)).Title);
        Assert.Null(Sev(new Mem009MemoryCrashes(), same));
        Assert.Empty(Run(new Cra002CrashPattern(), WithBsod(Snap.Healthy(), 0x116, 1)));

        var ram = WithBsod(WithBsod(Snap.Healthy(), 0x1A, 1), 0x1A, 2);
        Assert.Equal(Severity.Warning, Sev(new Mem009MemoryCrashes(), ram));
    }

    [Fact]
    public void Gpu005AndDrv007()
    {
        Assert.Equal(Severity.Warning, Sev(new Gpu005GraphicsCrashes(), WithBsod(Snap.Healthy(), 0x119, 2)));
        Assert.Equal(Severity.Warning, Sev(new Gpu005GraphicsCrashes(), WithBsod(Snap.Healthy(), 0x3B, 2, "nvlddmkm.sys")));
        Assert.Null(Sev(new Gpu005GraphicsCrashes(), WithBsod(Snap.Healthy(), 0x0A, 2)));

        Assert.Contains("rtwlane.sys", Assert.Single(Run(new Drv007CrashingDrivers(), WithBsod(Snap.Healthy(), 0xD1, 2, "rtwlane.sys"))).Title);
        Assert.Empty(Run(new Drv007CrashingDrivers(), WithBsod(Snap.Healthy(), 0x0A, 2, "ntoskrnl.exe")));
    }

    [Fact]
    public void Cra003GroupsRepeatedAppCrashesButNotTheShell()
    {
        var data = (string app) => (Func<int, Dictionary<string, string>>)(_ => new() { ["AppName"] = app, ["ModuleName"] = "ntdll.dll" });
        var s = Snap.Healthy().Events(2, "Application Error", 1000, data: data("Game.exe"));
        Assert.Empty(Run(new Cra003AppCrashes(), s));
        s.Events(1, "Application Error", 1000, data: data("game.exe"));
        var f = Assert.Single(Run(new Cra003AppCrashes(), s));
        Assert.Contains("3 crashes (in ntdll.dll)", f.Evidence);

        var shell = Snap.Healthy().Events(4, "Application Error", 1000, data: data("explorer.exe"));
        Assert.Empty(Run(new Cra003AppCrashes(), shell));
        Assert.Equal(Severity.Warning, Sev(new Win016ShellCrashes(), shell));

        var dotnet = Snap.Healthy().Events(3, ".NET Runtime", 1026, data: _ => new() { ["0"] = "Application: Tool.exe CoreCLR Version: 8.0" });
        Assert.Contains("Tool.exe", Assert.Single(Run(new Cra003AppCrashes(), dotnet)).Evidence);
    }

    [Fact]
    public void Sto007DiskErrors()
    {
        const string bad = @"The device, \Device\Harddisk0\DR0, has a bad block.";
        Assert.Equal(Severity.Critical, Sev(new Sto007DiskErrors(), Snap.Healthy().Event("disk", 7, 2, bad)));
        Assert.Equal(Severity.Warning, Sev(new Sto007DiskErrors(), Snap.Healthy().Event("disk", 7, 20, bad)));
        var usb = Snap.Healthy().With(x => x.Storage = Section<StorageInfo>.Of(new StorageInfo { Disks = [new DiskInfo { Number = 0, BusType = "USB" }] })).Event("disk", 7, 2, bad);
        Assert.Equal(Severity.Warning, Sev(new Sto007DiskErrors(), usb));
        Assert.Empty(Run(new Sto007DiskErrors(), Snap.Healthy().Event("stornvme", 129)));
        Assert.Equal(Severity.Warning, Sev(new Sto007DiskErrors(), Snap.Healthy().Events(3, "storahci", 129)));
        var ntfs = Snap.Healthy().Event("Microsoft-Windows-Ntfs", 98, 1, level: 3, data: new() { ["DriveName"] = "D:" });
        Assert.Contains("on D:", Assert.Single(Run(new Sto007DiskErrors(), ntfs)).Evidence);
    }

    [Fact]
    public void WheaSplitsMemoryAndProcessor()
    {
        const string memory = "A corrected hardware error has occurred. Component: Memory Error Source: Corrected Machine Check";
        const string cpu = "A corrected hardware error has occurred. Reported by component: Processor Core Error Source: Corrected Machine Check Error Type: Cache Hierarchy Error Processor APIC ID: 4";
        var s = Snap.Healthy().Event(Whea(), 19, message: memory, level: 3).Event(Whea(), 19, message: cpu, level: 3);
        Assert.Equal(Severity.Warning, Sev(new Mem007MemoryErrors(), s));
        var f = Assert.Single(Run(new Cpu006MachineChecks(), s));
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("Cache Hierarchy Error", f.Evidence);
        Assert.Equal(Severity.Critical, Sev(new Cpu006MachineChecks(), Snap.Healthy().Event(Whea(), 18, message: "A fatal hardware error has occurred.", level: 1)));
        Assert.Empty(Run(new Cpu006MachineChecks(), Snap.Healthy().Event(Whea(), 17, message: "A corrected hardware error has occurred. Component: PCI Express Root Port", level: 3)));
        static string Whea() => "Microsoft-Windows-WHEA-Logger";
    }

    [Fact]
    public void Mem008Cpu005Gpu004()
    {
        const string md = "Microsoft-Windows-MemoryDiagnostics-Results";
        Assert.Empty(Run(new Mem008MemoryDiagnostic(), Snap.Healthy().Event(md, 1201, level: 4)));
        Assert.Equal(Severity.Critical, Sev(new Mem008MemoryDiagnostic(), Snap.Healthy().Event(md, 1202, level: 2)));

        const string kpp = "Microsoft-Windows-Kernel-Processor-Power";
        Assert.Equal(Severity.Warning, Sev(new Cpu005Throttling(), Snap.Healthy().Events(5, kpp, 37, level: 3)));
        Assert.Equal(Severity.Info, Sev(new Cpu005Throttling(), Snap.Healthy().With(x => x.Sys().HasBattery = true).Events(40, kpp, 37, level: 3)));

        Assert.Equal(Severity.Info, Sev(new Gpu004DriverResets(), Snap.Healthy().Event("Display", 4101, level: 3)));
        Assert.Equal(Severity.Warning, Sev(new Gpu004DriverResets(), Snap.Healthy().Events(3, "Display", 4101, everyDays: 2, level: 3)));
        Assert.Equal(Severity.Critical, Sev(new Gpu004DriverResets(), Snap.Healthy().Events(5, "nvlddmkm", 153, everyDays: 0.01)));
    }

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", "Intel64 Family 6 Model 183 Stepping 1", "0x129", Severity.Critical)]
    [InlineData("14th Gen Intel(R) Core(TM) i7-14700KF", "Intel64 Family 6 Model 183 Stepping 1", "0x12F", null)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13980HX", "Intel64 Family 6 Model 183 Stepping 1", "0x104", null)] // laptop
    [InlineData("13th Gen Intel(R) Core(TM) i5-13400", "Intel64 Family 6 Model 191 Stepping 2", "0x34", null)]    // Alder Lake die
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "Intel64 Family 6 Model 170 Stepping 4", "0x1E", null)]
    public void Cpu007RaptorLakeMicrocode(string name, string id, string microcode, Severity? expected)
    {
        var s = Snap.Healthy().With(x => x.Cpu = Section<CpuInfo>.Of(new CpuInfo { Packages = [new() { Name = name, Identifier = id, Microcode = microcode }] }));
        Assert.Equal(expected, Sev(new Cpu007Microcode(), s));
    }

    [Fact]
    public void MicrocodeRegistryFormats()
    {
        Assert.Equal("0x1E", CpuCollector.Microcode(new byte[] { 0x1E, 0, 0, 0 }));                // Windows 11 25H2, Core Ultra
        Assert.Equal("0x12B", CpuCollector.Microcode(new byte[] { 0, 0, 0, 0, 0x2B, 0x01, 0, 0 })); // Intel, older Windows
        Assert.Equal("0xA201210", CpuCollector.Microcode(new byte[] { 0x10, 0x12, 0x20, 0x0A, 0, 0, 0, 0 })); // AMD
        Assert.Null(CpuCollector.Microcode(null));
    }

    [Fact]
    public void Win007UnexpectedShutdowns()
    {
        const string kp = BlueScreens.KernelPower;
        Assert.Equal(Severity.Info, Sev(new Win007UnexpectedShutdowns(), Snap.Healthy().Event(kp, 41, 2, level: 1, data: new() { ["BugcheckCode"] = "0" })));
        // A blue screen's Kernel-Power 41 (with its code) is CRA-001's, and 6008 at the same boot isn't a second shutdown.
        var bsod = Snap.Healthy().Event(kp, 41, 2, level: 1, data: new() { ["BugcheckCode"] = "281" }).Event("EventLog", 6008, 2.001);
        Assert.Empty(Run(new Win007UnexpectedShutdowns(), bsod));
        var many = Snap.Healthy().Events(6, kp, 41, everyDays: 1, level: 1, data: _ => new() { ["BugcheckCode"] = "0" });
        Assert.Equal(Severity.Critical, Sev(new Win007UnexpectedShutdowns(), many));
    }

    [Fact]
    public void Win008SlowBootNamesCulprits()
    {
        const string dp = "Microsoft-Windows-Diagnostics-Performance";
        var s = Snap.Healthy().Event(dp, 100, data: new() { ["BootTime"] = "114574" })
            .Event(dp, 101, data: new() { ["Name"] = "MsMpEng.exe", ["FriendlyName"] = "Microsoft Defender Antimalware Service", ["DegradationTime"] = "151285" })
            .Event(dp, 101, data: new() { ["Name"] = "Discord.exe", ["FriendlyName"] = "", ["DegradationTime"] = "9249" });
        var f = Assert.Single(Run(new Win008SlowBoot(), s));
        Assert.Contains("115 s", f.Evidence);
        Assert.Contains("Discord.exe", f.Evidence);
        Assert.Empty(Run(new Win008SlowBoot(), Snap.Healthy().Event(dp, 100, data: new() { ["BootTime"] = "30000" })));
    }

    [Fact]
    public void Win009UsesTheServiceNameFor7009()
    {
        var s = Snap.Healthy()
            .Event("Service Control Manager", 7000, data: new() { ["param1"] = "MBAMService" })
            .Event("Service Control Manager", 7000, data: new() { ["param1"] = "MBAMService" })
            .Event("Service Control Manager", 7009, data: new() { ["param1"] = "30000", ["param2"] = "MBAMService" });
        Assert.Contains("MBAMService 3 times", Assert.Single(Run(new Win009ServiceFailures(), s)).Evidence);
    }

    [Fact]
    public void Win010Services()
    {
        Snapshot With(params ServiceState[] x) => Snap.Healthy().With(s => s.Health!.Value!.Services = x.ToList());
        Assert.Empty(Run(new Win010ImportantServices(), With(new ServiceState { Name = "wuauserv", Status = "Stopped", StartType = "Manual" })));
        Assert.Equal(Severity.Warning, Sev(new Win010ImportantServices(), With(new ServiceState { Name = "wuauserv", Status = "Stopped", StartType = "Disabled" })));
        Assert.Equal(Severity.Warning, Sev(new Win010ImportantServices(), With(new ServiceState { Name = "Audiosrv", Status = "Stopped", StartType = "Automatic" })));
        Assert.Equal(Severity.Info, Sev(new Win010ImportantServices(), With(new ServiceState { Name = "Spooler", Status = "Stopped", StartType = "Disabled" })));
        Assert.Empty(Run(new Win010ImportantServices(), With(new ServiceState { Name = "Dhcp" }))); // not installed
    }

    [Fact]
    public void HealthChecks()
    {
        Assert.Equal(Severity.Critical, Sev(new Win013UserProfile(), Snap.Healthy().With(s => s.Health!.Value!.TempProfile = true)));
        Assert.Equal(Severity.Warning, Sev(new Win013UserProfile(), Snap.Healthy().Event("Microsoft-Windows-User Profiles Service", 1511, log: "Application")));
        Assert.Equal(Severity.Warning, Sev(new Win014LowVirtualMemory(), Snap.Healthy().Event("Microsoft-Windows-Resource-Exhaustion-Detector", 2004, message: "Windows successfully diagnosed a low virtual memory condition. The following programs consumed the most virtual memory: chrome.exe (1234) consumed 9000000000 bytes.")));
        Assert.Equal(Severity.Info, Sev(new Win015GroupPolicy(), Snap.Healthy().Event("Microsoft-Windows-GroupPolicy", 1129)));
        Assert.Equal(Severity.Warning, Sev(new Win017Stability(), Snap.Healthy().With(s => s.Health!.Value!.StabilityIndex = 2.3)));
        Assert.Equal(Severity.Info, Sev(new Win017Stability(), Snap.Healthy().With(s => s.Health!.Value!.StabilityIndex = 4.1)));
        Assert.Equal(Severity.Warning, Sev(new Win006SystemFiles(), Snap.Healthy().With(s => s.Health!.Value!.Cbs = new CbsSummary { CannotRepair = 2 })));
        Assert.Equal(Severity.Info, Sev(new Win006SystemFiles(), Snap.Healthy().With(s => s.Health!.Value!.Cbs = new CbsSummary { Repaired = 2 })));
        Assert.Empty(Run(new Win006SystemFiles(), Snap.Healthy().With(s => s.Health!.Value!.Cbs = new CbsSummary { CannotRepair = 2, Unavailable = "needs admin" })));
        Assert.Equal(Severity.Info, Sev(new Cra006DumpsDisabled(), Snap.Healthy().With(s => s.Crashes!.Value!.CrashDumpEnabled = 0)));
        var reports = Snap.Healthy().With(s => s.Crashes!.Value!.Reports = [new WerReport { Kind = "LiveKernelEvent", Subject = "141", TimeUtc = Snap.Now.AddDays(-3) }]);
        Assert.Contains("0x141 VIDEO_ENGINE_TIMEOUT_DETECTED", Assert.Single(Run(new Cra004KernelReports(), reports)).Evidence);
    }

    [Fact]
    public void CbsLogCounting()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var log = string.Join("\n",
            "2026-10-20 11:22:33, Info                  CSI    00000123 [SR] Cannot repair member file [l:12]'notepad.exe' of Microsoft-Windows-Notepad",
            "2026-10-20 11:22:34, Info                  CSI    00000124 [SR] Repairing corrupted file \\??\\C:\\Windows\\x.dll from store",
            "2026-10-20 11:22:35, Info                  CBS    (p)	CSI Payload Corrupt			amd64_microsoft-windows-x",
            "2025-01-01 10:00:00, Info                  CSI    [SR] Cannot repair member file old",
            "partial line without a date");
        var c = new CbsSummary();
        HealthCollector.Count(new StringReader(log), now.AddDays(-90), c);
        Assert.Equal((1, 1, 1), (c.CannotRepair, c.Repaired, c.StoreCorrupt));
    }

    [Fact]
    public void WerReportNames()
    {
        var r = CrashesCollector.ParseReportName("AppCrash_KillerProviderDa_18eea7d04b815e40589a82fd9d2d99463ba2e9bc_30494f71_3a099ac9")!;
        Assert.Equal(("AppCrash", "KillerProviderDa"), (r.Kind, r.Subject));
        Assert.Equal("119", CrashesCollector.ParseReportName("Kernel_119_24e9badaa85555b9a59eff209c4220ac8d2ec3_00000000_9f4fb07e")!.Subject);
        Assert.Null(CrashesCollector.ParseReportName("junk"));
    }

    [Fact]
    public void EventXmlDataAndXPath()
    {
        var xml = XElement.Parse("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System/><EventData>" +
                                 "<Data Name='BugcheckCode'>281</Data><Data Name='RawData'>00FF</Data><Data>unnamed</Data></EventData></Event>");
        var data = new Dictionary<string, string>();
        EventsCollector.Fill(xml, data);
        Assert.Equal("281", data["BugcheckCode"]);
        Assert.Equal("unnamed", data["2"]);
        Assert.False(data.ContainsKey("RawData"));

        var x = EventsCollector.XPath(new EventsCollector.Rule("System", "Ntfs", [55, 98], WarningsOnly: true), 30);
        Assert.Equal("*[System[Provider[@Name='Ntfs'] and (EventID=55 or EventID=98) and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", x);
    }

    [Fact]
    public void AnonymizerScrubsNamesFromEventText()
    {
        var s = Snap.Healthy().With(x => x.Sys().UserName = "jsmith").Event("Microsoft-Windows-Diagnostics-Performance", 101,
            message: "Discord.exe on TEST-PC was slow", data: new() { ["Path"] = @"C:\Users\jsmith\AppData\Local\Discord\Discord.exe" });
        s.Crashes!.Value!.Dumps.Add(new CrashDump { File = @"D:\Users\jsmith\x.dmp" });
        Anonymizer.Apply(s);
        var e = s.Evs().Events[0];
        Assert.Equal(@"C:\Users\[user]\AppData\Local\Discord\Discord.exe", e.Data["Path"]);
        Assert.Equal("Discord.exe on [redacted] was slow", e.Message);
        Assert.DoesNotContain("jsmith", s.Crashes.Value.Dumps[0].File);
    }

    // ---- crash dump parser, against bytes laid out like the owner's real 0x119 dump (64-bit small dump, driver stride 0x90) ----

    private static byte[] Dump64(uint code, ulong[] ps, (string Name, ulong Base, uint Size)[] drivers)
    {
        var b = new byte[0x40000];
        Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(b, 0);
        BitConverter.GetBytes(code).CopyTo(b, 0x38);
        for (var i = 0; i < 4; i++) BitConverter.GetBytes(ps[i]).CopyTo(b, 0x40 + i * 8);
        const int list = 0x12CE0, stride = 0x90;
        var pool = list + stride * drivers.Length;
        BitConverter.GetBytes((uint)list).CopyTo(b, 0x2000 + 48);
        BitConverter.GetBytes((uint)drivers.Length).CopyTo(b, 0x2000 + 52);
        BitConverter.GetBytes((uint)pool).CopyTo(b, 0x2000 + 56);
        var at = pool;
        for (var i = 0; i < drivers.Length; i++)
        {
            var e = list + i * stride;
            BitConverter.GetBytes((uint)at).CopyTo(b, e);
            BitConverter.GetBytes(drivers[i].Base).CopyTo(b, e + 8 + 0x30);
            BitConverter.GetBytes(drivers[i].Size).CopyTo(b, e + 8 + 0x40);
            BitConverter.GetBytes((uint)drivers[i].Name.Length).CopyTo(b, at);
            Encoding.Unicode.GetBytes(drivers[i].Name).CopyTo(b, at + 4);
            at += 4 + drivers[i].Name.Length * 2 + 4;
        }
        return b;
    }

    private static readonly (string, ulong, uint)[] RealDrivers =
        [("ntoskrnl.exe", 0xFFFFF803BA200000, 0x1450000), ("hal.dll", 0xFFFFF803BBA00000, 0x6000), ("rtwlane.sys", 0xFFFFF8034C000000, 0x200000)];

    private static CrashDump Parse(byte[] b) =>
        DumpParser.Parse((o, c) => o >= b.Length ? [] : b.Skip((int)o).Take(c).ToArray(), b.Length, new CrashDump());

    [Fact]
    public void DumpHeaderAndDriverAtFault()
    {
        var real = Parse(Dump64(0x119, [1, 0x5bfcb9, 0x5bfd6d, 0xffff9e099bebe000], RealDrivers));
        Assert.Equal(0x119u, real.BugCheck);
        Assert.Equal(0xffff9e099bebe000UL, real.Parameters[3]);
        Assert.Null(real.Driver); // 0x119 has no crash-address parameter

        var d1 = Parse(Dump64(0xD1, [0, 2, 0, 0xFFFFF8034C012345], RealDrivers));
        Assert.Equal("rtwlane.sys", d1.Driver);

        var b = Dump64(0xD1, [0, 2, 0, 0xFFFFF8034C012345], RealDrivers);
        var drivers = DumpParser.Drivers((o, c) => b.Skip((int)o).Take(c).ToArray(), b.Length)!;
        Assert.Equal(["ntoskrnl.exe", "hal.dll", "rtwlane.sys"], drivers.Select(x => x.Name));
    }

    [Fact]
    public void DumpParserRejectsJunk()
    {
        Assert.Equal("not a kernel dump file", Parse(new byte[0x100]).ParseError);
        var b32 = new byte[0x1000];
        Encoding.ASCII.GetBytes("PAGEDUMP").CopyTo(b32, 0);
        BitConverter.GetBytes(0x7Eu).CopyTo(b32, 0x20);
        Assert.Equal(0x7Eu, Parse(b32).BugCheck);
        // A corrupted driver list (name offset outside the pool) is ignored rather than trusted.
        var bad = Dump64(0xD1, [0, 2, 0, 0xFFFFF8034C012345], RealDrivers);
        BitConverter.GetBytes(5u).CopyTo(bad, 0x12CE0 + 0x90 * 2);
        Assert.Null(Parse(bad).Driver);
    }
}
