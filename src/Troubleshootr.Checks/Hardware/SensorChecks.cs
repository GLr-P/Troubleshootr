using System.Text.RegularExpressions;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Hardware;

/// <summary>CPU-004: the processor runs hot, or is at its throttle limit (TjMax). Needs the PawnIO driver and admin for a reading.</summary>
public sealed class Cpu004CpuTemperature : Check
{
    private const double Hot = 90;        // most desktop and laptop CPUs throttle between 95 and 105 °C
    private const double TooHot = 100;

    public override string Id => "CPU-004";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Sensors)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = s.Sensors!.Value!;
        if (x.CpuTemp is not { } t) yield break;
        var atLimit = x.CpuHeadroom is <= 0;
        if (t.Value < Hot && !atLimit) yield break;
        var load = s.Performance?.Value?.CpuPercent is { } cpu ? $" while the CPU was {cpu:0}% busy" : "";
        yield return F(t.Value >= TooHot || atLimit ? Severity.Critical : Severity.Warning, $"The processor is running hot: {t.Value:0} °C",
            $"{t.Hardware} {t.Name}: {t.Value:0} °C{load}{(x.CpuHeadroom is { } h ? $"; {h:0} °C below its throttle limit" : "")}.",
            "Clean the dust out of the fans and heatsink, and make sure the vents aren't blocked (laptops: use it on a hard surface). If it's still hot, the thermal paste is dried out or the cooler isn't seated properly; a technician can redo it.");
    }
}

/// <summary>GPU-008: the graphics card runs hot (core, or the hot spot on cards that report one).</summary>
public sealed class Gpu008GpuTemperature : Check
{
    private const double HotCore = 85;      // NVIDIA and AMD cards slow down from about 83-90 °C core
    private const double HotSpot = 105;     // hot spot limit is usually 105-110 °C

    public override string Id => "GPU-008";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Sensors)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var gpu in s.Sensors!.Value!.Temps("GPU").GroupBy(r => r.Hardware))
        {
            // Memory junction (GDDR6X runs ~95-105 °C normally) and hot spot have their own limits; never judge them as "core".
            var core = gpu.FirstOrDefault(r => r.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                       ?? gpu.Where(r => !Regex.IsMatch(r.Name, "Memory|Junction|Hot ?Spot|VRM", RegexOptions.IgnoreCase)).OrderByDescending(r => r.Value).FirstOrDefault();
            var spot = gpu.FirstOrDefault(r => r.Name.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) >= 0);
            var issues = new List<string>();
            if (core?.Value > HotCore) issues.Add($"{core.Name} {core.Value:0} °C");
            if (spot?.Value > HotSpot) issues.Add($"hot spot {spot.Value:0} °C");
            if (issues.Count == 0) continue;
            yield return F(Severity.Warning, $"The graphics card is running hot: {gpu.Key}", string.Join(", ", issues) + ".",
                "Clean the card's fans and the case filters, and check the case has airflow (front intake, rear exhaust). A hot spot far above the core temperature points to dried thermal paste.");
        }
    }
}

/// <summary>MEM-011: a DDR5 memory stick runs hot (its own sensor, read through PawnIO).</summary>
public sealed class Mem011DimmTemperature : Check
{
    private const double Hot = 85;   // JEDEC: DDR5 refreshes twice as often above 85 °C, which slows it and hints at poor airflow

    public override string Id => "MEM-011";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Sensors)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var hot = s.Sensors!.Value!.Temps("Memory").Where(r => r.Value > Hot).ToList();
        if (hot.Count == 0) yield break;
        yield return F(Severity.Warning, "A memory stick is running hot", string.Join(", ", hot.Select(r => $"{r.Hardware} {r.Value:0} °C")) + ".",
            "Improve airflow over the RAM (a case fan blowing across it), and check that overclocked/XMP voltages aren't higher than the stick's rating.");
    }
}
