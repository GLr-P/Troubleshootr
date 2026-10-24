
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Crashes;

/// <summary>Shared helpers for the event and crash checks: everything is judged relative to when the snapshot was taken.</summary>
internal static class Ev
{
    public const int Window = 30;

    public static IEnumerable<LogEvent> Within(this IEnumerable<LogEvent> events, Snapshot s, int days) =>
        events.Where(e => e.TimeUtc <= s.Meta.CollectedUtc.AddMinutes(5) && (s.Meta.CollectedUtc - e.TimeUtc).TotalDays <= days);

    public static List<BlueScreen> RecentBlueScreens(Snapshot s, int days = Window) =>
        BlueScreens.From(s).Where(b => (s.Meta.CollectedUtc - b.TimeUtc).TotalDays <= days).ToList();

    public static string Line(Snapshot s, BlueScreen b) =>
        $"{s.Meta.Local(b.TimeUtc):yyyy-MM-dd} {BugChecks.Hex(b.Code)} {BugChecks.Name(b.Code)}{(b.Driver is { } d ? $" in {d}" : "")}";

    public static string Times(int n) => n == 1 ? "once" : $"{n} times";

    public static string? App(LogEvent e) => AppCrashes.App(e);
    public static bool IsShell(string app) => AppCrashes.IsShell(app);
}

/// <summary>CRA-001: blue screens in the last 30 days, from dump files and the event log.</summary>
public sealed class Cra001BlueScreens : Check
{
    private const int CriticalCount = 2; // more than this many in 30 days is a pattern, not a fluke

    public override string Id => "CRA-001";
    public override Category Category => Category.Crashes;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events), nameof(Snapshot.Crashes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var list = Ev.RecentBlueScreens(s);
        if (list.Count == 0) yield break;
        var latest = string.Join("; ", list.Take(3).Select(b => Ev.Line(s, b))) + (list.Count > 3 ? $"; and {list.Count - 3} more" : "");
        var meaning = list[0].Info?.Meaning is { } m ? $" Latest: {m}" : "";
        yield return F(list.Count > CriticalCount ? Severity.Critical : Severity.Warning,
            list.Count == 1 ? "The PC had a blue screen in the last 30 days" : $"The PC had {list.Count} blue screens in the last 30 days",
            latest + "." + meaning,
            "Open Events & Crashes for each crash's code and meaning. Update the drivers named there (graphics, chipset, network) from the PC or part maker; undo any recent overclock or new hardware.");
    }
}

/// <summary>CRA-002: what the blue screens have in common. The same code points at one cause; many different codes point at hardware.</summary>
public sealed class Cra002CrashPattern : Check
{
    private const int MixedCodes = 3;

    public override string Id => "CRA-002";
    public override Category Category => Category.Crashes;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events), nameof(Snapshot.Crashes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var list = Ev.RecentBlueScreens(s);
        if (list.Count < 2) yield break;
        var codes = list.GroupBy(b => b.Code).OrderByDescending(g => g.Count()).ToList();
        if (codes.Count >= MixedCodes)
        {
            yield return F(Severity.Warning, "Blue screens with different causes each time",
                $"{list.Count} crashes with {codes.Count} different codes: {string.Join(", ", codes.Select(g => $"{BugChecks.Name(g.Key)} ×{g.Count()}"))}.",
                "Random crash codes usually mean hardware rather than one bad driver: test the RAM (Windows Memory Diagnostic or MemTest86), remove any CPU/RAM overclock or XMP/EXPO profile, check temperatures and the power supply.");
        }
        else if (codes[0].Count() >= 2)
        {
            var code = codes[0].Key;
            yield return F(Severity.Warning, "The same blue screen keeps happening",
                $"{BugChecks.Hex(code)} {BugChecks.Name(code)} {Ev.Times(codes[0].Count())} in 30 days.",
                BugChecks.Lookup(code)?.Meaning is { } m ? $"{m} Fix that cause and the crashes should stop." : "Search the code on learn.microsoft.com (Bug check code reference) for its usual causes.");
        }
    }
}

/// <summary>CRA-003: apps that keep crashing or hanging. Explorer and the Start menu are WIN-016.</summary>
public sealed class Cra003AppCrashes : Check
{
    private const int Repeated = 3;

    public override string Id => "CRA-003";
    public override Category Category => Category.Crashes;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var ev = s.Events!.Value!;
        var groups = ev.Find("Application Error", 1000).Concat(ev.Find("Application Hang", 1002)).Concat(ev.Find(".NET Runtime", 1026))
            .Within(s, Ev.Window)
            .Select(e => (e, app: Ev.App(e)))
            .Where(x => x.app is { Length: > 0 } a && !Ev.IsShell(a))
            .GroupBy(x => x.app!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= Repeated)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (groups.Count == 0) yield break;
        string Describe(IGrouping<string, (LogEvent e, string? app)> g)
        {
            var hangs = g.Count(x => x.e.Id == 1002);
            var module = g.Select(x => x.e.Get("ModuleName")).Where(m => m is { Length: > 0 }).GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(m => m.Count()).FirstOrDefault()?.Key;
            var crashes = g.Count() - hangs;
            var counts = string.Join(", ", new[] { crashes > 0 ? $"{crashes} crashes" : null, hangs > 0 ? $"{hangs} hangs" : null }.Where(x => x is not null));
            return $"{g.Key}: {counts}{(module is not null ? $" (in {module})" : "")}";
        }
        yield return F(Severity.Warning, groups.Count == 1 ? $"{groups[0].Key} keeps crashing" : "Some apps keep crashing",
            string.Join("; ", groups.Take(4).Select(Describe)) + " in the last 30 days.",
            "Update or reinstall the app. If many apps crash in the same module (ntdll.dll, a graphics or antivirus DLL), suspect that component, RAM or system files (sfc /scannow).");
    }
}

