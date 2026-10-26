using Troubleshootr.Core;

namespace Troubleshootr.Tests;

/// <summary>Builders for small hand-made snapshots. Start from Snap.Healthy() and change only what the test is about.</summary>
public static class Snap
{
    public static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    public static Snapshot Healthy() => new()
    {
        Meta = new SnapshotMeta { CollectedUtc = Now, ToolVersion = "test", IsAdmin = true },
        System = Section<SystemInfo>.Of(new SystemInfo
        {
            DeviceName = "TEST-PC", Manufacturer = "Contoso", Model = "Desk 1", PcSystemType = 1, HasBattery = false,
            WindowsProductName = "Windows 10 Pro", WindowsDisplayVersion = "25H2", EditionId = "Professional",
            MajorVersion = 10, Build = 26200, Ubr = 6000, Is64BitOs = true, UptimeSeconds = 3600,
        }),
        Memory = Section<MemoryInfo>.Of(new MemoryInfo
        {
            InstalledMB = 16384, UsableMB = 16200, AvailableMB = 9000, CommitUsedMB = 9000, CommitLimitMB = 20000, SlotCount = 2,
            Modules =
            [
                new() { CapacityMB = 8192, SpeedMts = 3200, ConfiguredSpeedMts = 3200, PartNumber = "F4-3200C16-8GVKB", Locator = "DIMM1", Type = "DDR4" },
                new() { CapacityMB = 8192, SpeedMts = 3200, ConfiguredSpeedMts = 3200, PartNumber = "F4-3200C16-8GVKB", Locator = "DIMM2", Type = "DDR4" },
            ],
        }),
        Board = Section<BoardInfo>.Of(new BoardInfo { BiosVendor = "AMI", BiosVersion = "1.0", BiosDate = Now.AddYears(-1) }),
        Volumes = Section<List<VolumeInfo>>.Of(
        [
            new() { Root = @"C:\", DriveType = "Fixed", TotalBytes = 500L * GB, FreeBytes = 200L * GB, IsSystem = true },
        ]),
        Cpu = Section<CpuInfo>.Of(new CpuInfo { Packages = [new() { Name = "Test CPU", Cores = 8, Threads = 16 }] }),
        Gpus = Section<List<GpuInfo>>.Of([new() { Name = "Test GPU", DriverDate = Now.AddMonths(-2) }]),
        Events = Section<EventsInfo>.Of(new EventsInfo()),
        Crashes = Section<CrashInfo>.Of(new CrashInfo { CrashDumpEnabled = 7, AutoReboot = true }),
        Health = Section<WindowsHealthInfo>.Of(new WindowsHealthInfo { StabilityIndex = 9.5, Cbs = new CbsSummary(), LicenseStatus = 1 }),
        Devices = Section<DevicesInfo>.Of(new DevicesInfo
        {
            TestSigning = false, IntegrityChecksOff = false,
            Devices = [new() { Name = "Test GPU", Class = "Display", InstanceId = @"PCI\VEN_10DE&DEV_2684\4&1", DriverProvider = "NVIDIA", DriverDate = Now.AddMonths(-2) }, new() { Name = "Trusted Platform Module 2.0", Class = "SecurityDevices" }],
        }),
        Displays = Section<DisplaysInfo>.Of(new DisplaysInfo { Displays = [new() { Adapter = "Test GPU", Monitor = "Monitor", Primary = true, Width = 2560, Height = 1440, RefreshHz = 144, MaxRefreshHz = 144 }] }),
        Startup = Section<StartupInfo>.Of(new StartupInfo()),
        Software = Section<SoftwareInfo>.Of(new SoftwareInfo()),
        Performance = Section<PerformanceInfo>.Of(new PerformanceInfo { SampleSeconds = 3, CpuPercent = 8, DiskActivePercent = 4, ProcessorPerformancePercent = 95 }),
        Tuning = Section<TuningInfo>.Of(new TuningInfo
        {
            PowerPlan = "Balanced", PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e", MaxCpuOnAc = 100, MaxCpuOnBattery = 100, OnAcPower = true,
            PageFileSettings = [@"?:\pagefile.sys"], PageFiles = [new() { Path = @"C:\pagefile.sys", AllocatedMB = 4096 }],
        }),
        Network = Section<NetworkInfo>.Of(new NetworkInfo
        {
            Adapters =
            [
                new() { Name = "Ethernet", Description = "Intel(R) Ethernet Controller I225-V", Type = "Ethernet", Status = "Up", SpeedMbps = 1000, Dhcp = true,
                        Ipv4 = ["192.168.1.20/24"], Gateways = ["192.168.1.1"], DnsServers = ["192.168.1.1"] },
            ],
            Winsock = [new() { Catalog = "Protocol", Path = @"%SystemRoot%\system32\mswsock.dll", Standard = true }],
        }),
        Connectivity = Section<ConnectivityInfo>.Of(new ConnectivityInfo
        {
            Gateway = "192.168.1.1", GatewayPing = new PingStats { Target = "192.168.1.1", Sent = 20, Received = 20, AvgMs = 2, MaxMs = 5 },
            InternetPing = new PingStats { Target = "1.1.1.1", Sent = 20, Received = 20, AvgMs = 18, MaxMs = 30 },
            InternetTcp = true, DnsOk = true, DnsMs = 12, WebOk = true, ClockOffsetSeconds = 0.4, ClockSource = "time.windows.com (NTP)", TimeSyncType = "NTP", TimeServiceStart = 3,
        }),
        Security = Section<SecurityInfo>.Of(new SecurityInfo
        {
            SecurityCenter = true,
            Antivirus = [new() { Name = "Windows Defender", Enabled = true, UpToDate = true, State = 397568 }],
            Defender = new DefenderStatus { RunningMode = "Normal", AntivirusEnabled = true, RealTimeProtection = true, TamperProtection = true, SignaturesUpdatedUtc = Now.AddHours(-3), SignatureAgeDays = 0 },
            FirewallDomain = true, FirewallPrivate = true, FirewallPublic = true, UacOn = true, UacAdminPrompt = 5,
            TpmVersion = "2.0", TpmPresent = true, BitLocker = [new() { Drive = "C:", Protection = 2 }],
            Accounts =
            [
                new() { Name = "Administrator", Rid = 500, Admin = true, Disabled = true },
                new() { Name = "Guest", Rid = 501, Disabled = true, PasswordNotRequired = true },
                new() { Name = "Owner", Rid = 1001, Admin = true },
            ],
            Smb1Client = false, Smb1Server = false, RdpEnabled = false, RdpNla = true, RdpPort = 3389, ListeningPorts = [445],
        }),
        Updates = Section<UpdatesInfo>.Of(new UpdatesInfo { History = [new() { DateUtc = Now.AddDays(-10), Title = "2026-10 Cumulative Update for Windows 11 Version 25H2 (KB5099999)", Result = 2 }] }),
    };

    public const long GB = 1073741824L;

    public static Snapshot With(this Snapshot s, Action<Snapshot> change) { change(s); return s; }
    public static SystemInfo Sys(this Snapshot s) => s.System!.Value!;
    public static MemoryInfo Mem(this Snapshot s) => s.Memory!.Value!;
    public static EventsInfo Evs(this Snapshot s) => s.Events!.Value!;

    /// <summary>Adds an event &lt;daysAgo&gt; before the snapshot time.</summary>
    public static Snapshot Event(this Snapshot s, string provider, int id, double daysAgo = 1, string? message = null, int level = 2,
        Dictionary<string, string>? data = null, string log = "System")
    {
        s.Evs().Events.Add(new LogEvent { Log = log, Provider = provider, Id = id, Level = level, TimeUtc = Now.AddDays(-daysAgo), Message = message, Data = data ?? new() });
        return s;
    }

    public static Snapshot Events(this Snapshot s, int count, string provider, int id, double everyDays = 0.1, string? message = null, int level = 2, Func<int, Dictionary<string, string>>? data = null)
    {
        for (var i = 0; i < count; i++) s.Event(provider, id, 0.5 + i * everyDays, message, level, data?.Invoke(i));
        return s;
    }
}
