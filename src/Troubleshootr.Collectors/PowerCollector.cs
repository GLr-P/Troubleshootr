using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Troubleshootr.Collectors.Events;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>
/// Batteries through the battery driver's IOCTLs (the data behind powercfg /batteryreport, no admin needed), the power
/// source, wakes from sleep, Modern Standby drain from Kernel-Power 507, and (as admin) what's keeping the PC awake.
/// </summary>
public sealed class PowerCollector : Collector<PowerInfo>
{
    private static Guid BatteryInterface = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint QueryTag = 0x294040, QueryInformation = 0x294044, QueryStatus = 0x29404C;
    private const int InfoBattery = 0, InfoDeviceName = 4, InfoManufactureDate = 5, InfoManufacturer = 6;
    private const uint Unknown = 0xFFFFFFFF;
    private const int Days = 14;
    private const double MinStandbyMinutes = 30;
    private static readonly TimeSpan RequestsBudget = TimeSpan.FromSeconds(4);
    private const uint SystemBattery = 0x80000000, RelativeCapacity = 0x40000000; // BATTERY_INFORMATION.Capabilities

    public override string Section => nameof(Snapshot.Power);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(7);

    protected override PowerInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new PowerInfo { Days = Days };
        if (PerfApi.GetSystemPowerStatus(out var status))
        {
            info.OnAc = status.ACLineStatus switch { 1 => true, 0 => false, _ => null };
            if (status.BatteryLifePercent <= 100) info.ChargePercent = status.BatteryLifePercent;
        }
        // Everything runs side by side so one slow part (powercfg, a busy event log) can't time out the batteries.
        var requests = ctx.IsAdmin ? Task.Run(() => Requests(RequestsBudget), ct) : null;
        var wakes = Task.Run(() => Wakes(ct), ct);
        var standby = Task.Run(() => Standby(ct), ct);
        try { info.Batteries = Batteries(); }
        catch (Exception e) when (e is not OperationCanceledException) { info.BatteriesUnavailable = e.Message; }
        info.Wakes = wakes.Result;
        info.Standby = standby.Result;

