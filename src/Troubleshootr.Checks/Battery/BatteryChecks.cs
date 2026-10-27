using Troubleshootr.Checks.Crashes;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Battery;

/// <summary>BAT-001: a worn battery: it holds much less than when new.</summary>
public sealed class Bat001BatteryWear : Check
{
    public const double Worn = 70, VeryWorn = 50; // % of design capacity; laptop makers replace under warranty below about 60–80%

    public override string Id => "BAT-001";
    public override Category Category => Category.Battery;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Power)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var b in s.Power!.Value!.Batteries)
        {
            if (b.HealthPercent is not { } health || health >= Worn) continue;
            yield return F(health < VeryWorn ? Severity.Critical : Severity.Warning,
                health < VeryWorn ? "The battery is badly worn" : "The battery is worn",
                $"{b.Name ?? "Battery"} holds {b.FullMwh / 1000.0:0.#} Wh of its {b.DesignMwh / 1000.0:0.#} Wh design ({health:0}% health)"
                + (b.CycleCount is { } c ? $", {c} charge cycles" : "") + (b.Manufactured is { } m ? $", made {m:yyyy-MM}" : "") + ".",
                health < VeryWorn
                    ? "Replace the battery: runtime is under half of new, and a worn battery can shut the laptop off suddenly. Check it isn't swelling (a lifting keyboard or trackpad, a bulging case); if it is, stop using it."
                    : "Expect noticeably shorter battery life. Replace it when it no longer lasts long enough; meanwhile a charge limit (80%) in the maker's app slows further wear.");
        }
    }
}

/// <summary>BAT-002: plugged in but not charging (a charge limit in the maker's app, or a charger/battery fault).</summary>
public sealed class Bat002NotCharging : Check
{
    private const double FullEnough = 95;

    public override string Id => "BAT-002";
    public override Category Category => Category.Battery;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Power)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var b in s.Power!.Value!.Batteries)
        {
            if (!b.PowerOnline || b.Charging || b.Discharging || b.ChargePercent is not { } pct || pct >= FullEnough) continue;
            var limit = Math.Abs(pct - Math.Round(pct / 10) * 10) < 2 && pct is >= 50 and <= 90; // stopping at 60/80% is a set limit
            yield return F(Severity.Info, $"Plugged in but not charging at {pct:0}%",
                $"{b.Name ?? "The battery"} is on mains power, holding at {pct:0}%{(limit ? " (a round number, so most likely a charge limit)" : "")}.",
                "Usually a battery-care charge limit in the maker's app (MSI Center, Lenovo Vantage, ASUS Armoury Crate/MyASUS, Dell Power Manager, HP Smart Battery) or the BIOS. If no limit is set, try another charger and outlet; then suspect the battery or charging port.");
        }
    }
}

/// <summary>BAT-003: sleep problems: timers or devices that keep waking the PC, and (as admin) what stops it sleeping now.</summary>
public sealed class Bat003SleepProblems : Check
{
    private const int TimerWakes = 3, DeviceWakes = 5;

    public override string Id => "BAT-003";
    public override Category Category => Category.Battery;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Power)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Power!.Value!;
        var timers = p.Wakes.Where(w => w.IsTimer).ToList();
        if (timers.Count >= TimerWakes)
        {
            var owners = timers.GroupBy(w => w.Owner ?? w.Source ?? "a timer").OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ({g.Count()})");
            yield return F(Severity.Info, "A timer keeps waking the PC from sleep", $"{N(timers.Count, "timed wake")} in {p.Days} days: {string.Join(", ", owners)}.",
                "Control Panel > Power Options > Change plan settings > Advanced > Sleep > Allow wake timers: Disable (or Important only). Windows Update maintenance and backup apps are the usual owners.");
        }
        var devices = p.Wakes.Where(w => !w.IsTimer && !w.IsUser && w.Source?.StartsWith("Device", StringComparison.OrdinalIgnoreCase) == true)
            .GroupBy(w => w.Source!).Where(g => g.Count() >= DeviceWakes).OrderByDescending(g => g.Count()).ToList();
        if (devices.Count > 0)
            yield return F(Severity.Info, "A device keeps waking the PC from sleep", string.Join("; ", devices.Take(3).Select(g => $"{g.Key}: {N(g.Count(), "time")}")) + $" in {p.Days} days.",
                "In Device Manager open that device (mouse, network adapter) > Power Management and untick \"Allow this device to wake the computer\".");
        var blockers = p.Requests.Where(r => r.BlocksSleep).ToList();
        if (blockers.Count > 0)
            yield return F(Severity.Info, blockers.Count == 1 ? $"Something is stopping sleep: {blockers[0].Name}" : $"{blockers.Count} things are stopping sleep",
                string.Join("; ", blockers.Take(5).Select(r => $"{r.Name} ({r.Type.ToLowerInvariant()}{(r.Reason is { } why ? $": {why}" : "")})")) + ".",
                "Fine while music or a download is running. If the PC never sleeps on its own, close or update that program, or check its settings (media players, game launchers and remote-access tools are common).");
    }
}

