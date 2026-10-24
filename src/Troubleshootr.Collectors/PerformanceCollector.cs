using System.Diagnostics;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>
/// Samples the PC for a few seconds: total CPU (minus this app's own work), each disk's active time, processor speed, and
/// per-process CPU, memory and disk. Like Task Manager, but one snapshot.
/// </summary>
public sealed class PerformanceCollector : Collector<PerformanceInfo>
{
    private const int SampleMs = 3000;
    private const int TopByCpu = 8;
    private const int TopByMemory = 8;
    private const int TopByDisk = 5;

    public override string Section => nameof(Snapshot.Performance);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    protected override PerformanceInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var self = Process.GetCurrentProcess();
        var info = new PerformanceInfo();

        IntPtr query = IntPtr.Zero, disk = IntPtr.Zero, speed = IntPtr.Zero;
        if (PerfApi.PdhOpenQueryW(null, IntPtr.Zero, out query) == 0)
        {
            if (PerfApi.PdhAddEnglishCounterW(query, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out disk) != 0) disk = IntPtr.Zero;
            if (PerfApi.PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out speed) != 0) speed = IntPtr.Zero;
            PerfApi.PdhCollectQueryData(query);
        }
        else query = IntPtr.Zero;

        try
        {
            PerfApi.GetSystemTimes(out var idle0, out var kernel0, out var user0);
            var self0 = self.TotalProcessorTime.Ticks;
            var sampler = new ProcessSampler();
            var watch = Stopwatch.StartNew();
            if (ct.WaitHandle.WaitOne(SampleMs)) ct.ThrowIfCancellationRequested();
            PerfApi.GetSystemTimes(out var idle1, out var kernel1, out var user1);
            self.Refresh();
            var self1 = self.TotalProcessorTime.Ticks;
            var samples = sampler.Sample();
            info.SampleSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);

            // Kernel time includes idle time. Everything is in 100 ns units summed over all cores.
            var total = (kernel1 - kernel0) + (user1 - user0);
            var busy = total - (idle1 - idle0) - (self1 - self0);
            info.CpuPercent = total > 0 ? Math.Round(Math.Max(0, 100.0 * busy / total), 1) : 0;

            if (query != IntPtr.Zero)
            {
                PerfApi.PdhCollectQueryData(query);
                // The busiest single disk, not the average: one disk stuck at 100% is the problem even if another is idle.
                var idleByDisk = disk != IntPtr.Zero ? PerfApi.Values(disk).Where(kv => kv.Key != "_Total").ToList() : [];
                if (idleByDisk.Count > 0) info.DiskActivePercent = Math.Round(Math.Min(100, Math.Max(0, 100 - idleByDisk.Min(kv => kv.Value))), 1);
                if (speed != IntPtr.Zero && PerfApi.Value(speed) is { } s) info.ProcessorPerformancePercent = Math.Round(s, 1);
            }

            info.ProcessCount = samples.Count;
            info.Processes = samples.OrderByDescending(p => p.CpuPercent).Take(TopByCpu)
                .Concat(samples.OrderByDescending(p => p.PrivateMB).Take(TopByMemory))
                .Concat(samples.Where(p => p.DiskBytesPerSec > 0).OrderByDescending(p => p.DiskBytesPerSec).Take(TopByDisk))
                .GroupBy(p => p.Pid).Select(g => g.First())
                .OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateMB).ToList();
        }
        finally { if (query != IntPtr.Zero) PerfApi.PdhCloseQuery(query); }
        return info;
    }
}