        if (requests is null) info.RequestsUnavailable = "needs admin";
        else
        {
            try { info.Requests = requests.Result; }
            catch (AggregateException e) { info.RequestsUnavailable = e.InnerException?.Message ?? e.Message; }
        }
        return info;
    }

    private static List<BatteryPack> Batteries()
    {
        var list = new List<BatteryPack>();
        var set = SetupApi.SetupDiGetClassDevsW(ref BatteryInterface, null, IntPtr.Zero, SetupApi.DigcfPresent | SetupApi.DigcfDeviceInterface);
        if (set == SetupApi.Invalid) throw new CollectorException($"batteries: SetupAPI error {Marshal.GetLastWin32Error()}");
        try
        {
            var iface = new SetupApi.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; i < 8 && SetupApi.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref BatteryInterface, i, ref iface); i++)
            {
                var data = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
                if (SetupApi.InterfacePath(set, ref iface, ref data) is not { } path) continue;
                using var h = DeviceIo.Open(path, readWrite: true) ?? DeviceIo.OpenRead(path);
                if (h is null) continue;
                // The tag identifies the battery currently in the slot; 0 means the slot is empty.
                if (DeviceIo.Ioctl(h, QueryTag, BitConverter.GetBytes(0), 4, out _) is not { Length: 4 } tagBytes) continue;
                var tag = BitConverter.ToUInt32(tagBytes, 0);
                if (tag == 0) continue;
                if (Query(h, tag, InfoBattery, 64) is not { } raw || ParseInformation(raw) is not { } pack) continue;
                pack.Name = Text(Query(h, tag, InfoDeviceName, 256));
                pack.Manufacturer = Text(Query(h, tag, InfoManufacturer, 256));
                if (Query(h, tag, InfoManufactureDate, 4) is { Length: 4 } d && ParseDate(d) is { } made) pack.Manufactured = made;
                var wait = new byte[20];
                BitConverter.GetBytes(tag).CopyTo(wait, 0);
                if (DeviceIo.Ioctl(h, QueryStatus, wait, 16, out _) is { Length: 16 } st) ApplyStatus(pack, st);
                list.Add(pack);
            }
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private static byte[]? Query(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint tag, int level, int size)
    {
        var q = new byte[12]; // BATTERY_QUERY_INFORMATION: tag, level, AtRate
        BitConverter.GetBytes(tag).CopyTo(q, 0);
        BitConverter.GetBytes(level).CopyTo(q, 4);
        return DeviceIo.Ioctl(h, QueryInformation, q, size, out _);
    }

    /// <summary>BATTERY_INFORMATION: capabilities, technology, chemistry[4] at 8, design 12, full charge 16, cycle count 32.</summary>
    public static BatteryPack? ParseInformation(byte[] b)
    {
        if (b.Length < 36) return null;
        var capabilities = BitConverter.ToUInt32(b, 0);
        if ((capabilities & SystemBattery) == 0) return null; // a UPS or a device's own battery, not the one powering this PC
        if ((capabilities & RelativeCapacity) != 0) return new BatteryPack(); // capacities are percentages, not mWh: wear is unknown
        var design = BitConverter.ToUInt32(b, 12);
        var full = BitConverter.ToUInt32(b, 16);
        var cycles = BitConverter.ToUInt32(b, 32);
        var chemistry = Encoding.ASCII.GetString(b, 8, 4).TrimEnd('\0', ' ');
        return new BatteryPack
        {
            DesignMwh = design is Unknown ? 0 : design,
            FullMwh = full is Unknown ? 0 : full,
            CycleCount = cycles is 0 or Unknown ? null : (int)cycles,
            Chemistry = chemistry.Length > 0 ? chemistry : null,
        };
    }

    /// <summary>BATTERY_STATUS: power state flags (1 on mains, 2 discharging, 4 charging), capacity mWh, voltage mV, rate mW.</summary>
    public static void ApplyStatus(BatteryPack p, byte[] b)
    {
        var state = BitConverter.ToUInt32(b, 0);
        var capacity = BitConverter.ToUInt32(b, 4);
        var voltage = BitConverter.ToUInt32(b, 8);
        var rate = BitConverter.ToInt32(b, 12);
        p.PowerOnline = (state & 1) != 0;
        p.Discharging = (state & 2) != 0;
        p.Charging = (state & 4) != 0;
        if (capacity != Unknown) p.RemainingMwh = capacity;
        if (voltage != Unknown) p.VoltageMv = (int)voltage;
        if (rate != unchecked((int)0x80000000) && rate != -1) p.RateMw = rate; // BATTERY_UNKNOWN_RATE
    }

    /// <summary>BATTERY_MANUFACTURE_DATE: day, month, year (ushort).</summary>
    public static DateTime? ParseDate(byte[] b)
    {
        int day = b[0], month = b[1], year = BitConverter.ToUInt16(b, 2);
        return year is >= 1995 and <= 2100 && month is >= 1 and <= 12 && day is >= 1 and <= 31 && day <= DateTime.DaysInMonth(year, month)
            ? new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc) : null;
    }

    private static string? Text(byte[]? b)
    {
        if (b is null || b.Length < 2) return null;
        var s = Encoding.Unicode.GetString(b);
        var end = s.IndexOf('\0');
        s = (end >= 0 ? s.Substring(0, end) : s).Trim();
        return s.Length == 0 ? null : s;
    }

    private static List<WakeEvent> Wakes(CancellationToken ct) =>
        Read("Microsoft-Windows-Power-Troubleshooter", [1], 200, ct, (e, d) => new WakeEvent
        {
            TimeUtc = e.TimeCreated?.ToUniversalTime() ?? default,
            SleepMinutes = Date(d, "WakeTime") is { } wake && Date(d, "SleepTime") is { } sleep ? Math.Max(0, (wake - sleep).TotalMinutes) : 0,
            Source = d.TryGetValue("WakeSourceText", out var src) && src.Length > 0 ? src : WakeSourceFromMessage(e),
            TimerOwner = d.TryGetValue("WakeTimerOwner", out var owner) && owner.Length > 0 ? owner : null,
        });

    private static string? WakeSourceFromMessage(EventRecord e)
    {
        string? text;
        try { text = e.FormatDescription(); }
        catch (EventLogException) { return null; }
        var line = text?.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("Wake Source:", StringComparison.OrdinalIgnoreCase));
        var value = line?.Substring(line.IndexOf(':') + 1).Trim();
        return value is { Length: > 0 } && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    // Only sessions on battery say anything about drain (on mains the battery just tops up), so the query asks for those alone.
    private static List<StandbySession> Standby(CancellationToken ct) =>
        Read("Microsoft-Windows-Kernel-Power", [507], 400, ct, (e, d) =>
        {
            if (Str(d, "PowerStateAc") != "false") return null;
            var us = Long(d, "DurationInUs");
            if (us is not > 0 || us.Value / 6e7 < MinStandbyMinutes) return null;
            return new StandbySession
            {
                EndUtc = e.TimeCreated?.ToUniversalTime() ?? default,
                Minutes = us.Value / 6e7,
                DrainMwh = Long(d, "EnergyDrain") ?? 0,
                FullMwh = Long(d, "BatteryFullChargeCapacityOnExit") ?? 0,
                DeepSleepPercent = Long(d, "HwDripsResidencyInUs") is { } hw ? Math.Round(hw * 100.0 / us.Value, 1) : 0,
            };
        });

    private static List<T> Read<T>(string provider, int[] ids, int max, CancellationToken ct, Func<EventRecord, Dictionary<string, string>, T?> make) where T : class
    {
        var list = new List<T>();
        var idFilter = string.Join(" or ", ids.Select(i => $"EventID={i}"));
        var onBatteryOnly = typeof(T) == typeof(StandbySession) ? " and *[EventData[Data[@Name='PowerStateAc']='false']]" : "";
        var xpath = $"*[System[Provider[@Name='{provider}'] and ({idFilter}) and TimeCreated[timediff(@SystemTime) <= {Days * 86400000L}]]]{onBatteryOnly}";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
            for (var n = 0; n < max; n++)
            {
                ct.ThrowIfCancellationRequested();
                using var e = reader.ReadEvent();
                if (e is null) break;
                if (make(e, EventsCollector.Data(e)) is { } item) list.Add(item);
            }
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException) { }
        return list;
    }

    private static string? Str(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v.Trim().ToLowerInvariant() : null;
    private static long? Long(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
    private static DateTime? Date(Dictionary<string, string> d, string k) =>
        d.TryGetValue(k, out var v) && DateTime.TryParse(v.Trim('‎', ' '), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static List<PowerRequest> Requests(TimeSpan budget)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/requests")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.Default,
        };
        using var p = Process.Start(psi) ?? throw new CollectorException("powercfg didn't start");
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit((int)budget.TotalMilliseconds))
        {
            try { p.Kill(); } catch (InvalidOperationException) { } // already gone
            throw new CollectorException($"powercfg didn't answer within {budget.TotalSeconds:0} s");
        }
        return ParseRequests(output.Wait(1000) ? output.Result : "");
    }

    /// <summary>powercfg /requests: "SYSTEM:" headers, then "[PROCESS] path" lines each followed by an optional reason line, or "None.".</summary>
    public static List<PowerRequest> ParseRequests(string text)
    {
        var list = new List<PowerRequest>();
        string? kind = null;
        PowerRequest? last = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { last = null; continue; }
            if (line.EndsWith(":") && line.IndexOf(' ') < 0 && line.ToUpperInvariant() == line) { kind = line.TrimEnd(':'); last = null; continue; }
            if (kind is null) continue;
            if (line.StartsWith("[") && line.IndexOf(']') is var close and > 1)
            {
                last = new PowerRequest { Kind = kind, Type = line.Substring(1, close - 1), Name = Short(line.Substring(close + 1).Trim()) };
                list.Add(last);
            }
            else if (last is not null && last.Reason is null) last.Reason = line;
        }
        return list;
    }

    /// <summary>"\Device\HarddiskVolume3\Program Files\Spotify\Spotify.exe" → "Spotify.exe".</summary>
    private static string Short(string name) =>
        name.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(name) : name;
}
