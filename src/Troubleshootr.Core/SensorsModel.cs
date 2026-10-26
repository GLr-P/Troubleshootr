namespace Troubleshootr.Core;

/// <summary>
/// Temperatures, fans, loads and clocks at scan time (LibreHardwareMonitor), plus Windows' ACPI thermal zones as a
/// driver-free fallback. CPU and RAM temperatures need the PawnIO driver and admin; GPU sensors work without either.
/// </summary>
public sealed class SensorsInfo
{
    /// <summary>The PawnIO driver is installed (needed for CPU, RAM and motherboard sensors).</summary>
    public bool DriverInstalled { get; set; }
    /// <summary>Why the sensor library couldn't run at all (ARM64, missing files); readings are then only the thermal zones.</summary>
    public string? LibraryProblem { get; set; }
    public List<SensorReading> Readings { get; set; } = new();
    /// <summary>ACPI thermal zones (PDH "Thermal Zone Information"), °C. Often a motherboard or chipset sensor, sometimes fixed.</summary>
    public List<SensorReading> ThermalZones { get; set; } = new();

    public IEnumerable<SensorReading> Temps(string kind) => Readings.Where(r => r.Kind == kind && r.Type == "Temperature");

    /// <summary>The CPU's main temperature: package / Tctl-Tdie / hottest core, whichever is highest. Null without the driver.</summary>
    public SensorReading? CpuTemp => Temps("CPU").Where(r => r.Name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) < 0)
        .Where(r => r.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 || r.Name.StartsWith("Core (Tctl", StringComparison.OrdinalIgnoreCase)
                    || r.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase) || r.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(r => r.Value).FirstOrDefault();

    /// <summary>Intel's "Distance to TjMax": degrees left before the CPU throttles itself. Smallest across cores.</summary>
    public double? CpuHeadroom => Temps("CPU").Where(r => r.Name.IndexOf("Distance to TjMax", StringComparison.OrdinalIgnoreCase) >= 0).Select(r => (double?)r.Value).Min();
}

public sealed class SensorReading
{
    /// <summary>The chip ("Intel Core Ultra 7 155H", "NVIDIA GeForce RTX 4070", "DIMM #1").</summary>
    public string Hardware { get; set; } = "";
    /// <summary>"CPU", "GPU", "Memory", "Motherboard", "Other".</summary>
    public string Kind { get; set; } = "Other";
    public string Name { get; set; } = "";
    /// <summary>LibreHardwareMonitor's SensorType name: Temperature, Load, Clock, Fan, Power, Voltage, Control, Data...</summary>
    public string Type { get; set; } = "";
    public double Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }

    public string Unit => Type switch
    {
        "Temperature" => "°C", "Load" or "Control" or "Level" => "%", "Clock" or "Frequency" => "MHz", "Fan" => "RPM", "Power" => "W",
        "Voltage" => "V", "Current" => "A", "Data" => "GB", "SmallData" => "MB", "Throughput" => "B/s", _ => "",
    };

    public string Format(double v) => Type switch
    {
        "Voltage" => $"{v:0.000} {Unit}",
        "Power" or "Current" => $"{v:0.0} {Unit}",
        "Data" or "SmallData" => $"{v:0.#} {Unit}",
        _ => $"{v:0} {Unit}",
    };
}
