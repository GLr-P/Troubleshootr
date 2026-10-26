namespace Troubleshootr.Core;

/// <summary>Everything that starts with Windows: Run keys, Startup folders and logon/boot scheduled tasks.</summary>
public sealed class StartupInfo
{
    public List<StartupEntry> Entries { get; set; } = new();
}

public sealed class StartupEntry
{
    [FreeText] public string Name { get; set; } = "";
    /// <summary>"HKCU Run", "HKLM Run", "HKLM Run (32-bit)", "Startup folder", "All users Startup folder", "Scheduled task".</summary>
    public string Location { get; set; } = "";
    [FreeText] public string? Command { get; set; }
    /// <summary>False when switched off in Task Manager's Startup tab (StartupApproved) or a disabled task.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The file the command runs (shortcuts resolved).</summary>
    [FreeText] public string? File { get; set; }
    /// <summary>Valid Authenticode signature; false = unsigned or invalid; null = not checked (file missing, or a Windows file signed by catalog).</summary>
    public bool? Signed { get; set; }
    public string? Publisher { get; set; }

    /// <summary>
    /// Windows' own files: System32, SysWOW64, WinSxS, SystemApps or the Windows folder itself. They're catalog-signed, so
    /// a file signature check would call them unsigned; they aren't checked. C:\Windows\Temp is not one of them.
    /// </summary>
    public static bool IsWindowsFile(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Z]:\\Windows\\((System32|SysWOW64|WinSxS|SystemApps)\\|[^\\]+$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Scheduled tasks (mostly updaters) aren't in Task Manager's Startup apps list, so they're counted separately.</summary>
    public bool IsTask => Location == "Scheduled task";
}

/// <summary>Installed programs (registry Uninstall keys; never Win32_Product, which triggers MSI repairs) and browsers.</summary>
public sealed class SoftwareInfo
{
    public List<InstalledProgram> Programs { get; set; } = new();
    /// <summary>The default browser's ProgId (ChromeHTML, MSEdgeHTM, FirefoxURL-…).</summary>
    public string? DefaultBrowser { get; set; }
    /// <summary>Extension count per Chromium browser profile ("Chrome: Default" → 12).</summary>
    public Dictionary<string, int> BrowserExtensions { get; set; } = new();
}

public sealed class InstalledProgram
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public DateTime? InstallDate { get; set; }
    public long? SizeKB { get; set; }
    /// <summary>"Machine", "Machine (32-bit)" or "User".</summary>
    public string? Scope { get; set; }
}

/// <summary>A short sample (a few seconds) of what the PC is doing: CPU, disk and the busiest processes.</summary>
public sealed class PerformanceInfo
{
    public double SampleSeconds { get; set; }
    /// <summary>Total CPU use during the sample, minus Troubleshootr's own work.</summary>
    public double CpuPercent { get; set; }
    /// <summary>Busiest disk's active time, from PDH "% Idle Time". Null if the counters are unavailable.</summary>
    public double? DiskActivePercent { get; set; }
    /// <summary>Processor speed as a % of its base speed ("% Processor Performance"): over 100 is boost, far under 100 under load is throttling.</summary>
    public double? ProcessorPerformancePercent { get; set; }
    /// <summary>The busiest processes by CPU, memory or disk (Troubleshootr itself excluded).</summary>
    public List<ProcessSample> Processes { get; set; } = new();
    public int ProcessCount { get; set; }
}

public sealed class ProcessSample
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    /// <summary>Share of all cores, 0–100.</summary>
    public double CpuPercent { get; set; }
    public long PrivateMB { get; set; }
    public long WorkingSetMB { get; set; }
    public long DiskBytesPerSec { get; set; }
    [FreeText] public string? Path { get; set; }
}

/// <summary>Settings that make a PC slow or fragile: power plan, page file, visual effects, and space that could be freed.</summary>
public sealed class TuningInfo
{
    public string? PowerPlan { get; set; }
    public string? PowerPlanGuid { get; set; }
    /// <summary>Maximum processor state on mains and battery, %.</summary>
    public int? MaxCpuOnAc { get; set; }
    public int? MaxCpuOnBattery { get; set; }
    public bool? OnAcPower { get; set; }
    public bool? BatterySaverOn { get; set; }

    /// <summary>The PagingFiles registry value: "?:\pagefile.sys" means system-managed on every drive; empty means no page file.</summary>
    public List<string> PageFileSettings { get; set; } = new();
    public List<PageFileUse> PageFiles { get; set; } = new();

    /// <summary>0 let Windows choose, 1 best appearance, 2 best performance, 3 custom.</summary>
    public int? VisualEffects { get; set; }
    public bool? Transparency { get; set; }
    public bool? GameMode { get; set; }
    /// <summary>Hardware-accelerated GPU scheduling.</summary>
    public bool? GpuScheduling { get; set; }

    public List<CleanupItem> Cleanup { get; set; } = new();

    public const string PowerSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
}

public sealed class PageFileUse
{
    public string Path { get; set; } = "";
    public long AllocatedMB { get; set; }
    public long CurrentMB { get; set; }
    public long PeakMB { get; set; }
}

public sealed class CleanupItem
{
    public string Name { get; set; } = "";
    [FreeText] public string Path { get; set; } = "";
    public long Bytes { get; set; }
    /// <summary>Counting stopped at the time limit, so the real size is at least this.</summary>
    public bool AtLeast { get; set; }
}
