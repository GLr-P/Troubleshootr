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
