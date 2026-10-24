using Troubleshootr.Checks;
using Troubleshootr.Checks.Memory;
using Troubleshootr.Checks.Storage;
using Troubleshootr.Checks.Windows;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

public class CheckTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();

    [Fact]
    public void HealthySnapshotHasNoFindings()
    {
        var report = ScanEngine.Evaluate(Snap.Healthy(), CheckRegistry.All);
        Assert.Empty(report.Findings);
    }

    [Theory]
    [InlineData(200, false, null)]
    [InlineData(80, false, null)]                // 16%, 80 GB
    [InlineData(60, false, Severity.Warning)]    // 12% of 500 GB
    [InlineData(4, false, Severity.Critical)]
    [InlineData(10, true, Severity.Info)]        // not the system drive: 2%
    [InlineData(50, true, null)]
    public void Sto008(long freeGb, bool otherDrive, Severity? expected)
    {
        var s = Snap.Healthy().With(x => { x.Volumes!.Value![0].FreeBytes = freeGb * Snap.GB; x.Volumes.Value[0].IsSystem = !otherDrive; });
        Assert.Equal(expected, Run(new Sto008FreeSpace(), s).SingleOrDefault()?.Severity);
    }

    [Fact]
    public void Sto008SmallDriveUsesAbsoluteLimit()
    {
        // 64 GB eMMC laptop with 15 GB free: 23% free, but under the 20 GB a feature update needs.
        var s = Snap.Healthy().With(x => { x.Volumes!.Value![0].TotalBytes = 64 * Snap.GB; x.Volumes.Value[0].FreeBytes = 15 * Snap.GB; });
        Assert.Equal(Severity.Warning, Assert.Single(Run(new Sto008FreeSpace(), s)).Severity);
    }

    [Fact]
    public void Sto008IgnoresRemovableDrives()
    {
        var s = Snap.Healthy().With(x => x.Volumes!.Value!.Add(new VolumeInfo { Root = @"E:\", DriveType = "Removable", TotalBytes = 16 * Snap.GB, FreeBytes = 0 }));
        Assert.Empty(Run(new Sto008FreeSpace(), s));
    }

    [Fact]
    public void Mem001ReservedRam()
    {
        Assert.Empty(Run(new Mem001UnusableRam(), Snap.Healthy()));
        var info = Run(new Mem001UnusableRam(), Snap.Healthy().With(x => x.Mem().UsableMB = 14000));
        Assert.Equal(Severity.Info, Assert.Single(info).Severity);
        var warn = Run(new Mem001UnusableRam(), Snap.Healthy().With(x => x.Mem().UsableMB = 8000));
        Assert.Equal(Severity.Warning, Assert.Single(warn).Severity);
        var x86 = Run(new Mem001UnusableRam(), Snap.Healthy().With(x => { x.Sys().Is64BitOs = false; x.Mem().UsableMB = 3300; }));
        Assert.Contains("32-bit", Assert.Single(x86).Title);
    }

    [Fact]
    public void Mem003XmpOff()
    {
        var s = Snap.Healthy().With(x => x.Mem().Modules.ForEach(m => m.ConfiguredSpeedMts = 2133));
        var f = Assert.Single(Run(new Mem003BelowRatedSpeed(), s));
        Assert.Contains("3200", f.Evidence);
        Assert.DoesNotContain("laptops", f.Advice);
    }

    [Theory]
    [InlineData("CMK32GX4M2D3600C18", 3600)]
    [InlineData("F5-6000J3038F16G", 6000)]
    [InlineData("M378A1K43CB2-CTD", 2666)]       // OEM part: falls back to the SMBIOS speed
    public void Mem003RatedSpeedFromPartNumber(string part, int rated) =>
        Assert.Equal(rated, Mem003BelowRatedSpeed.Rated(new MemoryModule { PartNumber = part, SpeedMts = 2666 }));

    [Fact]
    public void Mem004And005()
    {
        Assert.Empty(Run(new Mem004MismatchedModules(), Snap.Healthy()));
        var mixed = Snap.Healthy().With(x => x.Mem().Modules[1].CapacityMB = 4096);
        Assert.Contains("single channel", Assert.Single(Run(new Mem004MismatchedModules(), mixed)).Advice);

        var single = Snap.Healthy().With(x => x.Mem().Modules.RemoveAt(1));
        Assert.Single(Run(new Mem005SingleChannel(), single));
        var soldered = Snap.Healthy().With(x => { x.Mem().Modules.RemoveAt(1); x.Mem().SlotCount = 1; });
        Assert.Empty(Run(new Mem005SingleChannel(), soldered));
    }

    [Fact]
    public void Mem006Pressure()
    {
        Assert.Empty(Run(new Mem006MemoryPressure(), Snap.Healthy()));
        Assert.Single(Run(new Mem006MemoryPressure(), Snap.Healthy().With(x => x.Mem().CommitUsedMB = 19000)));
    }

    [Theory]
    [InlineData(19045, "Professional", "22H2", Severity.Warning)]  // Windows 10: ended 2025-10-14
    [InlineData(19044, "EnterpriseS", "21H2", null)]               // LTSC 2021: own lifecycle
    [InlineData(26100, "Professional", "24H2", Severity.Warning)]  // ended 2026-10-13
    [InlineData(26100, "Enterprise", "24H2", null)]                // until 2027-10-12
    [InlineData(26200, "Core", "25H2", null)]
    [InlineData(28000, "Core", "26H1", null)]                      // newer than the table
    public void Win002(int build, string edition, string display, Severity? expected)
    {
        var s = Snap.Healthy().With(x => { x.Sys().Build = build; x.Sys().EditionId = edition; x.Sys().WindowsDisplayVersion = display; });
        Assert.Equal(expected, Run(new Win002OutOfSupport(), s).SingleOrDefault()?.Severity);
    }

    [Fact]
    public void Win002Windows7()
    {
        var s = Snap.Healthy().With(x => { x.Sys().MajorVersion = 6; x.Sys().MinorVersion = 1; x.Sys().Build = 7601; x.Sys().WindowsProductName = "Windows 7 Professional"; });
        var f = Assert.Single(Run(new Win002OutOfSupport(), s));
        Assert.Contains("2020-01-14", f.Evidence);
    }

    [Fact]
    public void WindowsElevenNameFixed()
    {
        Assert.Equal("Windows 11 Pro 25H2 (build 26200.6000)", WindowsVersion.Describe(Snap.Healthy().Sys()));
    }

    [Fact]
    public void Win004And005()
    {
        Assert.Empty(Run(new Win004PendingReboot(), Snap.Healthy()));
        Assert.Single(Run(new Win004PendingReboot(), Snap.Healthy().With(x => x.Sys().PendingRebootReasons.Add("Windows Update"))));
        Assert.Equal(Severity.Info, Assert.Single(Run(new Win004PendingReboot(), Snap.Healthy().With(x => x.Sys().PendingRebootReasons.Add(SystemInfo.PendingFileRenames)))).Severity);
        var up = Run(new Win005LongUptime(), Snap.Healthy().With(x => { x.Sys().UptimeSeconds = 30 * 86400; x.Sys().FastStartupEnabled = true; }));
        Assert.Contains("Fast Startup", Assert.Single(up).Evidence);
    }

    [Fact]
    public void Win011SeesWmiFailures()
    {
        var s = Snap.Healthy().With(x => x.Gpus = Section<List<GpuInfo>>.Missing("WMI: 0x80041010 in root\\cimv2 (SELECT …)"));
        var report = ScanEngine.Evaluate(s, CheckRegistry.All);
        Assert.Contains(report.Findings, f => f.CheckId == "WIN-011");
        var timedOut = Snap.Healthy().With(x => x.Gpus = Section<List<GpuInfo>>.Missing("timed out after 5 s"));
        Assert.DoesNotContain(ScanEngine.Evaluate(timedOut, CheckRegistry.All).Findings, f => f.CheckId == "WIN-011");
    }

    [Fact]
    public void Per007OldBios()
    {
        Assert.Empty(Run(new Per007OldBios(), Snap.Healthy()));
        Assert.Single(Run(new Per007OldBios(), Snap.Healthy().With(x => x.Board!.Value!.BiosDate = Snap.Now.AddYears(-5))));
        Assert.Empty(Run(new Per007OldBios(), Snap.Healthy().With(x => x.Board!.Value!.BiosDate = new DateTime(1980, 1, 1))));
    }
}
