using Troubleshootr.Checks;
using Troubleshootr.Checks.Battery;
using Troubleshootr.Checks.Crashes;
using Troubleshootr.Collectors;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 11: battery wear and charging, sleep and wake, standby drain, power cuts, power limits.</summary>
public class BatteryTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();
    private static PowerInfo Pow(Snapshot s) => s.Power!.Value!;

    /// <summary>The owner's laptop battery as the driver reports it (80.3 Wh design, 79.3 Wh full, on mains, full).</summary>
    private static BatteryPack Msi() => new()
    {
        Name = "BIF0_9", Manufacturer = "MSI", Chemistry = "LION", DesignMwh = 80256, FullMwh = 79298, RemainingMwh = 79298, VoltageMv = 17206, RateMw = 0, PowerOnline = true,
    };

    private static Snapshot Laptop(Action<BatteryPack>? change = null) => Snap.Healthy().With(s =>
    {
        s.Sys().PcSystemType = 2;
        var b = Msi();
        change?.Invoke(b);
        Pow(s).Batteries.Add(b);
    });

    [Fact]
    public void BatteryIoctlsParse()
    {
        var info = new byte[36];
        BitConverter.GetBytes(0x80000000u).CopyTo(info, 0);       // capabilities: system battery
        "LION"u8.ToArray().CopyTo(info, 8);
        BitConverter.GetBytes(80256u).CopyTo(info, 12);
        BitConverter.GetBytes(79298u).CopyTo(info, 16);
        BitConverter.GetBytes(0u).CopyTo(info, 32);                // MSI reports no cycle count
        var p = PowerCollector.ParseInformation(info)!;
        Assert.Equal(("LION", 80256L, 79298L, (int?)null), (p.Chemistry, p.DesignMwh, p.FullMwh, p.CycleCount));
        Assert.Equal(98.8, Math.Round(p.HealthPercent!.Value, 1));

        var status = new byte[16];
        BitConverter.GetBytes(1u | 2u).CopyTo(status, 0);         // on mains and discharging
        BitConverter.GetBytes(40000u).CopyTo(status, 4);
        BitConverter.GetBytes(16800u).CopyTo(status, 8);
        BitConverter.GetBytes(-12500).CopyTo(status, 12);
        PowerCollector.ApplyStatus(p, status);
        Assert.True(p.PowerOnline && p.Discharging && !p.Charging);
        Assert.Equal((40000L, 16800, -12500), (p.RemainingMwh!.Value, p.VoltageMv!.Value, p.RateMw!.Value));

        Assert.Equal(new DateTime(2023, 5, 14), PowerCollector.ParseDate([14, 5, 0xE7, 0x07]));
        Assert.Null(PowerCollector.ParseDate([0, 0, 0, 0]));
    }

    [Fact]
    public void ReviewRegressions()
    {
        // A USB UPS reports a battery too, but not a system battery: it isn't the laptop's.
        var ups = new byte[36];
        BitConverter.GetBytes(80256u).CopyTo(ups, 12);
        Assert.Null(PowerCollector.ParseInformation(ups));
        // Folder names with brackets mustn't confuse the timer owner's short name.
        Assert.Equal("foo.exe", new WakeEvent { TimerOwner = @"\Device\HarddiskVolume3\Program Files (x86)\Foo\foo.exe" }.Owner);
        Assert.Equal("svchost.exe (NT TASK\\Microsoft\\Windows\\UpdateOrchestrator\\Reboot)",
            new WakeEvent { TimerOwner = @"\Device\HarddiskVolume3\Windows\System32\svchost.exe (NT TASK\Microsoft\Windows\UpdateOrchestrator\Reboot)" }.Owner);
        // Power cuts reported by BAT-005 aren't repeated by WIN-007.
        static Dictionary<string, string> Cut(int _) => new() { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "0", ["LongPowerButtonPressDetected"] = "false", ["SleepInProgress"] = "0" };
        var desktop = Snap.Healthy().Events(3, BlueScreens.KernelPower, 41, everyDays: 2, level: 1, data: Cut);
        Assert.Single(Run(new Bat005PowerCuts(), desktop));
        Assert.Empty(Run(new Troubleshootr.Checks.Windows.Win007UnexpectedShutdowns(), desktop));
    }

    [Fact]
    public void PowercfgRequestsParse()
    {
        const string text = "DISPLAY:\r\n[PROCESS] \\Device\\HarddiskVolume3\\Program Files\\Google\\Chrome\\Application\\chrome.exe\r\nVideo Wake Lock\r\n\r\nSYSTEM:\r\n[DRIVER] Realtek High Definition Audio (HDAUDIO\\FUNC_01&VEN_10EC&DEV_0295)\r\nAn audio stream is currently in use.\r\n[PROCESS] \\Device\\HarddiskVolume3\\Program Files\\RustDesk\\rustdesk.exe\r\n\r\nAWAYMODE:\r\nNone.\r\n\r\nEXECUTION:\r\nNone.\r\n";
        var r = PowerCollector.ParseRequests(text);
        Assert.Equal(3, r.Count);
        Assert.Equal(("DISPLAY", "PROCESS", "chrome.exe", "Video Wake Lock"), (r[0].Kind, r[0].Type, r[0].Name, r[0].Reason));
        Assert.True(r[1].BlocksSleep);
        Assert.Equal("rustdesk.exe", r[2].Name);
        Assert.Null(r[2].Reason);
    }

    [Fact]
    public void HealthyHasNoFindings()
    {
        foreach (var s in new[] { Snap.Healthy(), Laptop() })
            foreach (var c in new ICheck[] { new Bat001BatteryWear(), new Bat002NotCharging(), new Bat003SleepProblems(), new Bat004StandbyDrain(), new Bat005PowerCuts(), new Prf010PowerLimits() })
                Assert.Empty(Run(c, s));
    }

    [Fact]
    public void Bat001Wear()
    {
        Assert.Equal(Severity.Warning, Assert.Single(Run(new Bat001BatteryWear(), Laptop(b => b.FullMwh = 52000))).Severity);
        var bad = Assert.Single(Run(new Bat001BatteryWear(), Laptop(b => { b.FullMwh = 30000; b.CycleCount = 812; })));
        Assert.Equal(Severity.Critical, bad.Severity);
        Assert.Contains("holds 30 Wh of its 80.3 Wh design (37% health), 812 charge cycles", bad.Evidence);
        Assert.Empty(Run(new Bat001BatteryWear(), Laptop(b => b.DesignMwh = 0))); // design unknown
        Assert.Empty(Run(new Bat001BatteryWear(), Laptop(b => b.FullMwh = 85000))); // new battery above design
    }

    [Fact]
    public void Bat002ChargeLimit()
    {
        var f = Assert.Single(Run(new Bat002NotCharging(), Laptop(b => b.RemainingMwh = 63400)));
        Assert.Contains("at 80%", f.Title);
        Assert.Contains("charge limit", f.Evidence);
        Assert.Empty(Run(new Bat002NotCharging(), Laptop(b => { b.RemainingMwh = 63400; b.Charging = true; })));
        Assert.Empty(Run(new Bat002NotCharging(), Laptop(b => { b.RemainingMwh = 63400; b.PowerOnline = false; b.Discharging = true; })));
    }

    [Fact]
    public void Bat003Sleep()
    {
        var timers = Laptop().With(s => { for (var i = 0; i < 3; i++) Pow(s).Wakes.Add(new() { TimeUtc = Snap.Now.AddDays(-i), TimerOwner = @"\Device\HarddiskVolume3\Windows\System32\svchost.exe (SystemEventsBroker)" }); });
        Assert.Contains("3 timed wakes", Assert.Single(Run(new Bat003SleepProblems(), timers)).Evidence);
        var mouse = Laptop().With(s => { for (var i = 0; i < 6; i++) Pow(s).Wakes.Add(new() { Source = "Device -HID-compliant mouse" }); });
        Assert.Contains("HID-compliant mouse: 6 times", Assert.Single(Run(new Bat003SleepProblems(), mouse)).Evidence);
        var button = Laptop().With(s => { for (var i = 0; i < 9; i++) Pow(s).Wakes.Add(new() { Source = "Power Button" }); });
        Assert.Empty(Run(new Bat003SleepProblems(), button));
        var blocked = Laptop().With(s => { Pow(s).RequestsUnavailable = null; Pow(s).Requests.Add(new() { Kind = "SYSTEM", Type = "PROCESS", Name = "rustdesk.exe" }); Pow(s).Requests.Add(new() { Kind = "DISPLAY", Type = "PROCESS", Name = "chrome.exe" }); });
        Assert.Equal("Something is stopping sleep: rustdesk.exe", Assert.Single(Run(new Bat003SleepProblems(), blocked)).Title);
    }

    [Fact]
    public void Bat004StandbyDrain()
    {
        // 8 h overnight using 40% of an 80 Wh battery = 5%/h.
        var fast = Laptop().With(s => { Pow(s).Standby.Add(new() { Minutes = 480, DrainMwh = 32000, FullMwh = 80000, DeepSleepPercent = 12 }); Pow(s).Standby.Add(new() { Minutes = 60, DrainMwh = 4000, FullMwh = 80000, DeepSleepPercent = 20 }); });
        var f = Assert.Single(Run(new Bat004StandbyDrain(), fast));
        Assert.Equal("The battery drains 5% an hour in standby", f.Title);
        Assert.Contains("deepest sleep state reached 13% of the time", f.Evidence);
        var fine = Laptop().With(s => { Pow(s).Standby.Add(new() { Minutes = 480, DrainMwh = 4000, FullMwh = 80000 }); Pow(s).Standby.Add(new() { Minutes = 480, DrainMwh = 4000, FullMwh = 80000 }); });
        Assert.Empty(Run(new Bat004StandbyDrain(), fine));
        Assert.Empty(Run(new Bat004StandbyDrain(), Laptop().With(s => Pow(s).Standby.Add(new() { Minutes = 480, DrainMwh = 40000, FullMwh = 80000 })))); // one night isn't a pattern
    }

    [Fact]
    public void Bat005PowerCutsOnDesktops()
    {
        static Dictionary<string, string> Cut(int _) => new() { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "0", ["LongPowerButtonPressDetected"] = "false", ["SleepInProgress"] = "0" };
        var desktop = Snap.Healthy().Events(3, BlueScreens.KernelPower, 41, everyDays: 2, level: 1, data: Cut);
        Assert.Equal("The power cut out 3 times", Assert.Single(Run(new Bat005PowerCuts(), desktop)).Title);
        Assert.Empty(Run(new Bat005PowerCuts(), Snap.Healthy().With(s => s.Sys().PcSystemType = 2).Events(3, BlueScreens.KernelPower, 41, level: 1, data: Cut)));
        static Dictionary<string, string> Held(int _) => new() { ["BugcheckCode"] = "0", ["LongPowerButtonPressDetected"] = "true" };
        Assert.Empty(Run(new Bat005PowerCuts(), Snap.Healthy().Events(3, BlueScreens.KernelPower, 41, level: 1, data: Held)));
    }

    [Fact]
    public void Prf010ChargerTooWeak()
    {
        var f = Assert.Single(Run(new Prf010PowerLimits(), Laptop(b => { b.Discharging = true; b.RateMw = -14000; b.RemainingMwh = 50000; })));
        Assert.Contains("losing 14 W (at 63%)", f.Evidence);
        Assert.Equal("Battery saver is on", Assert.Single(Run(new Prf010PowerLimits(), Laptop().With(s => s.Tuning!.Value!.BatterySaverOn = true))).Title);
    }
}
