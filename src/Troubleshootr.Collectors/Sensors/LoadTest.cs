using System.Diagnostics;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Sensors;

/// <summary>
/// The optional CPU stress test on the Sensors tab: every core busy for 30 s while the CPU's speed (PDH "% Processor
/// Performance", no driver needed) and, with PawnIO, its temperature are sampled each second. A healthy, well-cooled
/// CPU holds its speed; one that slows down by a quarter or reaches its limit temperature is throttling.
/// </summary>
public static class LoadTest
{
    public const int Seconds = 30;
    private const double ThrottleDrop = 0.25;   // speed in the last 10 s vs the first 5 s
    private const double HotHeadroom = 3;       // °C left before TjMax
    private const double BaseSpeed = 90;        // % of base clock: below this under full load the CPU is held back
    private const double AmdLimit = 95;         // Ryzen 7000/9000 hold Tjmax at 95 °C

    public sealed record Sample(int Second, double? SpeedPercent, double? TempC);

    public sealed record Result(List<Sample> Samples, double? MaxTemp, double? StartSpeed, double? EndSpeed, bool Throttled, string Summary);

    /// <summary>
    /// Runs the test. Pass the Sensors tab's open <paramref name="session"/> (and stop reading it meanwhile); without one,
    /// a session is opened first, before the load starts, so the first samples really are the start of the load.
    /// </summary>
    public static async Task<Result> RunAsync(Action<Sample>? progress, CancellationToken ct, SensorSession? session = null)
    {
        Native.DisablePowerThrottling();
        var onBattery = PerfApi.GetSystemPowerStatus(out var power) && power.ACLineStatus == 0;
        var own = session is null ? SensorSession.Open() : null;
        var sensors = session ?? own!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;  // fully loaded, but the PC stays usable
            var x = 1.0;
            while (!stop.IsCancellationRequested) for (var i = 0; i < 100_000; i++) x = Math.Sqrt(x + i);
            return x;
        }, TaskCreationOptions.LongRunning)).ToList();

        var samples = new List<Sample>();
        double? minHeadroom = null;
        try
        {
            using var speed = new SpeedCounter();
            for (var s = 1; s <= Seconds; s++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var readings = sensors.Read();
                var info = new SensorsInfo { Readings = readings };
                if (info.CpuHeadroom is { } h) minHeadroom = Math.Min(minHeadroom ?? h, h);
                var sample = new Sample(s, speed.Read(), info.CpuTemp?.Value);
                samples.Add(sample);
                progress?.Invoke(sample);
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(workers).ConfigureAwait(false);
            own?.Dispose();
        }
        return Judge(samples, minHeadroom, onBattery);
    }

    /// <summary>The verdict, separated from the timing so it can be tested with made-up samples.</summary>
    public static Result Judge(List<Sample> samples, double? minHeadroom, bool onBattery = false)
    {
        double? Avg(IEnumerable<Sample> xs) => xs.Where(x => x.SpeedPercent is not null).Select(x => x.SpeedPercent!.Value).DefaultIfEmpty(double.NaN).Average() is var a && !double.IsNaN(a) ? a : null;
        var start = Avg(samples.Take(5));
        var end = Avg(samples.Skip(Math.Max(0, samples.Count - 10)));
        var maxTemp = samples.Where(x => x.TempC is not null).Select(x => x.TempC).Max();
        // Boost (PL2/PPT) settling to the sustained limit is normal and stays above base speed; throttling drops below it.
        var slowed = start is > 0 && end is not null && end < start * (1 - ThrottleDrop) && end < BaseSpeed;
        // Intel reports degrees left before TjMax; AMD doesn't, so its hottest reading is compared with ~95 °C (Ryzen's limit).
        var hot = minHeadroom is { } h ? h <= HotHeadroom : maxTemp >= AmdLimit;
        var parts = new List<string>();
        if (start is not null && end is not null) parts.Add($"speed {start:0}% of base at the start, {end:0}% at the end");
        if (maxTemp is not null) parts.Add($"hottest {maxTemp:0} °C");
        if (minHeadroom is not null) parts.Add($"{minHeadroom:0} °C from the limit at worst");
        var verdict = slowed || hot ? "The CPU throttled under load" : "No throttling";
        var why = slowed && hot ? " (it reached its temperature limit and slowed down)" : hot ? " (it reached its temperature limit)" : slowed ? " (it slowed down by more than a quarter)" : "";
        return new Result(samples, maxTemp, start, end, slowed || hot, $"{verdict}{why}: {string.Join(", ", parts)}."
            + (onBattery ? " The PC was on battery, where Windows limits the speed on purpose; plug in for a full test." : "")
            + (maxTemp is null ? " Temperatures need advanced sensors." : ""));
    }

    /// <summary>"\Processor Information(_Total)\% Processor Performance": speed as a % of base (over 100 = boost).</summary>
    private sealed class SpeedCounter : IDisposable
    {
        private readonly IntPtr _query;
        private readonly IntPtr _counter;

        public SpeedCounter()
        {
            if (PerfApi.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            PerfApi.PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _counter);
            PerfApi.PdhCollectQueryData(_query);
        }

        public double? Read() => _counter != IntPtr.Zero && PerfApi.PdhCollectQueryData(_query) == 0 ? PerfApi.Value(_counter) : null;

        public void Dispose() { if (_query != IntPtr.Zero) PerfApi.PdhCloseQuery(_query); }
    }
}
