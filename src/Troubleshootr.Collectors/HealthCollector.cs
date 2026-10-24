using System.Globalization;
using System.ServiceProcess;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Windows health: the services everything depends on, SFC/DISM results in CBS.log, the user profile, Reliability Monitor.</summary>
public sealed class HealthCollector : Collector<WindowsHealthInfo>
{
    /// <summary>Services checked by WIN-010. Start types differ by edition, so the check only judges a few clear cases.</summary>
    internal static readonly string[] ServiceNames =
        ["wuauserv", "BITS", "Winmgmt", "EventLog", "CryptSvc", "Audiosrv", "AudioEndpointBuilder", "Dhcp", "Dnscache", "Spooler", "W32Time"];

    private const long CbsTailBytes = 4L * 1024 * 1024;
    private const int CbsDays = 90;

    public override string Section => nameof(Snapshot.Health);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(6);

    protected override WindowsHealthInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var info = new WindowsHealthInfo
        {
            Services = ServiceNames.Select(Service).ToList(),
            TempProfile = Path.GetFileName(profile.TrimEnd('\\')).StartsWith("TEMP", StringComparison.OrdinalIgnoreCase),
            ProfileBackups = Reg.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
                .Count(k => k.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)),
        };
        ct.ThrowIfCancellationRequested();
        info.Cbs = Cbs(DateTime.UtcNow);
        ct.ThrowIfCancellationRequested();
        Stability(info);
        return info;
    }

    private static ServiceState Service(string name)
    {
        var state = new ServiceState { Name = name };
        try
        {
            using var sc = new ServiceController(name);
            state.DisplayName = sc.DisplayName;
            state.Status = sc.Status.ToString();
            state.StartType = sc.StartType.ToString();
        }
        catch (InvalidOperationException) { } // not installed (Status stays null)
        catch (System.ComponentModel.Win32Exception) { }
        return state;
    }

    /// <summary>Counts SFC/DISM results in the tail of CBS.log (it can be hundreds of MB; only the end matters).</summary>
    internal static CbsSummary Cbs(DateTime nowUtc)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\CBS\CBS.log");
        var summary = new CbsSummary();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            summary.LastWriteUtc = File.GetLastWriteTimeUtc(path);
            if (stream.Length > CbsTailBytes) stream.Position = stream.Length - CbsTailBytes;
            using var reader = new StreamReader(stream);
            Count(reader, nowUtc.AddDays(-CbsDays), summary);
        }
        catch (FileNotFoundException) { summary.Unavailable = "no CBS.log"; }
        catch (DirectoryNotFoundException) { summary.Unavailable = "no CBS.log"; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { summary.Unavailable = "CBS.log needs admin"; }
        return summary;
    }

    internal static void Count(TextReader reader, DateTime sinceUtc, CbsSummary summary)
    {
        while (reader.ReadLine() is { } line)
        {
            // Lines start "2026-10-20 11:22:33, Info  CSI ..." (local time); skip old ones and partial lines.
            if (line.Length < 19 || !DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
                || t.ToUniversalTime() < sinceUtc) continue;
            if (line.IndexOf("Cannot repair member file", StringComparison.Ordinal) >= 0) summary.CannotRepair++;
            else if (line.IndexOf("Repairing corrupted file", StringComparison.Ordinal) >= 0) summary.Repaired++;
            else if (line.IndexOf("CSI Payload Corrupt", StringComparison.Ordinal) >= 0 || line.IndexOf("Corruption detected", StringComparison.OrdinalIgnoreCase) >= 0) summary.StoreCorrupt++;
        }
    }

    /// <summary>Reliability Monitor's hourly stability index (about 650 ms on the owner's laptop; WQL can't limit rows).</summary>
    private static void Stability(WindowsHealthInfo info)
    {
        List<WmiRow> rows;
        try { rows = Wmi.QueryOptional("SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics"); }
        catch (CollectorException) { return; } // the reliability service is off; not a WMI failure worth reporting
        var points = rows
            .Select(r => (index: r["SystemStabilityIndex"] is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : (double?)null,
                          time: Wmi.ParseCimDate(r.Str("TimeGenerated"))?.ToUniversalTime()))
            .Where(p => p.index is not null && p.time is not null)
            .OrderByDescending(p => p.time)
            .ToList();
        if (points.Count == 0) return;
        info.StabilityIndex = Math.Round(points[0].index!.Value, 2);
        var since = points[0].time!.Value.AddDays(-30);
        info.StabilityLowest30Days = Math.Round(points.Where(p => p.time >= since).Min(p => p.index!.Value), 2);
    }
}
