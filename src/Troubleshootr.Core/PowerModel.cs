namespace Troubleshootr.Core;

/// <summary>Batteries (straight from the battery driver), charging, sleep and wake history, and Modern Standby drain.</summary>
public sealed class PowerInfo
{
    public List<BatteryPack> Batteries { get; set; } = new();
    public string? BatteriesUnavailable { get; set; }
    public bool? OnAc { get; set; }
    /// <summary>Overall charge, % (GetSystemPowerStatus).</summary>
    public int? ChargePercent { get; set; }

    /// <summary>Wakes from sleep or hibernate in the last <see cref="Days"/> days (Power-Troubleshooter 1), newest first.</summary>
    public List<WakeEvent> Wakes { get; set; } = new();
    /// <summary>Modern Standby sessions on battery lasting 30 min or more (Kernel-Power 507), newest first.</summary>
    public List<StandbySession> Standby { get; set; } = new();
    public int Days { get; set; } = 14;

    /// <summary>What's keeping the PC or screen awake right now (powercfg /requests; needs admin).</summary>
    public List<PowerRequest> Requests { get; set; } = new();
    public string? RequestsUnavailable { get; set; }
}

public sealed class BatteryPack
{
    public string? Name { get; set; }
    public string? Manufacturer { get; set; }
    /// <summary>"LION", "LiP", "NiMH"… as the battery reports it.</summary>
    public string? Chemistry { get; set; }
    public long DesignMwh { get; set; }
    public long FullMwh { get; set; }
    public long? RemainingMwh { get; set; }
    /// <summary>Charge cycles; null when the battery doesn't count them (many report 0).</summary>
    public int? CycleCount { get; set; }
    public DateTime? Manufactured { get; set; }
    public int? VoltageMv { get; set; }
    /// <summary>Charge (+) or discharge (−) rate, mW; null when unknown.</summary>
    public int? RateMw { get; set; }
    public bool PowerOnline { get; set; }
    public bool Charging { get; set; }
    public bool Discharging { get; set; }

    /// <summary>Full-charge capacity as a share of design capacity, % (capped at 100); null when unknown.</summary>
    public double? HealthPercent => DesignMwh > 0 && FullMwh > 0 ? Math.Min(100, FullMwh * 100.0 / DesignMwh) : null;
    public double? ChargePercent => FullMwh > 0 && RemainingMwh is { } r ? Math.Min(100, r * 100.0 / FullMwh) : null;
}

public sealed class WakeEvent
{
    public DateTime TimeUtc { get; set; }
    public double SleepMinutes { get; set; }
    /// <summary>"Power Button", "Device -USB Input Device", "Timer - …", or null when Windows didn't know.</summary>
    [FreeText] public string? Source { get; set; }
    /// <summary>The program that set a wake timer, when a timer woke it.</summary>
    [FreeText] public string? TimerOwner { get; set; }

    /// <summary>The timer owner without its device path: "svchost.exe (SystemEventsBroker)".</summary>
    public string? Owner
    {
        get
        {
            if (TimerOwner is not { } o || !o.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return TimerOwner;
            // "<path>.exe (context)": the context can hold backslashes and the path can hold "(x86)", so split at the file extension.
            var ext = System.Text.RegularExpressions.Regex.Match(o, @"\.(exe|dll|sys)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!ext.Success) return o.Substring(o.LastIndexOf('\\') + 1);
            var path = o.Substring(0, ext.Index + ext.Length);
            return path.Substring(path.LastIndexOf('\\') + 1) + o.Substring(path.Length);
        }
    }

    public bool IsTimer => !string.IsNullOrEmpty(TimerOwner) || (Source?.StartsWith("Timer", StringComparison.OrdinalIgnoreCase) ?? false);
    public bool IsUser => Source is { } src && (src.IndexOf("Power Button", StringComparison.OrdinalIgnoreCase) >= 0 || src.IndexOf("Lid", StringComparison.OrdinalIgnoreCase) >= 0);
}

public sealed class StandbySession
{
    public DateTime EndUtc { get; set; }
    public double Minutes { get; set; }
    /// <summary>Energy used during the session, mWh.</summary>
    public long DrainMwh { get; set; }
    public long FullMwh { get; set; }
    /// <summary>Share of the time spent in the deepest low-power state (hardware DRIPS), %.</summary>
    public double DeepSleepPercent { get; set; }

    /// <summary>Battery lost per hour, % of a full charge.</summary>
    public double PercentPerHour => FullMwh > 0 && Minutes > 0 ? DrainMwh * 100.0 / FullMwh / (Minutes / 60) : 0;
}

public sealed class PowerRequest
{
    /// <summary>"DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION"… (DISPLAY, SYSTEM and AWAYMODE stop sleep or the screen turning off).</summary>
    public string Kind { get; set; } = "";
    /// <summary>"PROCESS", "DRIVER", "SERVICE".</summary>
    public string Type { get; set; } = "";
    [FreeText] public string Name { get; set; } = "";
    [FreeText] public string? Reason { get; set; }

    public bool BlocksSleep => Kind is "SYSTEM" or "AWAYMODE";
}
