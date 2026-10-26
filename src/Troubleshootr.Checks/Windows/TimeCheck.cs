using Troubleshootr.Core;

namespace Troubleshootr.Checks.Windows;

/// <summary>WIN-012: the clock is wrong, or Windows can't keep it right (time sync off).</summary>
public sealed class Win012Clock : Check
{
    private const double Off = 120;            // Kerberos and many sign-ins fail beyond 5 min; 2 min is already noticeable
    private const double VeryOff = 86400;      // a day or more: HTTPS certificates look expired or not yet valid

    public override string Id => "WIN-012";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Connectivity)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var c = s.Connectivity!.Value!;
        var syncOff = c.TimeServiceStart == 4 || string.Equals(c.TimeSyncType, "NoSync", StringComparison.OrdinalIgnoreCase);
        var why = c.TimeServiceStart == 4 ? "The Windows Time service is disabled." : syncOff ? "Time sync is set to NoSync." : "";
        if (c.ClockOffsetSeconds is { } off && Math.Abs(off) >= Off)
        {
            var a = Math.Abs(off);
            var amount = a >= 86400 ? $"{a / 86400:0.#} days" : a >= 3600 ? $"{a / 3600:0.#} hours" : $"{a / 60:0} minutes";
            yield return F(a >= VeryOff ? Severity.Critical : Severity.Warning, $"The clock is {amount} {(off > 0 ? "fast" : "slow")}",
                $"Compared with {c.ClockSource}. {why}".Trim(),
                "Settings > Time & language > Date & time: turn on \"Set time automatically\" and press Sync now. A wrong clock breaks secure websites, sign-ins and updates. "
                + "If it's wrong again after every restart, the motherboard's CMOS battery is flat.");
        }
        else if (syncOff)
            yield return F(Severity.Warning, "Windows doesn't keep the clock in sync", why,
                "Turn on \"Set time automatically\" in Settings > Time & language > Date & time (and set the Windows Time service back to Manual if it was disabled).");
    }
}
