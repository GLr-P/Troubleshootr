using Troubleshootr.Checks.Hardware;
using Troubleshootr.Collectors.Sensors;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 9: sensor readings, temperature checks and the stress-test verdict.</summary>
public class SensorTests
{
    private static SensorReading T(string kind, string hw, string name, double v) => new() { Kind = kind, Hardware = hw, Name = name, Type = "Temperature", Value = v };

    /// <summary>Real names from the owner's Core Ultra 7 155H with PawnIO, as admin.</summary>
    private static Snapshot WithCpu(double package, double headroom) => Snap.Healthy().With(s => s.Sensors = Section<SensorsInfo>.Of(new SensorsInfo
    {
        DriverInstalled = true,
        Readings =
        [
            T("CPU", "Intel Core Ultra 7 155H", "P-Core #1", package - 4), T("CPU", "Intel Core Ultra 7 155H", "Core Max", package - 1),
            T("CPU", "Intel Core Ultra 7 155H", "CPU Package", package), T("CPU", "Intel Core Ultra 7 155H", "P-Core #1 Distance to TjMax", headroom),
            T("CPU", "Intel Core Ultra 7 155H", "E-Core #9 Distance to TjMax", headroom + 5),
        ],
    }));

    [Fact]
    public void CpuTempAndHeadroom()
    {
        var x = WithCpu(45, 65).Sensors!.Value!;
        Assert.Equal("CPU Package", x.CpuTemp!.Name);   // never a "Distance to TjMax" sensor, even though 65 > 45
        Assert.Equal(65, x.CpuHeadroom);
        Assert.Null(new SensorsInfo().CpuTemp);
        // AMD: Tctl/Tdie is the main one.
        Assert.Equal("Core (Tctl/Tdie)", new SensorsInfo { Readings = [T("CPU", "Ryzen 7 7800X3D", "Core (Tctl/Tdie)", 71), T("CPU", "Ryzen 7 7800X3D", "CCD1 (Tdie)", 69)] }.CpuTemp!.Name);
    }

    [Fact]
    public void Cpu004()
    {
        Assert.Empty(new Cpu004CpuTemperature().Run(WithCpu(45, 65)));
        Assert.Equal(Severity.Warning, new Cpu004CpuTemperature().Run(WithCpu(93, 17)).Single().Severity);
        Assert.Equal(Severity.Critical, new Cpu004CpuTemperature().Run(WithCpu(101, 9)).Single().Severity);
        Assert.Equal(Severity.Critical, new Cpu004CpuTemperature().Run(WithCpu(88, 0)).Single().Severity);  // at TjMax (a 90 °C-limit chip)
        Assert.Empty(new Cpu004CpuTemperature().Run(Snap.Healthy().With(s => s.Sensors = Section<SensorsInfo>.Of(new SensorsInfo()))));  // no driver: no reading, no finding
    }

    [Fact]
    public void Gpu008AndMem011()
    {
        Snapshot With(params SensorReading[] r) => Snap.Healthy().With(s => s.Sensors = Section<SensorsInfo>.Of(new SensorsInfo { Readings = [.. r] }));
        Assert.Empty(new Gpu008GpuTemperature().Run(With(T("GPU", "RTX 4070", "GPU Core", 72), T("GPU", "RTX 4070", "GPU Hot Spot", 88))));
        Assert.Contains("GPU Core 88 °C", new Gpu008GpuTemperature().Run(With(T("GPU", "RTX 4070", "GPU Core", 88))).Single().Evidence);
        Assert.Contains("hot spot 109 °C", new Gpu008GpuTemperature().Run(With(T("GPU", "RX 7800 XT", "GPU Core", 80), T("GPU", "RX 7800 XT", "GPU Hot Spot", 109))).Single().Evidence);
        Assert.Empty(new Gpu008GpuTemperature().Run(With(T("GPU", "RTX 3090", "GPU Memory Junction", 98), T("GPU", "RTX 3090", "GPU Temperature", 70))));  // junction isn't core
        Assert.Contains("DIMM #2 87 °C", new Mem011DimmTemperature().Run(With(T("Memory", "DIMM #1", "Temperature", 70), T("Memory", "DIMM #2", "Temperature", 87))).Single().Evidence);
    }

    [Fact]
    public void LoadTestVerdict()
    {
        List<LoadTest.Sample> Run(Func<int, double> speed, double? temp = null) => Enumerable.Range(1, 30).Select(i => new LoadTest.Sample(i, speed(i), temp)).ToList();
        var steady = LoadTest.Judge(Run(_ => 110, 78), 22);
        Assert.False(steady.Throttled);
        Assert.StartsWith("No throttling", steady.Summary);

        var slowed = LoadTest.Judge(Run(i => i <= 10 ? 110 : 60, 96), 9);
        Assert.True(slowed.Throttled);
        Assert.Contains("slowed down", slowed.Summary);

        var hot = LoadTest.Judge(Run(_ => 100, 99), 1);
        Assert.Contains("reached its temperature limit", hot.Summary);

        // Review fixes: boost settling to the sustained limit isn't throttling; AMD (no headroom sensor) is judged by temperature.
        Assert.False(LoadTest.Judge(Run(i => i <= 10 ? 200 : 130, 85), 15).Throttled);
        Assert.True(LoadTest.Judge(Run(_ => 105, 95), null).Throttled);
        Assert.False(LoadTest.Judge(Run(_ => 105, 82), null).Throttled);

        Assert.Contains("on battery", LoadTest.Judge(Run(_ => 57), null, onBattery: true).Summary);
        Assert.Contains("Temperatures need advanced sensors", LoadTest.Judge(Run(_ => 100), null).Summary);
    }

    [Fact]
    public void Formatting()
    {
        Assert.Equal("1.250 V", new SensorReading { Type = "Voltage" }.Format(1.25));
        Assert.Equal("1450 RPM", new SensorReading { Type = "Fan" }.Format(1450.4));
        Assert.Equal("6.5 W", new SensorReading { Type = "Power" }.Format(6.49));
    }
}
