using Troubleshootr.Checks;
using Troubleshootr.Checks.Storage;
using Troubleshootr.Collectors.Storage;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

public class StorageTests
{
    /// <summary>Builds an ATA SMART data page: (id, current, worst, raw) entries, plus a thresholds page.</summary>
    internal static (byte[] Data, byte[] Thresholds) AtaPage(params (int Id, int Current, int Threshold, long Raw)[] attrs)
    {
        var data = new byte[512];
        var thr = new byte[512];
        for (var i = 0; i < attrs.Length; i++)
        {
            var o = 2 + i * 12;
            data[o] = thr[o] = (byte)attrs[i].Id;
            data[o + 3] = (byte)attrs[i].Current;
            data[o + 4] = (byte)attrs[i].Current;
            BitConverter.GetBytes(attrs[i].Raw).Take(6).ToArray().CopyTo(data, o + 5);
            thr[o + 1] = (byte)attrs[i].Threshold;
        }
        return (data, thr);
    }

    internal static byte[] NvmeLog(int criticalWarning = 0, int tempC = 35, int spare = 100, int spareThreshold = 10, int used = 3,
        long written = 2_000_000, long hours = 1234, long mediaErrors = 0)
    {
        var log = new byte[512];
        log[0] = (byte)criticalWarning;
        BitConverter.GetBytes((ushort)(tempC + 273)).CopyTo(log, 1);
        log[3] = (byte)spare;
        log[4] = (byte)spareThreshold;
        log[5] = (byte)used;
        BitConverter.GetBytes(written).CopyTo(log, 48);
        BitConverter.GetBytes(hours).CopyTo(log, 128);
        BitConverter.GetBytes(mediaErrors).CopyTo(log, 160);
        return log;
    }

    private static Snapshot With(DiskInfo disk) => Snap.Healthy().With(s => s.Storage = Section<StorageInfo>.Of(new StorageInfo { Disks = [disk] }));
    private static List<Finding> Run(ICheck c, DiskInfo d) => c.Run(With(d)).ToList();
    private static DiskInfo Hdd(params (int, int, int, long)[] attrs)
    {
        var (data, thr) = AtaPage(attrs);
        return new DiskInfo { Number = 1, Model = "WDC WD10EZEX", BusType = "SATA", MediaType = "HDD", Ata = new AtaSmart { Attributes = HealthParsers.ParseAtaSmart(data, thr) } };
    }

    [Fact]
    public void ParsesAtaSmartPage()
    {
        var (data, thr) = AtaPage((0x05, 100, 36, 8), (0xC2, 115, 0, 0x0000_2D00_0F00_0026), (0x09, 40, 0, 0x0005_0000_9C40));
        var attrs = HealthParsers.ParseAtaSmart(data, thr);
        Assert.Equal(3, attrs.Count);
        Assert.Equal("Reallocated sectors", attrs[0].Name);
        Assert.Equal(8, attrs[0].Raw);
        Assert.Equal(36, attrs[0].Threshold);
        Assert.Equal(0x26, attrs[1].Raw);          // 38 °C from the low byte only
        Assert.Equal(0x9C40, attrs[2].Raw);        // 40,000 hours; high bytes ignored
    }

    [Fact]
    public void ParsesNvmeHealthLog()
    {
        var n = HealthParsers.ParseNvmeHealth(NvmeLog(tempC: 41, used: 7, written: 9_324_343, hours: 326, mediaErrors: 2));
        Assert.Equal(41, n.TemperatureC);
        Assert.Equal(7, n.PercentageUsed);
        Assert.Equal(326, n.PowerOnHours);
        Assert.Equal(2, n.MediaErrors);
        Assert.Equal(4.77, n.TerabytesWritten, 2);
    }

    [Fact]
    public void HealthyDisksGiveNoFindings()
    {
        var nvme = new DiskInfo { Model = "Micron 2400", BusType = "NVMe", MediaType = "SSD", IsBoot = true, Health = "Healthy", Nvme = HealthParsers.ParseNvmeHealth(NvmeLog()) };
        var report = ScanEngine.Evaluate(With(nvme), CheckRegistry.All);
        Assert.Empty(report.Findings);
        Assert.Equal(DiskState.Good, DiskHealth.Assess(nvme).State);
        Assert.Empty(ScanEngine.Evaluate(With(Hdd((0x05, 100, 36, 0), (0xC5, 100, 0, 0))), CheckRegistry.All).Findings);
    }

    [Fact]
    public void BadSectors()
    {
        Assert.Equal(Severity.Warning, Assert.Single(Run(new Sto002BadSectors(), Hdd((0x05, 100, 36, 12)))).Severity);
        var pending = Hdd((0x05, 100, 36, 12), (0xC5, 100, 0, 3));
        Assert.Equal(Severity.Critical, Assert.Single(Run(new Sto002BadSectors(), pending)).Severity);
        Assert.Equal(DiskState.Bad, DiskHealth.Assess(pending).State);
        Assert.Equal(DiskState.Caution, DiskHealth.Assess(Hdd((0x05, 100, 36, 12))).State);
    }

