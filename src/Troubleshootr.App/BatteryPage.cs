using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>The Battery tab: battery health and charging, the power source, wakes from sleep, standby drain and sleep blockers.</summary>
public static class BatteryPage
{
    public static List<InfoGroup> Build(Snapshot s)
    {
        if (s.WhyMissing(nameof(Snapshot.Power)) is { } why)
            return [new InfoGroup("Battery & power") { Note = $"Not available: {why}. Run a scan with Battery & power selected." }];
        var p = s.Power!.Value!;
        var groups = new List<InfoGroup>();

        if (p.Batteries.Count == 0)
            groups.Add(new InfoGroup("Battery") { Note = p.BatteriesUnavailable is { } bu ? $"Not available: {bu}." : "No battery: this PC runs on mains power only." });
        foreach (var b in p.Batteries)
        {
            var g = new InfoGroup(b.Name is { } n ? $"Battery: {n}" : "Battery")
                .Row("Maker", b.Manufacturer)
                .Row("Type", b.Chemistry switch { "LION" or "Li-I" => "Lithium-ion", "LiP" or "LIP" => "Lithium polymer", "NiMH" => "Nickel-metal hydride", var other => other })
                .Row("Health", b.HealthPercent is { } h ? $"{h:0}% of design capacity" : null)
                .Row("Full charge", b.FullMwh > 0 ? $"{b.FullMwh / 1000.0:0.0} Wh" : null)
                .Row("Design capacity", b.DesignMwh > 0 ? $"{b.DesignMwh / 1000.0:0.0} Wh" : null)
                .Row("Charge now", b.ChargePercent is { } c ? $"{c:0}% ({b.RemainingMwh / 1000.0:0.0} Wh)" : null)
                .Row("Charge cycles", b.CycleCount?.ToString() ?? "Not reported")
                .Row("Made", b.Manufactured?.ToString("yyyy-MM-dd"))
                .Row("State", b.Charging ? "Charging" : b.Discharging ? (b.PowerOnline ? "Discharging while plugged in" : "On battery") : b.PowerOnline ? "Plugged in, not charging" : "Idle")
                .Row("Rate", b.RateMw is { } r and not 0 ? $"{(r > 0 ? "+" : "−")}{Math.Abs(r) / 1000.0:0.0} W" : null)
                .Row("Voltage", b.VoltageMv is { } v ? $"{v / 1000.0:0.00} V" : null);
            if (b.HealthPercent is { } health)
            {
                const double worn = Checks.Battery.Bat001BatteryWear.Worn, bad = Checks.Battery.Bat001BatteryWear.VeryWorn; // same limits as the finding
                g.Badge = health >= worn ? "Good" : health >= bad ? "Worn" : "Replace";
                g.BadgeKey = health >= worn ? "Ok" : health >= bad ? "Warning" : "Critical";
            }
            groups.Add(g);
        }

        var t = s.Tuning?.Value;
        groups.Add(new InfoGroup("Power")
            .Row("Power source", p.OnAc switch { true => "Plugged in", false => "Battery", null => null })
            .Row("Charge", p.ChargePercent is { } pct && p.Batteries.Count > 0 ? $"{pct}%" : null)
            .Row("Battery saver", t?.BatterySaverOn)
            .Row("Power plan", t?.PowerPlan));

        var wakes = new InfoGroup($"Wakes from sleep (last {p.Days} days)") { Columns = ["Woke", "Slept for", "Woken by"], Note = p.Wakes.Count == 0 ? "None recorded." : null };
        foreach (var w in p.Wakes.Take(30))
            wakes.Table.Add([s.Meta.Local(w.TimeUtc).ToString("yyyy-MM-dd HH:mm"), Duration(w.SleepMinutes), w.Owner is { } o ? $"Timer: {o}" : w.Source ?? "Not recorded"]);
        groups.Add(wakes);

        if (p.Standby.Count > 0)
        {
            var standby = new InfoGroup("Standby on battery (30 min or more)") { Columns = ["Ended", "Length", "Used", "Per hour", "Deepest sleep"] };
            foreach (var x in p.Standby.Take(20))
                standby.Table.Add([s.Meta.Local(x.EndUtc).ToString("yyyy-MM-dd HH:mm"), Duration(x.Minutes), x.FullMwh > 0 ? $"{x.DrainMwh * 100.0 / x.FullMwh:0.#}%" : "",
                    $"{x.PercentPerHour:0.#}%/h", $"{x.DeepSleepPercent:0}%"]);
            groups.Add(standby);
        }

        var requests = new InfoGroup("Keeping the PC awake now") { Columns = p.Requests.Count > 0 ? ["Blocks", "Type", "Program or driver", "Why"] : null };
        if (p.RequestsUnavailable is { } ru) requests.Note = ru == "needs admin" ? "Restart as admin to see what stops the PC sleeping." : $"Not available: {ru}.";
        else if (p.Requests.Count == 0) requests.Note = "Nothing.";
        foreach (var r in p.Requests)
            requests.Table.Add([r.Kind switch { "SYSTEM" => "Sleep", "DISPLAY" => "Screen off", "AWAYMODE" => "Sleep (away mode)", var k => k.ToLowerInvariant() }, r.Type.ToLowerInvariant(), r.Name, r.Reason ?? ""]);
        groups.Add(requests);
        return groups;
    }

    private static string Duration(double minutes) =>
        minutes >= 1440 ? $"{(int)(minutes / 1440)} d {(int)(minutes % 1440 / 60)} h" : minutes >= 60 ? $"{(int)(minutes / 60)} h {(int)(minutes % 60)} min" : $"{minutes:0} min";
}