/// <summary>CRA-004: Windows Error Reporting has kernel reports (driver hangs, GPU resets, crashes it recovered from).</summary>
public sealed class Cra004KernelReports : Check
{
    public override string Id => "CRA-004";
    public override Category Category => Category.Crashes;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Crashes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var kernel = s.Crashes!.Value!.Reports
            .Where(r => r.Kind is "Kernel" or "LiveKernelEvent" && (s.Meta.CollectedUtc - r.TimeUtc).TotalDays <= Ev.Window)
            .GroupBy(r => $"{r.Kind} {r.Subject}").OrderByDescending(g => g.Count()).ToList();
        if (kernel.Count == 0) yield break;
        static string Describe(IGrouping<string, WerReport> g)
        {
            var r = g.First();
            var code = uint.TryParse(r.Subject, System.Globalization.NumberStyles.HexNumber, null, out var c) ? c : (uint?)null;
            var what = r.Kind == "Kernel" ? "crash" : "recovered problem";
            var name = code is { } x ? $"{BugChecks.Hex(x)}{(BugChecks.Lookup(x) is { } i ? $" {i.Name}" : "")}" : r.Subject;
            return $"{what} {name} ×{g.Count()}";
        }
        yield return F(Severity.Info, "Windows recorded hardware or driver error reports",
            string.Join(", ", kernel.Take(5).Select(Describe)) + " in the last 30 days.",
            "Recovered problems (LiveKernelEvent) didn't cause a blue screen; 0x141 and 0x117 are the graphics driver resetting. Update the driver for the device involved.");
    }
}

/// <summary>CRA-006: crash dumps are turned off, so a blue screen leaves nothing to diagnose.</summary>
public sealed class Cra006DumpsDisabled : Check
{
    public override string Id => "CRA-006";
    public override Category Category => Category.Crashes;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Crashes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        if (s.Crashes!.Value!.CrashDumpEnabled != 0) yield break;
        yield return F(Severity.Info, "Crash dumps are turned off", "System Properties > Startup and Recovery: write debugging information is set to None.",
            "Set it to Automatic memory dump (or Small memory dump) so the next blue screen can be diagnosed.");
    }
}

/// <summary>MEM-009: blue-screen codes that usually mean bad RAM.</summary>
public sealed class Mem009MemoryCrashes : Check
{
    public override string Id => "MEM-009";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events), nameof(Snapshot.Crashes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var list = Ev.RecentBlueScreens(s);
        var memory = list.Where(b => b.Info?.Hint == CrashHint.Memory).ToList();
        // Two memory-type crashes, or one plus a spread of unrelated codes (which also points at RAM).
        if (memory.Count < 2 && !(memory.Count == 1 && list.Select(b => b.Code).Distinct().Count() >= 3)) yield break;
        yield return F(Severity.Warning, "Crashes point to a memory problem",
            $"{memory.Count} of {N(list.Count, "blue screen")} in 30 days {(memory.Count == 1 ? "has a memory-type code" : "have memory-type codes")}: {string.Join(", ", memory.Select(b => BugChecks.Name(b.Code)).Distinct())}.",
            "Test the RAM: Windows Memory Diagnostic (mdsched) or MemTest86 for several passes. Turn off XMP/EXPO or any memory overclock first; reseat or test the sticks one at a time.");
    }
}

/// <summary>GPU-005: blue screens caused by the graphics driver.</summary>
public sealed class Gpu005GraphicsCrashes : Check
{
    public override string Id => "GPU-005";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events), nameof(Snapshot.Crashes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var gpu = Ev.RecentBlueScreens(s)
            .Where(b => b.Info?.Hint == CrashHint.Graphics || b.Driver is { } d && BlueScreens.GpuDrivers.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        if (gpu.Count == 0) yield break;
        yield return F(Severity.Warning, "The graphics driver crashed the PC",
            string.Join("; ", gpu.Take(3).Select(b => Ev.Line(s, b))) + ".",
            "Install the latest graphics driver from NVIDIA, AMD or Intel (or the laptop maker), using a clean install. If it keeps happening, remove any GPU overclock and check GPU temperatures and power cables.");
    }
}

/// <summary>DRV-007: drivers the crash dumps name as the cause.</summary>
public sealed class Drv007CrashingDrivers : Check
{
    public override string Id => "DRV-007";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events), nameof(Snapshot.Crashes)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var drivers = Ev.RecentBlueScreens(s).Where(b => b.Driver is { } d && !BlueScreens.IsKernel(d))
            .GroupBy(b => b.Driver!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ToList();
        if (drivers.Count == 0) yield break;
        yield return F(Severity.Warning, drivers.Count == 1 ? $"A driver crashed the PC: {drivers[0].Key}" : "Drivers crashed the PC",
            string.Join(", ", drivers.Select(g => $"{g.Key} {Ev.Times(g.Count())}")) + " in the last 30 days (from the crash dumps).",
            "Find which device or program the driver belongs to (search its file name), then update or uninstall it. Antivirus, VPN, RGB and anti-cheat drivers are frequent culprits.");
    }
}
