using System.Reflection;
using System.Text;
using Troubleshootr.Checks;
using Troubleshootr.Collectors;
using Troubleshootr.Core;

namespace Troubleshootr.Cli;

/// <summary>
/// troubleshootr scan    [--full] [--categories storage,memory] [--json] [--timings] [--anonymize] [--out file]
/// troubleshootr collect --out file.snap.json [--full] [--anonymize]
/// troubleshootr check   --snapshot file.snap.json [--json]
/// troubleshootr anonymize --snapshot report-or-snapshot.json --out file.snap.json   (e.g. a report saved by an admin run)
/// troubleshootr dump --file C:\Windows\Minidump\x.dmp [--drivers]   (reading the Minidump folder needs admin)
/// troubleshootr speedtest   (downloads for up to 10 s, the same test as the Network tab's button)
/// troubleshootr sensors | loadtest | driver [--remove|--check]   (sensor readings, 30 s CPU stress test, install/remove PawnIO with one UAC prompt, or only download and verify it)
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var a = new Args(args);
            switch (a.Command)
            {
                case "scan": return await Scan(a, snapshotOnly: false);
                case "collect": return await Scan(a, snapshotOnly: true);
                case "check": return CheckSnapshot(a);
                case "anonymize": return AnonymizeFile(a);
                case "dump": return Dump(a);
                case "sensors": return Sensors();
                case "driver": return await Driver(a);
                case "loadtest":
                    var result = await Troubleshootr.Collectors.Sensors.LoadTest.RunAsync(x => Console.WriteLine($"{x.Second,3} s  speed {x.SpeedPercent:0}%  temp {x.TempC:0} °C"), CancellationToken.None);
                    Console.WriteLine(result.Summary);
                    return 0;
                case "speedtest":
                    var (mbps, mb) = await SpeedTest.RunAsync(CancellationToken.None);
                    Console.WriteLine($"Download: {mbps:0.#} Mb/s ({mb:0.#} MB)");
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: troubleshootr scan|collect|check [options]. See Program.cs for the options.");
                    return 2;
            }
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    /// <summary>Every sensor once (CPU/RAM temperatures need PawnIO and admin).</summary>
    private static int Sensors()
    {
        using var session = Troubleshootr.Collectors.Sensors.SensorSession.Open();
        var readings = session.Read();
        Console.WriteLine($"PawnIO installed: {Troubleshootr.Collectors.Sensors.PawnIo.Installed}; admin: {CollectorRegistry.IsElevated()}{(session.Problem is { } p ? $"; problem: {p}" : "")}");
        foreach (var r in readings.Concat(Troubleshootr.Collectors.Sensors.SensorsCollector.ThermalZones()).Where(r => r.Type is "Temperature" or "Fan" or "Clock" or "Power"))
            Console.WriteLine($"{r.Kind,-11} {r.Hardware,-32} {r.Name,-28} {r.Format(r.Value)}");
        return 0;
    }

    /// <summary>driver [--remove]: the same download, signature check and elevated setup as the Sensors tab's button.</summary>
    private static async Task<int> Driver(Args a)
    {
        var remove = a.Flag("remove");
        using var setup = await Troubleshootr.Collectors.Sensors.PawnIo.DownloadAsync(CancellationToken.None);
        Console.WriteLine($"Downloaded PawnIO setup, signed by {setup.Publisher}.");
        if (a.Flag("check")) return 0;  // verify only
        var (ok, message) = Troubleshootr.Collectors.Sensors.PawnIo.Run(setup, remove);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static async Task<int> Scan(Args a, bool snapshotOnly)
    {
        var options = new ScanOptions
        {
            Full = a.Flag("full"),
            Categories = a.Categories(),
            Anonymize = a.Flag("anonymize"),
            IsAdmin = CollectorRegistry.IsElevated(),
            ToolVersion = Version,
        };
        var report = await ScanEngine.ScanAsync(CollectorRegistry.All, CheckRegistry.All, options);
        if (snapshotOnly)
        {
            var path = a.Value("out") ?? throw new ArgumentException("collect needs --out <file>");
            File.WriteAllText(path, Json.Serialize(report.Snapshot));
            Console.WriteLine($"Snapshot written to {path} ({report.DurationMs} ms{(options.Anonymize ? ", anonymized" : "")}).");
            return 0;
        }
        return Output(a, report);
    }

    private static int CheckSnapshot(Args a)
    {
        var path = a.Value("snapshot") ?? throw new ArgumentException("check needs --snapshot <file>");
        var snapshot = Json.Deserialize<Snapshot>(File.ReadAllText(path));
        var report = ScanEngine.Evaluate(snapshot, CheckRegistry.All, new ScanOptions { Categories = a.Categories(), ToolVersion = Version });
        return Output(a, report);
    }

    private static int AnonymizeFile(Args a)
    {
        var path = a.Value("snapshot") ?? throw new ArgumentException("anonymize needs --snapshot <file>");
        var outPath = a.Value("out") ?? throw new ArgumentException("anonymize needs --out <file>");
        var json = File.ReadAllText(path);
        // Accept a full report (from scan --json) or a bare snapshot.
        var snapshot = json.Contains("\"findings\"") ? Json.Deserialize<Report>(json).Snapshot ?? throw new ArgumentException("report has no snapshot") : Json.Deserialize<Snapshot>(json);
        Anonymizer.Apply(snapshot);
        File.WriteAllText(outPath, Json.Serialize(snapshot));
        Console.WriteLine($"Anonymized snapshot written to {outPath}.");
        return 0;
    }

    /// <summary>Shows what a crash dump says: bugcheck, parameters, the driver at fault and (with --drivers) the loaded drivers.</summary>
    private static int Dump(Args a)
    {
        var path = a.Value("file") ?? throw new ArgumentException("dump needs --file <file.dmp>");
        var dump = Collectors.Crashes.CrashesCollector.ReadDump(path, out var drivers);
        if (dump.ParseError is { } err) { Console.WriteLine($"{path}: {err}"); return 1; }
        var code = dump.BugCheck ?? 0;
        Console.WriteLine($"{Path.GetFileName(path)}  {dump.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {dump.SizeBytes:N0} bytes");
        Console.WriteLine($"Bugcheck {BugChecks.Hex(code)} {BugChecks.Name(code)} ({string.Join(", ", dump.Parameters.Select(p => $"0x{p:X}"))})");
        if (BugChecks.Lookup(code) is { } info) Console.WriteLine(info.Meaning);
        Console.WriteLine($"Driver at fault: {dump.Driver ?? "not identified"}");
        Console.WriteLine(drivers is null ? "Driver list: not found (not a 64-bit small dump)" : $"Driver list: {drivers.Count} drivers");
        if (a.Flag("drivers") && drivers is not null)
            foreach (var d in drivers) Console.WriteLine($"  0x{d.Base:X16}  {d.Size,10:N0}  {d.Name}");
        return 0;
    }

    private static int Output(Args a, Report report)
    {
        var text = a.Flag("json") ? Json.Serialize(report) : TextReport.Write(report, a.Flag("timings"));
        if (a.Value("out") is { } outPath) File.WriteAllText(outPath, text);
        else Console.WriteLine(text);
        return report.Findings.Any(f => f.Severity == Severity.Critical) ? 1 : 0;
    }

    private sealed class Args
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
        public string Command { get; }

        public Args(string[] args)
        {
            Command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument {args[i]}");
                var name = args[i].Substring(2);
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                _options[name] = value;
            }
        }

        public bool Flag(string name) => _options.ContainsKey(name);
        public string? Value(string name) => _options.TryGetValue(name, out var v) ? v : null;

        public IReadOnlyCollection<Category>? Categories()
        {
            if (Value("categories") is not { } list) return null;
            return list.Split([','], StringSplitOptions.RemoveEmptyEntries).Select(c =>
                Enum.TryParse<Category>(c.Trim(), ignoreCase: true, out var cat) ? cat
                    : throw new ArgumentException($"Unknown category '{c}'. Use: {string.Join(", ", Enum.GetNames(typeof(Category)))}")).ToList();
        }
    }
}