/// <summary>BAT-004: the battery drains fast during Modern Standby ("I closed the lid and it was flat in the morning").</summary>
public sealed class Bat004StandbyDrain : Check
{
    private const double FastPerHour = 3; // % of a full charge per hour; a healthy laptop loses about 0.5–1.5%/h
    private const int MinSessions = 2;

    public override string Id => "BAT-004";
    public override Category Category => Category.Battery;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Power)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Power!.Value!;
        var sessions = p.Standby.Where(x => x.FullMwh > 0).ToList();
        if (sessions.Count < MinSessions) yield break;
        var hours = sessions.Sum(x => x.Minutes) / 60;
        var percent = sessions.Sum(x => x.DrainMwh * 100.0 / x.FullMwh);
        var perHour = hours > 0 ? percent / hours : 0;
        if (perHour < FastPerHour) yield break;
        var deep = sessions.Sum(x => x.DeepSleepPercent * x.Minutes) / sessions.Sum(x => x.Minutes);
        yield return F(Severity.Info, $"The battery drains {perHour:0.#}% an hour in standby",
            $"{N(sessions.Count, "standby session")} on battery in {p.Days} days, {hours:0.#} h in total; deepest sleep state reached {deep:0}% of the time.",
            "Something keeps the laptop busy while asleep. Set it to hibernate after an hour (Settings > Power), unplug docks and USB devices before closing the lid, and update the BIOS and drivers. 'powercfg /sleepstudy' (as admin) names the culprit.");
    }
}

/// <summary>BAT-005: a desktop lost power instantly, more than once (no blue screen, no button held): power supply or wall power.</summary>
public sealed class Bat005PowerCuts : Check
{
    private const int Repeated = 2;

    public override string Id => "BAT-005";
    public override Category Category => Category.Battery;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    /// <summary>The Kernel-Power 41 events this check reports (empty unless there are enough to count); WIN-007 leaves them out.</summary>
    internal static List<LogEvent> Cuts(Snapshot s)
    {
        if (s.System?.Value is not { IsLaptop: false, IsVirtualMachine: false } || s.Events?.Value is not { } ev) return [];
        var cuts = ev.Find(BlueScreens.KernelPower, 41).Within(s, Ev.Window)
            // Only events that positively say "no button pressed" count; an event without those fields proves nothing.
            .Where(e => e.Get("BugcheckCode") is null or "0" && e.Get("LongPowerButtonPressDetected") == "false"
                        && e.Get("PowerButtonTimestamp") == "0" && e.Get("SleepInProgress") is null or "0").ToList();
        return cuts.Count >= Repeated ? cuts : [];
    }

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var cuts = Cuts(s);
        if (cuts.Count == 0) yield break;
        yield return F(Severity.Warning, $"The power cut out {cuts.Count} times",
            $"{cuts.Count} sudden shutdowns in {Ev.Window} days with no blue screen and nobody holding the power button (Kernel-Power 41). Latest {s.Meta.Local(cuts.Max(e => e.TimeUtc)):yyyy-MM-dd HH:mm}.",
            "If there wasn't a power outage: test or replace the power supply (an old or undersized one cuts out under gaming load), check the power cable and power strip, and try another outlet or a UPS.");
    }
}

/// <summary>PRF-010: a laptop held back by power: the charger can't keep up (battery draining while plugged in), or battery saver is on.</summary>
public sealed class Prf010PowerLimits : Check
{
    private const int DrainMw = 1000; // more than 1 W out of the battery while plugged in

    public override string Id => "PRF-010";
    public override Category Category => Category.Performance;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Power), nameof(Snapshot.Tuning)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Power!.Value!;
        if (p.Batteries.FirstOrDefault(b => b.PowerOnline && b.Discharging && b.RateMw is < -DrainMw) is { } b)
            yield return F(Severity.Info, "The battery is draining while plugged in",
                $"On mains power but the battery is losing {-b.RateMw!.Value / 1000.0:0.#} W{(b.ChargePercent is { } pct ? $" (at {pct:0}%)" : "")}: the charger can't supply what the laptop is using.",
                "Use the laptop's own charger (or a USB-C charger with at least its rated wattage; phone chargers are too weak). A charger that's too small makes the laptop slow itself down and the battery run flat while plugged in.");
        if (s.Tuning?.Value?.BatterySaverOn == true)
            yield return F(Severity.Info, "Battery saver is on", "Windows battery saver (energy saver) is switched on, which lowers performance and pauses background sync.",
                "Turn it off in Settings > System > Power & battery if the PC feels slow, or set it to come on only at a low charge.");
    }
}
