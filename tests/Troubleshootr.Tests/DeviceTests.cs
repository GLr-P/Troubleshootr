using Troubleshootr.Checks;
using Troubleshootr.Checks.Drivers;
using Troubleshootr.Checks.Graphics;
using Troubleshootr.Checks.Updates;
using Troubleshootr.Checks.Windows;
using Troubleshootr.Collectors;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 5: devices, drivers, displays, Windows Update and Windows 11 readiness.</summary>
public class DeviceTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();
    private static Severity? Sev(ICheck check, Snapshot s) => Run(check, s).SingleOrDefault()?.Severity;
    private static DevicesInfo Dev(Snapshot s) => s.Devices!.Value!;

    private static Snapshot WithDevice(DeviceEntry d) => Snap.Healthy().With(s => Dev(s).Devices.Add(d));

    [Fact]
    public void Drv001SeverityByClass()
    {
        Assert.Equal(Severity.Critical, Sev(new Drv001ProblemDevices(), WithDevice(new() { Name = "Intel(R) Wi-Fi 6E AX211", Class = "Net", ProblemCode = 10 })));
        Assert.Equal(Severity.Warning, Sev(new Drv001ProblemDevices(), WithDevice(new() { Name = "WAN Miniport (IKEv2)", Class = "Net", ProblemCode = 31 })));
        var cam = Assert.Single(Run(new Drv001ProblemDevices(), WithDevice(new() { Name = "USB Camera", Class = "Camera", ProblemCode = 43 })));
        Assert.Equal(Severity.Warning, cam.Severity);
        Assert.Contains("stopped because it reported problems (code 43)", cam.Evidence);
        Assert.Equal(Severity.Info, Sev(new Drv001ProblemDevices(), WithDevice(new() { Name = "Webcam", Class = "Camera", ProblemCode = DeviceProblems.Disabled })));
        Assert.Empty(Run(new Drv001ProblemDevices(), WithDevice(new() { Name = "Unknown device", ProblemCode = DeviceProblems.NoDriver })));
    }

    [Fact]
    public void Drv002NamesTheVendor()
    {
        var f = Assert.Single(Run(new Drv002MissingDrivers(), WithDevice(new() { Name = "Unknown device", ProblemCode = 28, HardwareId = @"PCI\VEN_10EC&DEV_8168&SUBSYS_86771043" })));
        Assert.Contains("made by Realtek", f.Evidence);
        Assert.Contains(@"PCI\VEN_10EC&DEV_8168", f.Evidence);
    }

    [Fact]
    public void Drv004Drv006Drv010()
    {
        Assert.Empty(Run(new Drv004DriverSigning(), Snap.Healthy()));
        Assert.Equal(Severity.Warning, Sev(new Drv004DriverSigning(), Snap.Healthy().With(s => Dev(s).TestSigning = true)));

        var ring0 = Snap.Healthy().With(s => Dev(s).KernelDrivers.Add(new KernelDriver { Name = "WinRing0_1_2_0", File = "WinRing0x64.sys", Start = 3 }));
        Assert.Contains("WinRing0", Assert.Single(Run(new Drv006KnownBadDrivers(), ring0)).Evidence);
        var disabled = Snap.Healthy().With(s => Dev(s).KernelDrivers.Add(new KernelDriver { Name = "RTCore64", File = "RTCore64.sys", Start = 4 }));
        Assert.Empty(Run(new Drv006KnownBadDrivers(), disabled));

        var oldCard = Snap.Healthy().With(s => Dev(s).Hidden.Add(new DeviceEntry { Name = "NVIDIA GeForce GTX 1060", Class = "Display", InstanceId = @"PCI\VEN_10DE&DEV_1C03\4&2" }));
        Assert.Contains("GTX 1060", Assert.Single(Run(new Drv010GhostDevices(), oldCard)).Evidence);
        var usbDock = Snap.Healthy().With(s => Dev(s).Hidden.Add(new DeviceEntry { Name = "USB Display", Class = "Display", InstanceId = @"USB\VID_17E9&PID_4301\1" }));
        Assert.Empty(Run(new Drv010GhostDevices(), usbDock));
    }

    [Fact]
    public void Drv005OldVendorDriversOnly()
    {
        var old = new DeviceEntry { Name = "Realtek PCIe GbE", Class = "Net", DriverProvider = "Realtek", DriverDate = Snap.Now.AddYears(-7) };
        Assert.Equal(Severity.Info, Sev(new Drv005OldDrivers(), WithDevice(old)));
        Assert.Empty(Run(new Drv005OldDrivers(), WithDevice(new() { Name = "Generic", Class = "Net", DriverProvider = "Microsoft", DriverDate = new DateTime(2006, 6, 21) })));
        Assert.Empty(Run(new Drv005OldDrivers(), WithDevice(new() { Name = "Tailscale Tunnel", Class = "Net", DriverProvider = "WireGuard LLC", DriverDate = Snap.Now.AddYears(-6) })));
    }

    [Fact]
    public void Drv008IgnoresWudfBootNoise()
    {
        Dictionary<string, string> Wudf(int _) => new() { ["DriverName"] = @"HID\Vid_8087&Pid_0AC2\7&a3e83db&0&0000", ["FailureName"] = @"\Driver\WUDFRd" };
        Assert.Empty(Run(new Drv008DriverLoadFailures(), Snap.Healthy().Events(5, "Microsoft-Windows-Kernel-PnP", 219, data: Wudf)));
        var real = Snap.Healthy().Events(3, "Microsoft-Windows-Kernel-PnP", 411, data: _ => new() { ["DeviceInstanceId"] = @"PCI\VEN_8086&DEV_51F0\3&1" });
        Assert.Equal(Severity.Warning, Sev(new Drv008DriverLoadFailures(), real));
    }

    [Fact]
    public void GpuChecks()
    {
        var basic = Snap.Healthy().With(s => s.Gpus = Section<List<GpuInfo>>.Of([new() { Name = "Microsoft Basic Display Adapter" }]));
        Assert.Equal(Severity.Critical, Sev(new Gpu002BasicDisplay(), basic));

        var old = Snap.Healthy().With(s => s.Gpus!.Value![0].DriverDate = Snap.Now.AddMonths(-14));
        Assert.Equal(Severity.Info, Sev(new Gpu003OldDriver(), old));
        Assert.Equal(Severity.Warning, Sev(new Gpu003OldDriver(), Snap.Healthy().With(s => s.Gpus!.Value![0].DriverDate = Snap.Now.AddMonths(-30))));

        var onboard = Snap.Healthy().With(s =>
        {
            s.Gpus = Section<List<GpuInfo>>.Of([new() { Name = "NVIDIA GeForce RTX 4070" }, new() { Name = "Intel(R) UHD Graphics 770" }]);
            s.Displays!.Value!.Displays[0].Adapter = "Intel(R) UHD Graphics 770";
        });
        Assert.Equal(Severity.Warning, Sev(new Gpu006MonitorOnMotherboard(), onboard));
        Assert.Empty(Run(new Gpu006MonitorOnMotherboard(), onboard.With(s => s.Sys().HasBattery = true))); // laptop
        Assert.Empty(Run(new Gpu006MonitorOnMotherboard(), Snap.Healthy().With(s => s.Gpus = Section<List<GpuInfo>>.Of([new() { Name = "AMD Radeon(TM) Graphics" }]))));

        var slow = Snap.Healthy().With(s => s.Displays!.Value!.Displays[0].RefreshHz = 60);
        Assert.Contains("60 Hz, can do 144 Hz", Assert.Single(Run(new Gpu007RefreshRate(), slow)).Evidence);
        Assert.Empty(Run(new Gpu007RefreshRate(), Snap.Healthy().With(s => s.Displays!.Value!.Displays[0].MaxRefreshHz = 147)));

        var forced = Snap.Healthy().With(s =>
        {
            s.Gpus = Section<List<GpuInfo>>.Of([new() { Name = "NVIDIA GeForce RTX 4060 Laptop GPU" }]);
            s.Displays!.Value!.PowerSavingApps.Add(@"C:\Games\game.exe");
        });
        Assert.Contains("game.exe", Assert.Single(Run(new Gpu009PowerSavingApps(), forced)).Evidence);
    }

    private static UpdateEntry Upd(double daysAgo, string title, int result, int hresult = 0) =>
        new() { DateUtc = Snap.Now.AddDays(-daysAgo), Title = title, Result = result, HResult = hresult };

    [Fact]
    public void Upd001IgnoresDefenderAndStore()
    {
        var s = Snap.Healthy().With(x => x.Updates!.Value!.History =
        [
            Upd(1, "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.461.184.0)", 2),
            Upd(2, "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", 2),
            Upd(90, "2026-07 Cumulative Update for Windows 11 Version 25H2 (KB5090000)", 2),
        ]);
        Assert.Contains("90 days ago", Assert.Single(Run(new Upd001LastUpdate(), s)).Evidence);
        Assert.Empty(Run(new Upd001LastUpdate(), Snap.Healthy()));
    }

    [Fact]
    public void Upd002OnlyUnresolvedFailures()
    {
        const string cu = "2026-10 Cumulative Update for Windows 11 Version 25H2 (KB5099999)";
        var retried = Snap.Healthy().With(x => x.Updates!.Value!.History = [Upd(1, cu, 2), Upd(3, cu, 4, unchecked((int)0x80240016))]);
        Assert.Empty(Run(new Upd002FailedUpdates(), retried));
        var failing = Snap.Healthy().With(x => x.Updates!.Value!.History = [Upd(1, cu, 4, unchecked((int)0x800F081F)), Upd(5, cu, 4, unchecked((int)0x800F081F))]);
        var f = Assert.Single(Run(new Upd002FailedUpdates(), failing));
        Assert.Contains("0x800F081F (component store files missing", f.Evidence);
        Assert.Equal("An update keeps failing", f.Title);
        var defender = Snap.Healthy().With(x => x.Updates!.Value!.History.Add(Upd(1, "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602", 4, unchecked((int)0x80240016))));
        Assert.Empty(Run(new Upd002FailedUpdates(), defender));
    }

    [Fact]
    public void Upd003AndUpd005()
    {
        Assert.Empty(Run(new Upd003UpdatePolicy(), Snap.Healthy()));
        Assert.Equal(Severity.Warning, Sev(new Upd003UpdatePolicy(), Snap.Healthy().With(x => x.Updates!.Value!.Policy.PausedUntilUtc = Snap.Now.AddDays(20))));
        Assert.Empty(Run(new Upd003UpdatePolicy(), Snap.Healthy().With(x => x.Updates!.Value!.Policy.PausedUntilUtc = Snap.Now.AddDays(-20))));
        Assert.Equal(Severity.Info, Sev(new Upd003UpdatePolicy(), Snap.Healthy().With(x => x.Updates!.Value!.Policy.UsesWsus = true)));
        Assert.Equal(Severity.Warning, Sev(new Upd003UpdatePolicy(), Snap.Healthy().With(x => x.Updates!.Value!.Policy.AutoUpdateOff = true)));

        // The owner's laptop on 25H2: every target says None.
        var none = Snap.Healthy().With(x => x.Updates!.Value!.UpgradeBlocks = [new() { Target = "GE24H2", RedReason = "None" }, new() { Target = "GE26H2", RedReason = "None" }]);
        Assert.Empty(Run(new Upd005FeatureUpdateBlocked(), none));
        var held = Snap.Healthy().With(x => x.Updates!.Value!.UpgradeBlocks = [new() { Target = "GE26H2", RedReason = "None", GatedBlockId = "56318982" }, new() { Target = "GE24H2", RedReason = "SystemDriveSize" }]);
        var f = Assert.Single(Run(new Upd005FeatureUpdateBlocked(), held));
        Assert.Contains("26H2: safeguard hold 56318982", f.Evidence);
        Assert.DoesNotContain("24H2", f.Evidence); // older than the installed 25H2
    }

    [Fact]
    public void Win003Activation()
    {
        Assert.Empty(Run(new Win003Activation(), Snap.Healthy()));
        Assert.Contains("not activated", Assert.Single(Run(new Win003Activation(), Snap.Healthy().With(s => s.Health!.Value!.LicenseStatus = 0))).Evidence);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz", true)]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", false)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700H", false)]
    [InlineData("AMD Ryzen 7 1700X Eight-Core Processor", true)]
    [InlineData("AMD Ryzen 5 2400G with Radeon Vega Graphics", true)]
    [InlineData("AMD Ryzen 7 2700X Eight-Core Processor", false)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", false)]
    [InlineData("AMD FX(tm)-8350 Eight-Core Processor", true)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", null)]
    [InlineData("Intel(R) Pentium(R) Silver N5000", null)]
    public void Windows11CpuList(string name, bool? unsupported)
    {
        var s = Snap.Healthy().With(x => { x.Sys().Build = 19045; x.Sys().WindowsDisplayVersion = "22H2"; x.Cpu!.Value!.Packages[0].Name = name; });
        var f = Assert.Single(Run(new Win018Windows11Ready(), s));
        Assert.Equal(unsupported == true ? "This PC doesn't meet Windows 11's requirements" : "This PC can run Windows 11", f.Title);
    }

    [Fact]
    public void Win018AndCpu009()
    {
        var win10 = Snap.Healthy().With(x => { x.Sys().Build = 19045; x.Devices!.Value!.Devices.RemoveAll(d => d.Class == "SecurityDevices"); });
        Assert.Contains("no TPM found", Assert.Single(Run(new Win018Windows11Ready(), win10)).Evidence);
        Assert.Empty(Run(new Win018Windows11Ready(), Snap.Healthy())); // already on Windows 11 (build 26200)

        var old = Snap.Healthy().With(x => x.Cpu!.Value!.Packages[0].Name = "Intel(R) Core(TM) i7-6700 CPU @ 3.40GHz");
        Assert.Equal(Severity.Info, Sev(new Cpu009UnsupportedCpu(), old));
        Assert.Empty(Run(new Cpu009UnsupportedCpu(), old.With(x => x.Sys().Build = 19045)));
    }

    [Fact]
    public void AnonymizerHidesBluetoothNamesAndPrinterMacs()
    {
        var s = Snap.Healthy().With(x => Dev(x).Devices.AddRange(
        [
            new() { Name = "Sam's AirPods Pro", Class = "Bluetooth", InstanceId = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C\7&1&A4C1381234AB_C00000000" },
            new() { Name = "HP732C80 (HP OfficeJet 8020 series)", Class = "SoftwareDevice" },
            new() { Name = "HP OfficeJet 8020 series [732C80] (Fax)", Class = "PrintQueue" },
        ]));
        Anonymizer.Apply(s);
        var names = Dev(s).Devices.Select(d => d.Name).ToList();
        Assert.Contains("Bluetooth device", names);
        Assert.DoesNotContain(names, n => n.Contains("Sam") || n.Contains("732C80"));
        Assert.Contains("HPXXXXXX (HP OfficeJet 8020 series)", names);
        Assert.Contains("HP OfficeJet 8020 series [XXXXXX] (Fax)", names);
        Assert.Contains("Trusted Platform Module 2.0", names);
    }

    [Fact]
    public void DriverDateAndVendors()
    {
        Assert.Equal(new DateTime(2006, 6, 21), DevicesCollector.ParseDriverDate("6-21-2006"));
        Assert.Equal(new DateTime(2024, 2, 20), DevicesCollector.ParseDriverDate("2-20-2024"));
        Assert.Null(DevicesCollector.ParseDriverDate("junk"));
        Assert.Equal("Intel", Vendors.FromHardwareId(@"HID\Vid_8087&Pid_0AC2"));
        Assert.Null(Vendors.FromHardwareId(@"ACPI\PNP0C0A"));
        Assert.Equal("has no driver installed", DeviceProblems.Meaning(28));
        Assert.Equal("0x80070643 (the installer failed (often the WinRE partition is too small for KB5034441-type updates))", UpdateErrors.Describe(unchecked((int)0x80070643)));
    }
}
