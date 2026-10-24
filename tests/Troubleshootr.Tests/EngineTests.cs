using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Troubleshootr.Checks;
using Troubleshootr.Collectors;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

public class EngineTests
{
    private sealed class Throws : Check
    {
        public override string Id => "TST-001";
        public override Category Category => Category.Storage;
        public override IReadOnlyList<string> Needs => [nameof(Snapshot.System)];
        public override IEnumerable<Finding> Run(Snapshot s) => throw new InvalidOperationException("boom");
    }

    private sealed class SlowCollector(string section, TimeSpan delay, TimeSpan timeout) : Collector<List<GpuInfo>>
    {
        public override string Section => section;
        public override TimeSpan Timeout => timeout;
        protected override List<GpuInfo> Collect(CollectContext ctx, CancellationToken ct) { Thread.Sleep(delay); return []; }
    }

    private sealed class VolumesStub : Collector<List<VolumeInfo>>
    {
        public override string Section => nameof(Snapshot.Volumes);
        protected override List<VolumeInfo> Collect(CollectContext ctx, CancellationToken ct) => [];
    }

    private sealed class FailingCollector : Collector<SystemInfo>
    {
        public override string Section => nameof(Snapshot.System);
        protected override SystemInfo Collect(CollectContext ctx, CancellationToken ct) => throw new CollectorException("WMI: test failure");
    }

    [Fact]
    public void MissingSectionIsReportedNotThrown()
    {
        var s = Snap.Healthy().With(x => x.Memory = Section<MemoryInfo>.Missing("needs admin"));
        var report = ScanEngine.Evaluate(s, CheckRegistry.All);
        Assert.Contains(report.NotChecked, n => n.CheckId == "MEM-003" && n.Reason == "Memory: needs admin");
        s.Memory = null;
        Assert.Contains(ScanEngine.Evaluate(s, CheckRegistry.All).NotChecked, n => n.Reason == "Memory: not collected in this scan");
    }

    [Fact]
    public void CrashingCheckBecomesNotChecked()
    {
        var report = ScanEngine.Evaluate(Snap.Healthy(), [new Throws()]);
        Assert.StartsWith("check failed: InvalidOperationException", Assert.Single(report.NotChecked).Reason);
    }

    [Fact]
    public async Task CollectorTimeoutAndFailureAreRecorded()
    {
        var report = await ScanEngine.ScanAsync(
            [new SlowCollector(nameof(Snapshot.Gpus), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100)), new FailingCollector()],
            CheckRegistry.All, new ScanOptions());
        Assert.Equal("timed out after 0.1 s", report.Snapshot!.Gpus!.Unavailable);
        Assert.Equal("WMI: test failure", report.Snapshot.System!.Unavailable);
        Assert.Contains(report.Findings, f => f.CheckId == "WIN-011");
        Assert.True(report.DurationMs < 2000, $"scan waited for the timed-out collector ({report.DurationMs} ms)");
    }

    [Fact]
    public async Task CategoryScanRunsOnlyNeededCollectors()
    {
        var report = await ScanEngine.ScanAsync(
            [new VolumesStub(), new FailingCollector()],
            CheckRegistry.All, new ScanOptions { Categories = [Category.Memory] });
        Assert.Null(report.Snapshot!.Volumes);
        Assert.NotNull(report.Snapshot.System); // always collected for the header
    }

    [Fact]
    public void AnonymizerBlanksSensitiveFields()
    {
        var s = Snap.Healthy().With(x => x.Mem().Modules[0].SerialNumber = "ABC123");
        Anonymizer.Apply(s);
        var json = Json.Serialize(s);
        Assert.DoesNotContain("TEST-PC", json);
        Assert.DoesNotContain("ABC123", json);
        Assert.Contains("Contoso", json);
        Assert.True(s.Meta.Anonymized);
    }

    [Fact]
    public void SnapshotRoundTripsThroughJson()
    {
        var s = Snap.Healthy().With(x => x.Gpus = Section<List<GpuInfo>>.Missing("needs admin"));
        var back = Json.Deserialize<Snapshot>(Json.Serialize(s));
        Assert.Equal(Json.Serialize(s), Json.Serialize(back));
        Assert.Equal("needs admin", back.WhyMissing(nameof(Snapshot.Gpus)));
    }
}

public class RegistryTests
{
    [Fact]
    public void ChecksAreConsistent()
    {
        var ids = CheckRegistry.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var c in CheckRegistry.All)
        {
            Assert.Matches(new Regex(@"^[A-Z]{3}-\d{3}$"), c.Id);
            foreach (var need in c.Needs) Assert.Contains(need, Snapshot.SectionNames);
        }
    }

    [Fact]
    public void EverySectionHasExactlyOneCollector()
    {
        foreach (var section in Snapshot.SectionNames)
            Assert.Single(CollectorRegistry.All, c => c.Section == section);
    }

    [Fact]
    public void EveryCheckIsInTheCatalogue()
    {
        var catalogue = File.ReadAllText(Path.Combine(Paths.Repo(), "docs", "CHECKS.md"));
        foreach (var c in CheckRegistry.All) Assert.Contains($"| {c.Id} |", catalogue);
    }
}

public static class Paths
{
    public static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
