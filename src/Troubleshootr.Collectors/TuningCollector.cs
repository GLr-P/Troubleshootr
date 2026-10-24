using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Power plan and CPU cap, page file, visual-effects settings, and how much space temp/update/old-Windows folders take.</summary>
public sealed class TuningCollector : Collector<TuningInfo>
{
    private const int CleanupBudgetMs = 2500;
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    public override string Section => nameof(Snapshot.Tuning);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(8);

    protected override TuningInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new TuningInfo();
        Power(info);

        var memoryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        if (Reg.Get(RegistryHive.LocalMachine, memoryKey, "PagingFiles") is string[] settings)
            info.PageFileSettings = settings.Where(x => x.Trim().Length > 0).Select(x => x.Trim()).ToList();
        try
        {
            info.PageFiles = Wmi.QueryOptional("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage")
                .Select(r => new PageFileUse { Path = r.Str("Name") ?? "", AllocatedMB = r.Long("AllocatedBaseSize") ?? 0, CurrentMB = r.Long("CurrentUsage") ?? 0, PeakMB = r.Long("PeakUsage") ?? 0 })
                .ToList();
        }
        catch (CollectorException) { }

        info.VisualEffects = (int?)Reg.Num(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting");
        info.Transparency = Reg.Num(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency") is { } tr ? tr == 1 : null;
        info.GameMode = Reg.Num(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") is { } gm ? gm == 1 : true; // on unless switched off
        info.GpuScheduling = Reg.Num(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") switch { 2 => true, 1 => false, _ => null };

        ct.ThrowIfCancellationRequested();
        Cleanup(info, ct);
        return info;
    }

    private static void Power(TuningInfo info)
    {
        if (PerfApi.GetSystemPowerStatus(out var status))
        {
            info.OnAcPower = status.ACLineStatus switch { 1 => true, 0 => false, _ => null };
            info.BatterySaverOn = (status.SystemStatusFlag & 1) != 0;
        }
        try
        {
            if (PerfApi.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return;
            var scheme = Marshal.PtrToStructure<Guid>(ptr);
            PerfApi.LocalFree(ptr);
            info.PowerPlanGuid = scheme.ToString();
            uint size = 0;
            PerfApi.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size > 0)
            {
                var buffer = new byte[size];
                if (PerfApi.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0)
                    info.PowerPlan = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            }
            var sub = ProcessorSubgroup;
            var setting = MaxProcessorState;
            if (PerfApi.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac) == 0) info.MaxCpuOnAc = (int)ac;
            if (PerfApi.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc) == 0) info.MaxCpuOnBattery = (int)dc;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>Folder sizes, sharing one time budget; a folder cut short is marked AtLeast.</summary>
    private static void Cleanup(TuningInfo info, CancellationToken ct)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(windows) ?? @"C:\";
        var targets = new[]
        {
            ("Your temp files", Path.GetTempPath()),
            ("Windows temp files", Path.Combine(windows, "Temp")),
            ("Previous Windows installation (Windows.old)", Path.Combine(drive, "Windows.old")),
            ("Downloaded Windows updates", Path.Combine(windows, @"SoftwareDistribution\Download")),
            ("Error reports", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER")),
        };
        // Each folder gets its own time budget, counted in parallel, so one huge temp folder can't starve the others.
        var results = new CleanupItem?[targets.Length];
        Parallel.For(0, targets.Length, i =>
        {
            var (name, path) = targets[i];
            if (!Directory.Exists(path)) return;
            var (bytes, complete) = Size(path, Stopwatch.StartNew(), ct);
            results[i] = new CleanupItem { Name = name, Path = path, Bytes = bytes, AtLeast = !complete };
        });
        info.Cleanup = results.Where(r => r is not null).Select(r => r!).ToList();
    }

    private static (long Bytes, bool Complete) Size(string root, Stopwatch watch, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (watch.ElapsedMilliseconds > CleanupBudgetMs || ct.IsCancellationRequested) return (total, false);
            var dir = new DirectoryInfo(stack.Pop());
            try
            {
                foreach (var f in dir.EnumerateFiles()) total += f.Length;
                // Don't follow junctions/symlinks (Windows.old has some that loop back).
                foreach (var d in dir.EnumerateDirectories())
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d.FullName);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
        return (total, true);
    }
}
