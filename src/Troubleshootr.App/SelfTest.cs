using System.IO;
using Troubleshootr.Checks;
using Troubleshootr.Collectors;
using Troubleshootr.Collectors.Sensors;
using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>
/// Troubleshootr.exe --selftest --out result.txt: without opening a window, runs a Quick scan, writes the HTML report and
/// opens the sensor library, so a release build (including the lone single exe) can be checked on any PC. Exit code 0 = all good.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var i = Array.IndexOf(args, "--out");
        var outPath = i >= 0 && i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "troubleshootr-selftest.txt");
        var lines = new List<string> { $"Troubleshootr {MainViewModel.Version}, {Environment.OSVersion}, 64-bit process: {Environment.Is64BitProcess}" };
        var ok = true;
        try
        {
            var report = await ScanEngine.ScanAsync(CollectorRegistry.All, CheckRegistry.All,
                new ScanOptions { IsAdmin = CollectorRegistry.IsElevated(), ToolVersion = MainViewModel.Version });
            lines.Add($"Quick scan: {report.DurationMs} ms, {report.Findings.Count} findings, {report.NotChecked.Count} not checked");
            lines.Add($"HTML report: {HtmlReport.Write(Anonymizer.Apply(report)).Length:N0} characters");
        }
        catch (Exception e) { ok = false; lines.Add($"Scan FAILED: {e}"); }
        try
        {
            using var sensors = SensorSession.Open();
            var readings = sensors.Read();
            lines.Add($"Sensor library from {LhmLoader.Folder()}: {(sensors.Problem is { } p ? "PROBLEM " + p : $"{readings.Count} readings")}");
            if (sensors.Problem is not null) ok = false;
        }
        catch (Exception e) { ok = false; lines.Add($"Sensors FAILED: {e}"); }
        lines.Add(ok ? "OK" : "FAILED");
        try { File.WriteAllLines(outPath, lines); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 2; }  // never leave a dialog open: package.ps1 waits on this
        return ok ? 0 : 1;
    }
}
