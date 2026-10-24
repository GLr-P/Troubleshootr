using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Storage;

/// <summary>Pure byte parsers for SMART and NVMe health data, unit-tested from saved bytes.</summary>
public static class HealthParsers
{
    /// <summary>
    /// ATA SMART data page as returned by MSStorageDriver_FailurePredictData.VendorSpecific (or SMART READ DATA):
    /// 2-byte revision, then 30 attributes of 12 bytes: id, flags(2), current, worst, raw(6), reserved.
    /// Thresholds page has the same layout with the threshold in byte 1 of each entry.
    /// </summary>
    public static List<SmartAttribute> ParseAtaSmart(byte[] data, byte[]? thresholds)
    {
        var list = new List<SmartAttribute>();
        for (var i = 0; i < 30; i++)
        {
            var o = 2 + i * 12;
            if (o + 12 > data.Length) break;
            var id = data[o];
            if (id == 0) continue;
            var raw = new byte[6];
            Array.Copy(data, o + 5, raw, 0, 6);
            var threshold = 0;
            if (thresholds is not null && o + 2 <= thresholds.Length && thresholds[o] == id) threshold = thresholds[o + 1];
            list.Add(new SmartAttribute
            {
                Id = id,
                Name = SmartNames.TryGetValue(id, out var n) ? n : $"Vendor specific {id:X2}",
                Current = data[o + 3],
                Worst = data[o + 4],
                Threshold = threshold,
                Raw = DecodeRaw(id, raw),
                RawHex = BitConverter.ToString(Enumerable.Reverse(raw).ToArray()).Replace("-", ""),
            });
        }
        return list;
    }

    /// <summary>Raw values are 6 little-endian bytes, but several attributes pack extra data in the high bytes.</summary>
    private static long DecodeRaw(int id, byte[] raw)
    {
        long Le(int count) { long v = 0; for (var i = count - 1; i >= 0; i--) v = (v << 8) | raw[i]; return v; }
        return id switch
        {
            0xC2 or 0xBE => raw[0],          // temperature: low byte; others hold min/max
            0x09 => Le(4),                   // power-on hours: some vendors store minutes/seconds in bytes 4-5
            0x05 or 0xC5 or 0xC6 or 0xC4 or 0xBB or 0xBC or 0xB8 => Le(4) & 0xFFFF_FFFF,
            _ => Le(6),
        };
    }

    /// <summary>NVMe SMART / Health Information log page (512 bytes).</summary>
    public static NvmeHealth ParseNvmeHealth(byte[] log)
    {
        if (log.Length < 512) throw new ArgumentException($"NVMe health log is {log.Length} bytes, expected 512");
        // 128-bit counters; the low 64 bits are plenty (and the high half is zero on any real drive).
        long U64(int o) => (long)Math.Min(BitConverter.ToUInt64(log, o), long.MaxValue);
        var kelvin = BitConverter.ToUInt16(log, 1);
        return new NvmeHealth
        {
            CriticalWarning = log[0],
            TemperatureC = kelvin > 0 ? kelvin - 273 : 0,
            AvailableSparePercent = log[3],
            SpareThresholdPercent = log[4],
            PercentageUsed = log[5],
            DataUnitsRead = U64(32),
            DataUnitsWritten = U64(48),
            PowerCycles = U64(112),
            PowerOnHours = U64(128),
            UnsafeShutdowns = U64(144),
            MediaErrors = U64(160),
            ErrorLogEntries = U64(176),
            WarningTempMinutes = BitConverter.ToUInt32(log, 192),
            CriticalTempMinutes = BitConverter.ToUInt32(log, 196),
        };
    }

    public static readonly IReadOnlyDictionary<int, string> SmartNames = new Dictionary<int, string>
    {
        [0x01] = "Read error rate", [0x02] = "Throughput performance", [0x03] = "Spin-up time", [0x04] = "Start/stop count",
        [0x05] = "Reallocated sectors", [0x07] = "Seek error rate", [0x08] = "Seek time performance", [0x09] = "Power-on hours",
        [0x0A] = "Spin retry count", [0x0B] = "Calibration retry count", [0x0C] = "Power cycle count",
        [0xA9] = "Remaining life", [0xAA] = "Available reserved space", [0xAB] = "Program fail count", [0xAC] = "Erase fail count",
        [0xAD] = "Wear leveling count", [0xAE] = "Unexpected power loss", [0xB1] = "Wear range delta / wear leveling", [0xB3] = "Used reserved blocks",
        [0xB4] = "Unused reserved blocks", [0xB5] = "Program fail count", [0xB6] = "Erase fail count", [0xB7] = "SATA downshift errors",
        [0xB8] = "End-to-end errors", [0xBB] = "Reported uncorrectable errors", [0xBC] = "Command timeouts", [0xBD] = "High fly writes",
        [0xBE] = "Airflow temperature", [0xBF] = "G-sense error rate", [0xC0] = "Power-off retract count", [0xC1] = "Load cycle count",
        [0xC2] = "Temperature", [0xC3] = "Hardware ECC recovered", [0xC4] = "Reallocation events", [0xC5] = "Pending sectors",
        [0xC6] = "Uncorrectable sectors", [0xC7] = "UltraDMA CRC errors", [0xC8] = "Write error rate", [0xCA] = "Percent lifetime remaining",
        [0xCE] = "Flying height", [0xD2] = "Successful RAIN recovery", [0xE7] = "SSD life left", [0xE8] = "Available reserved space",
        [0xE9] = "Media wearout indicator", [0xEA] = "Average / max erase count", [0xF1] = "Total host writes", [0xF2] = "Total host reads",
        [0xF9] = "NAND writes",
    };
}
