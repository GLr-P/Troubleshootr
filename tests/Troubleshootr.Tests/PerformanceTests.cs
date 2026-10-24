using Troubleshootr.Checks;
using Troubleshootr.Checks.Performance;
using Troubleshootr.Collectors;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 6: startup, software, the performance sample and tuning settings.</summary>
public class PerformanceTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();
    private static Severity? Sev(ICheck check, Snapshot s) => Run(check, s).SingleOrDefault()?.Severity;
    private static PerformanceInfo Perf(Snapshot s) => s.Performance!.Value!;
    private static TuningInfo Tune(Snapshot s) => s.Tuning!.Value!;

    private static Snapshot WithStartup(int apps, int tasks, int off = 0) => Snap.Healthy().With(s =>
    {
        var e = s.Startup!.Value!.Entries;
        for (var i = 0; i < apps; i++) e.Add(new StartupEntry { Name = $"App{i}", Location = "HKCU Run" });
        for (var i = 0; i < tasks; i++) e.Add(new StartupEntry { Name = $"Updater{i}", Location = "Scheduled task" });
        for (var i = 0; i < off; i++) e.Add(new StartupEntry { Name = $"Off{i}", Location = "HKLM Run", Enabled = false });
    });

    [Fact]
    public void Prf001CountsLikeTaskManager()
    {
        Assert.Empty(Run(new Prf001StartupPrograms(), WithStartup(7, 10, 5)));  // tasks and disabled ones don't count
        Assert.Equal(Severity.Info, Sev(new Prf001StartupPrograms(), WithStartup(13, 6)));
        Assert.Equal(Severity.Warning, Sev(new Prf001StartupPrograms(), WithStartup(16, 0)));
    }

    [Fact]
    public void Prf003AndCpu002()
    {
        Assert.Empty(Run(new Prf003ResourceHogs(), Snap.Healthy()));
        var miner = Snap.Healthy().With(s => { Perf(s).CpuPercent = 92; Perf(s).Processes.Add(new ProcessSample { Name = "svchosts.exe", CpuPercent = 88 }); });
        Assert.Contains("svchosts.exe is using 88% of the CPU", Assert.Single(Run(new Prf003ResourceHogs(), miner)).Evidence);
        Assert.Contains("svchosts.exe 88%", Assert.Single(Run(new Cpu002HighCpu(), miner)).Evidence);
        var leak = Snap.Healthy().With(s => Perf(s).Processes.Add(new ProcessSample { Name = "chrome.exe", PrivateMB = 9000 })); // 16 GB usable
        Assert.Contains("56% of RAM", Assert.Single(Run(new Prf003ResourceHogs(), leak)).Evidence);
    }

    [Fact]
    public void Cpu003OnlyOnMainsUnderLoad()
    {
        Snapshot With(double cpu, double speed, bool ac) => Snap.Healthy().With(s => { Perf(s).CpuPercent = cpu; Perf(s).ProcessorPerformancePercent = speed; Tune(s).OnAcPower = ac; });
        Assert.Equal(Severity.Info, Sev(new Cpu003Throttled(), With(80, 35, true)));
        Assert.Empty(Run(new Cpu003Throttled(), With(80, 35, false)));   // battery: slowing down is expected
        Assert.Empty(Run(new Cpu003Throttled(), With(10, 35, true)));    // idle: low clocks are normal
        Assert.Empty(Run(new Cpu003Throttled(), With(80, 110, true)));
    }

    [Fact]
    public void Cpu008PowerPlan()
    {
        Assert.Empty(Run(new Cpu008PowerPlan(), Snap.Healthy()));
        var saver = Snap.Healthy().With(s => { Tune(s).PowerPlanGuid = TuningInfo.PowerSaverGuid; Tune(s).PowerPlan = "Power saver"; });
        Assert.Equal(Severity.Warning, Sev(new Cpu008PowerPlan(), saver));
        Assert.Equal(Severity.Info, Sev(new Cpu008PowerPlan(), saver.With(s => s.Sys().HasBattery = true)));
        Assert.Contains("capped at 70%", Assert.Single(Run(new Cpu008PowerPlan(), Snap.Healthy().With(s => Tune(s).MaxCpuOnAc = 70))).Evidence);
    }

    [Fact]
    public void Sto018Sto019Sto020()
    {
        var space = Snap.Healthy().With(s => Tune(s).Cleanup =
        [
            new() { Name = "Previous Windows installation (Windows.old)", Bytes = 22L * Snap.GB },
            new() { Name = "Your temp files", Bytes = 3L * Snap.GB, AtLeast = true },
            new() { Name = "Error reports", Bytes = 1000 },
        ]);
        var f = Assert.Single(Run(new Sto018Reclaimable(), space));
        Assert.Contains("Your temp files: at least 3 GB", f.Evidence);
        Assert.DoesNotContain("Error reports", f.Evidence);

        Assert.Equal(Severity.Warning, Sev(new Sto019PageFile(), Snap.Healthy().With(s => { Tune(s).PageFileSettings.Clear(); Tune(s).PageFiles.Clear(); })));
        var usb = Snap.Healthy().With(s =>
        {
            s.Volumes!.Value!.Add(new VolumeInfo { Root = @"E:\", DriveType = "Removable" });
            Tune(s).PageFiles = [new() { Path = @"E:\pagefile.sys", AllocatedMB = 4096 }];
        });
        Assert.Contains("removable", Assert.Single(Run(new Sto019PageFile(), usb)).Title);
        Assert.Empty(Run(new Sto019PageFile(), Snap.Healthy()));

        var busy = Snap.Healthy().With(s => { Perf(s).DiskActivePercent = 99; Perf(s).Processes.Add(new ProcessSample { Name = "SearchIndexer.exe", DiskBytesPerSec = 30L * 1048576 }); });
        Assert.Contains("SearchIndexer.exe 30 MB/s", Assert.Single(Run(new Sto020DiskBusy(), busy)).Evidence);
    }

    [Fact]
    public void Prf005AndPrf009()
    {
        var s = Snap.Healthy().With(x => x.Software!.Value!.Programs.AddRange([new() { Name = "Driver Booster 12" }, new() { Name = "7-Zip 24.08 (x64)" }]));
        var f = Assert.Single(Run(new Prf005Bloatware(), s));
        Assert.Contains("Driver Booster 12", f.Evidence);
        Assert.DoesNotContain("7-Zip", f.Evidence);
        Assert.Empty(Run(new Prf009Browsers(), Snap.Healthy()));
        Assert.Equal(Severity.Info, Sev(new Prf009Browsers(), Snap.Healthy().With(x => x.Software!.Value!.BrowserExtensions["Chrome: Default"] = 31)));
    }

    [Fact]
    public void AnonymizerScrubsSidsFromTaskNames()
    {
        var s = Snap.Healthy().With(x => x.Startup!.Value!.Entries.Add(new StartupEntry
        {
            Name = "OneDrive Startup Task-S-1-5-21-1252696972-597946724-2226361577-1001", Location = "Scheduled task",
            Command = @"C:\Users\jsmith\AppData\Local\Microsoft\OneDrive\OneDriveLauncher.exe",
        }));
        Anonymizer.Apply(s);
        var e = s.Startup!.Value!.Entries.Single();
        Assert.Equal("OneDrive Startup Task-S-1-5-21-[redacted]", e.Name);
        Assert.Equal(@"C:\Users\[user]\AppData\Local\Microsoft\OneDrive\OneDriveLauncher.exe", e.Command);
    }

    [Fact]
    public void ProcessSamplerSeesThisMachine()
    {
        var sampler = new ProcessSampler();
        var list = sampler.Sample();
        Assert.NotEmpty(list);
        Assert.DoesNotContain(list, p => p.Pid == sampler.SelfId);
        Assert.All(list, p => Assert.InRange(p.CpuPercent, 0, 100.5));
    }
}