    [Fact]
    public void AttributeAtThresholdIsFailing()
    {
        var f = Assert.Single(Run(new Sto001SmartFailure(), Hdd((0x05, 30, 36, 2000))));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains("threshold 36", f.Evidence);
        var predicted = Hdd((0x05, 100, 36, 0));
        predicted.Ata!.PredictFailure = true;
        Assert.Single(Run(new Sto001SmartFailure(), predicted));
    }

    [Theory]
    [InlineData(0x01, 100, Severity.Critical)]   // spare low bit
    [InlineData(0x08, 100, Severity.Critical)]   // read-only
    [InlineData(0x00, 3, Severity.Critical)]     // spare 3% < threshold 10%
    [InlineData(0x02, 100, null)]                // temperature bit alone is STO-005's job
    public void NvmeCriticalWarning(int warning, int spare, Severity? expected)
    {
        var d = new DiskInfo { BusType = "NVMe", MediaType = "SSD", Nvme = HealthParsers.ParseNvmeHealth(NvmeLog(criticalWarning: warning, spare: spare)) };
        Assert.Equal(expected, Run(new Sto003NvmeHealth(), d).SingleOrDefault()?.Severity);
    }

    [Theory]
    [InlineData(50, null)]
    [InlineData(85, Severity.Warning)]
    [InlineData(100, Severity.Critical)]
    [InlineData(140, Severity.Critical)]
    public void SsdWear(int used, Severity? expected)
    {
        var d = new DiskInfo { BusType = "NVMe", MediaType = "SSD", Nvme = HealthParsers.ParseNvmeHealth(NvmeLog(used: used)) };
        Assert.Equal(expected, Run(new Sto004SsdWear(), d).SingleOrDefault()?.Severity);
    }

    [Fact]
    public void SataSsdWearFromVendorAttribute()
    {
        var (data, thr) = AtaPage((0xB1, 15, 0, 3000));   // Samsung wear leveling: 15% life left
        var d = new DiskInfo { BusType = "SATA", MediaType = "SSD", Ata = new AtaSmart { Attributes = HealthParsers.ParseAtaSmart(data, thr) } };
        Assert.Equal(85, DiskHealth.Wear(d));
        Assert.Equal(Severity.Warning, Assert.Single(Run(new Sto004SsdWear(), d)).Severity);
    }

    [Fact]
    public void TemperatureHddAndNvme()
    {
        Assert.Single(Run(new Sto005Temperature(), Hdd((0xC2, 100, 0, 55))));
        Assert.Empty(Run(new Sto005Temperature(), Hdd((0xC2, 100, 0, 42))));
        var hot = new DiskInfo { BusType = "NVMe", MediaType = "SSD", Nvme = HealthParsers.ParseNvmeHealth(NvmeLog(tempC: 74)) };
        Assert.Equal(Severity.Warning, Assert.Single(Run(new Sto005Temperature(), hot)).Severity);
    }

    [Fact]
    public void SystemOnHddAndOldHdd()
    {
        var hdd = Hdd((0x09, 100, 0, 52000));
        hdd.IsBoot = true;
        Assert.Single(Run(new Sto010SystemOnHdd(), hdd));
        Assert.Single(Run(new Sto006PowerOnHours(), hdd));
    }

    [Fact]
    public void WindowsHealthAndUsb()
    {
        Assert.Equal(Severity.Critical, Run(new Sto013WindowsDiskHealth(), new DiskInfo { Health = "Unhealthy" }).Single().Severity);
        Assert.Single(Run(new Sto016UsbBridge(), new DiskInfo { BusType = "USB" }));
    }

    [Fact]
    public void TrimAlignmentAndVirtualDisks()
    {
        var s = Snap.Healthy().With(x => x.Storage = Section<StorageInfo>.Of(new StorageInfo
        {
            TrimDisabled = true,
            Disks = [new DiskInfo { Number = 0, MediaType = "SSD", BusType = "SATA" }],
            Partitions = [new PartitionInfo { DiskNumber = 0, PartitionNumber = 1, DriveLetter = "C:", OffsetBytes = 32256 }],
            VirtualDisks = [new VirtualDiskInfo { Name = "Pool", Health = "Warning", Resiliency = "Mirror", OperationalStatus = ["Degraded"] }],
        }));
        Assert.Single(new Sto011TrimDisabled().Run(s));
        Assert.Contains("32,256", Assert.Single(new Sto014Misaligned().Run(s)).Evidence);
        Assert.Equal(Severity.Warning, Assert.Single(new Sto012VirtualDiskDegraded().Run(s)).Severity);
    }

    [Fact]
    public void DirtyVolume()
    {
        var s = Snap.Healthy().With(x => x.Volumes!.Value![0].Dirty = true);
        Assert.Single(new Sto009DirtyVolume().Run(s));
    }
}
