using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Sensors;

/// <summary>
/// Loads the right LibreHardwareMonitorLib build (lhm\x64, lhm\x86 or lhm\arm64 next to the exe; the library has no AnyCPU build).
/// Must be registered before any method that mentions LibreHardwareMonitor types is compiled.
/// </summary>
public static class LhmLoader
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            if (new AssemblyName(e.Name).Name != "LibreHardwareMonitorLib" || Folder() is not { } dir) return null;
            var path = Path.Combine(dir, "LibreHardwareMonitorLib.dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }

    /// <summary>The folder holding this process's build (x64, x86 or arm64), or null on other architectures.</summary>
    public static string? Folder() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lhm", "x64"),
        Architecture.X86 => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lhm", "x86"),
        Architecture.Arm64 => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lhm", "arm64"),
        _ => null,
    };
}

/// <summary>
/// An open LibreHardwareMonitor "Computer" (CPU, GPU, memory, motherboard; storage and batteries are read elsewhere).
/// Open once, Read as often as needed (the live Sensors tab reads every second), then Dispose. Never throws: problems
/// end up in <see cref="Problem"/> and Read returns nothing.
/// </summary>
public sealed class SensorSession : IDisposable
{
    private object? _computer;  // a LibreHardwareMonitor Computer, kept untyped so this class loads without the library

    public string? Problem { get; private set; }

    public static SensorSession Open()
    {
        LhmLoader.Register();
        var s = new SensorSession();
        if (LhmLoader.Folder() is null) { s.Problem = "the sensor library isn't available for this processor type"; return s; }
        try { s._computer = LhmReader.Open(); }
        catch (FileNotFoundException e) { s.Problem = $"a sensor library file is missing next to the app ({e.FileName ?? "lhm folder"}); extract the whole zip, not just the exe"; }
        catch (Exception e) when (e is FileLoadException or BadImageFormatException or TypeLoadException)
        { s.Problem = $"Windows wouldn't load the sensor library: {e.Message}"; }
        catch (Exception e) { s.Problem = $"the sensor library failed to start: {e.GetType().Name}: {e.Message}"; }
        return s;
    }

    public List<SensorReading> Read()
    {
        if (_computer is null) return [];
        try { return LhmReader.Read(_computer); }
        catch (Exception e) { Problem = $"reading sensors failed: {e.Message}"; return []; }
    }

    public void Dispose()
    {
        if (_computer is null) return;
        try { LhmReader.Close(_computer); } catch (Exception) { }
        _computer = null;
    }
}

/// <summary>The only class that touches LibreHardwareMonitor types (so a missing library fails here, caught by SensorSession).</summary>
internal static class LhmReader
{
    private static readonly HashSet<SensorType> Wanted =
        [SensorType.Temperature, SensorType.Fan, SensorType.Load, SensorType.Clock, SensorType.Power, SensorType.Voltage, SensorType.Control, SensorType.Data];

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object Open()
    {
        var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true };
        c.Open();
        return c;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static List<SensorReading> Read(object computer)
    {
        var list = new List<SensorReading>();
        foreach (var hw in ((Computer)computer).Hardware)
        {
            hw.Update();
            var kind = Kind(hw.HardwareType);
            Add(list, hw, kind);
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                Add(list, sub, kind);
            }
        }
        return list;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Close(object computer) => ((Computer)computer).Close();

    private static void Add(List<SensorReading> list, IHardware hw, string kind)
    {
        foreach (var s in hw.Sensors)
        {
            if (!Wanted.Contains(s.SensorType) || s.Value is not { } v || float.IsNaN(v) || float.IsInfinity(v)) continue;
            // Without the driver, power and clock sensors exist but read 0: leave them out rather than show wrong numbers.
            if (v == 0 && s.SensorType is SensorType.Power or SensorType.Clock) continue;
            // DIMM sensors live under the memory hardware; anything named DIMM counts as memory even under the motherboard.
            var k = hw.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase) ? "Memory" : kind;
            list.Add(new SensorReading { Hardware = hw.Name, Kind = k, Name = s.Name, Type = s.SensorType.ToString(), Value = v, Min = s.Min, Max = s.Max });
        }
    }

    private static string Kind(HardwareType t) => t switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "Memory",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Motherboard",
        _ => "Other",
    };
}
