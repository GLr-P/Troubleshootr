using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Sensors;

/// <summary>One reading of every sensor at scan time, plus the ACPI thermal zones (which need neither the driver nor admin).</summary>
public sealed class SensorsCollector : Collector<SensorsInfo>
{
    public override string Section => nameof(Snapshot.Sensors);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(8);

    protected override SensorsInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new SensorsInfo { DriverInstalled = PawnIo.Installed, ThermalZones = ThermalZones() };
        using var session = SensorSession.Open();
        info.Readings = session.Read();
        info.LibraryProblem = session.Problem;
        return info;
    }

    /// <summary>PDH "\Thermal Zone Information(*)\Temperature" is in kelvin.</summary>
    public static List<SensorReading> ThermalZones()
    {
        var list = new List<SensorReading>();
        try
        {
            if (PerfApi.PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return list;
            try
            {
                if (PerfApi.PdhAddEnglishCounterW(query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out var counter) != 0) return list;
                if (PerfApi.PdhCollectQueryData(query) != 0) return list;
                foreach (var kv in PerfApi.Values(counter))
                {
                    var c = kv.Value - 273.15;
                    if (c is > 0 and < 150) list.Add(new SensorReading { Hardware = "ACPI thermal zone", Kind = "Motherboard", Name = kv.Key.TrimStart('\\'), Type = "Temperature", Value = Math.Round(c, 1) });
                }
            }
            finally { PerfApi.PdhCloseQuery(query); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return list;
    }
}
