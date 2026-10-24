using System.Diagnostics;
using System.Text;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>
/// Per-process CPU, memory and disk between two calls of <see cref="Sample"/>, like Task Manager. Used by the scan's
/// performance sample and the live Processes tab. Processes Windows won't open even for limited queries are skipped.
/// </summary>
public sealed class ProcessSampler
{
    private sealed record Times(string Name, long Cpu100ns, ulong IoBytes, string? Path);

    private readonly int _selfId = Process.GetCurrentProcess().Id;
    private Dictionary<int, Times> _last;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, string?> _paths = new();

    public ProcessSampler() => _last = Read();

    /// <summary>Processes with their use since the previous call (the first call compares with construction time).</summary>
    public List<ProcessSample> Sample()
    {
        var seconds = Math.Max(0.05, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        var now = Read();
        var cores = Environment.ProcessorCount;
        var memory = new Dictionary<int, (long Private, long Working)>();
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                try { memory[p.Id] = (p.PrivateMemorySize64, p.WorkingSet64); }
                catch (InvalidOperationException) { }
            }
        var list = now.Select(kv =>
        {
            _last.TryGetValue(kv.Key, out var before);
            memory.TryGetValue(kv.Key, out var mem);
            return new ProcessSample
            {
                Name = kv.Value.Name,
                Pid = kv.Key,
                CpuPercent = before is null ? 0 : Math.Round(Math.Max(0, 100.0 * (kv.Value.Cpu100ns - before.Cpu100ns) / (seconds * 1e7 * cores)), 1),
                DiskBytesPerSec = before is null ? 0 : (long)((kv.Value.IoBytes - Math.Min(kv.Value.IoBytes, before.IoBytes)) / seconds),
                PrivateMB = mem.Private / 1048576,
                WorkingSetMB = mem.Working / 1048576,
                Path = kv.Value.Path,
            };
        }).ToList();
        _last = now;
        return list;
    }

    public int SelfId => _selfId;

    private Dictionary<int, Times> Read()
    {
        var result = new Dictionary<int, Times>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == _selfId || p.Id == 0) continue;
                var h = PerfApi.OpenProcess(PerfApi.ProcessQueryLimitedInformation, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var cpu = PerfApi.GetProcessTimes(h, out _, out _, out var k, out var u) ? k + u : 0;
                    var io = PerfApi.GetProcessIoCounters(h, out var c) ? c.ReadTransferCount + c.WriteTransferCount : 0;
                    // The path doesn't change for a process id, so it's looked up once.
                    if (!_paths.TryGetValue(p.Id, out var path))
                    {
                        var sb = new StringBuilder(1024);
                        var size = sb.Capacity;
                        _paths[p.Id] = path = PerfApi.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
                    }
                    result[p.Id] = new Times(p.ProcessName + ".exe", cpu, io, path);
                }
                finally { PerfApi.CloseHandle(h); }
            }
        }
        return result;
    }
}
