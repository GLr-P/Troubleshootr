namespace Troubleshootr.Core;

/// <summary>The event-log entries the checks care about (filtered by provider and id), plus the recent error timeline.</summary>
public sealed class EventsInfo
{
    /// <summary>How far back the filtered queries go.</summary>
    public int Days { get; set; } = 30;
    /// <summary>Events matching the rules in the collector (BSODs, disk errors, WHEA, crashes…), newest first.</summary>
    public List<LogEvent> Events { get; set; } = new();
    /// <summary>Every critical and error event in System and Application for the last 7 days (capped), newest first.</summary>
    public List<LogEvent> Timeline { get; set; } = new();
    public int TimelineDays { get; set; } = 7;
    /// <summary>Logs that couldn't be read, and why ("Diagnostics-Performance: needs admin").</summary>
    [FreeText] public List<string> Unreadable { get; set; } = new();

    public IEnumerable<LogEvent> Find(string provider, params int[] ids) =>
        Events.Where(e => string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase) && (ids.Length == 0 || ids.Contains(e.Id)));
}

public sealed class LogEvent
{
    /// <summary>"System", "Application", or an operational log name.</summary>
    public string Log { get; set; } = "";
    public string Provider { get; set; } = "";
    public int Id { get; set; }
    /// <summary>1 Critical, 2 Error, 3 Warning, 4 Information (0 also means information).</summary>
    public int Level { get; set; }
    public DateTime TimeUtc { get; set; }
    /// <summary>The rendered message, shortened. May be null when the provider's message file is missing.</summary>
    [FreeText] public string? Message { get; set; }
    /// <summary>EventData values by name; unnamed values are "0", "1", ….</summary>
    public Dictionary<string, string> Data { get; set; } = new();

    public string? Get(string name) => Data.TryGetValue(name, out var v) ? v : null;
    public string LevelName => Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" };
}

/// <summary>Crash dumps, crash-dump settings and the Windows Error Reporting folders.</summary>
public sealed class CrashInfo
{
    public List<CrashDump> Dumps { get; set; } = new();
    /// <summary>Why the minidump files couldn't be read (they need admin), if so. The file list may still be there.</summary>
    public string? DumpsUnavailable { get; set; }
    /// <summary>CrashControl CrashDumpEnabled: 0 none, 1 complete, 2 kernel, 3 small, 7 automatic.</summary>
    public int? CrashDumpEnabled { get; set; }
    public bool? AutoReboot { get; set; }
    /// <summary>Windows Error Reporting reports from the last 30 days (archive and queue).</summary>
    public List<WerReport> Reports { get; set; } = new();
    public int QueuedReports { get; set; }
}

public sealed class CrashDump
{
    [FreeText] public string File { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public long SizeBytes { get; set; }
    public uint? BugCheck { get; set; }
    public List<ulong> Parameters { get; set; } = new();
    /// <summary>The driver the crash address points into (from the dump's driver list), if it could be worked out.</summary>
    public string? Driver { get; set; }
    public string? ParseError { get; set; }
}

public sealed class WerReport
{
    /// <summary>The folder name's first part: AppCrash, AppHang, Kernel, LiveKernelEvent, BEX64…</summary>
    public string Kind { get; set; } = "";
    /// <summary>The app (AppCrash_explorer.exe) or code (LiveKernelEvent_141).</summary>
    public string Subject { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public bool Queued { get; set; }
}

/// <summary>Windows itself: important services, system-file corruption, user profile and the reliability index.</summary>
public sealed class WindowsHealthInfo
{
    public List<ServiceState> Services { get; set; } = new();
    public CbsSummary? Cbs { get; set; }
    /// <summary>The current user is signed in with a temporary profile (their real profile failed to load).</summary>
    public bool TempProfile { get; set; }
    /// <summary>ProfileList keys ending in .bak: a profile that Windows had trouble loading.</summary>
    public int ProfileBackups { get; set; }
    /// <summary>Reliability Monitor's latest stability index, 1 (bad) to 10 (perfect).</summary>
    public double? StabilityIndex { get; set; }
    public double? StabilityLowest30Days { get; set; }
    /// <summary>Windows activation (SoftwareLicensingProduct.LicenseStatus): 1 licensed, 0 unlicensed, 2–6 grace or notification periods.</summary>
    public int? LicenseStatus { get; set; }
    public string? LicenseName { get; set; }
}

public sealed class ServiceState
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>Running, Stopped, StartPending… or null when the service doesn't exist.</summary>
    public string? Status { get; set; }
    /// <summary>Automatic, Manual, Disabled, Boot, System.</summary>
    public string? StartType { get; set; }
}

/// <summary>What the tail of CBS.log says about the last SFC/DISM runs.</summary>
public sealed class CbsSummary
{
    /// <summary>"[SR] Cannot repair member file" lines: SFC found damage it couldn't fix.</summary>
    public int CannotRepair { get; set; }
    /// <summary>"[SR] Repairing corrupted file" lines: SFC fixed something.</summary>
    public int Repaired { get; set; }
    /// <summary>DISM/CBS "corrupt" store lines (CSI Payload Corrupt, Corruption detected).</summary>
    public int StoreCorrupt { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public string? Unavailable { get; set; }
}

/// <summary>Reading app-crash events (Application Error 1000, Application Hang 1002, .NET Runtime 1026); shared by checks and the UI.</summary>
public static class AppCrashes
{
    private static readonly System.Text.RegularExpressions.Regex DotNetApp = new(@"Application:\s*(\S+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static IEnumerable<LogEvent> All(EventsInfo ev) =>
        ev.Find("Application Error", 1000).Concat(ev.Find("Application Hang", 1002)).Concat(ev.Find(".NET Runtime", 1026));

    /// <summary>The crashing app's file name.</summary>
    public static string? App(LogEvent e) =>
        (e.Get("AppName") ?? (e.Provider == ".NET Runtime" ? null : e.Get("0"))
         ?? (DotNetApp.Match(e.Get("0") ?? e.Message ?? "") is { Success: true } m ? m.Groups[1].Value : null))?.Trim();

    public static readonly string[] ShellApps =
        ["explorer.exe", "SearchIndexer.exe", "SearchHost.exe", "SearchUI.exe", "SearchApp.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe", "sihost.exe", "dwm.exe"];

    public static bool IsShell(string app) => ShellApps.Contains(app, StringComparer.OrdinalIgnoreCase);
}
