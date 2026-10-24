using System.Diagnostics;

namespace Troubleshootr.Core;

public sealed class ScanOptions
{
    /// <summary>Full scan: also runs the slow collectors.</summary>
    public bool Full { get; init; }
    /// <summary>Only these categories; null = all.</summary>
    public IReadOnlyCollection<Category>? Categories { get; init; }
    public bool Anonymize { get; init; }
    public bool IsAdmin { get; init; }
    public bool KeepSnapshot { get; init; } = true;
    public string ToolVersion { get; init; } = "";
}

public static class ScanEngine
{
    /// <summary>Sections every scan collects, because the device header needs them.</summary>
    public static readonly IReadOnlyList<string> AlwaysCollect = ["System", "Cpu", "Memory", "Gpus", "Storage", "Volumes"];

    /// <summary>
    /// Runs the needed collectors in parallel, then each check as soon as its sections are ready.
    /// <paramref name="onFinding"/> is called as findings appear (any thread).
    /// </summary>
    public static async Task<Report> ScanAsync(
        IEnumerable<ICollector> collectors, IEnumerable<ICheck> checks, ScanOptions o,
        Action<Finding>? onFinding = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var started = DateTime.UtcNow;
        var selected = checks.Where(c => o.Categories is null || o.Categories.Contains(c.Category)).ToList();
        var needed = new HashSet<string>(selected.SelectMany(c => c.Needs).Concat(AlwaysCollect));
        var all = collectors.Where(c => needed.Contains(c.Section)).ToList();
        var toRun = all.Where(c => o.Full || !c.Slow).ToList();

        var snapshot = new Snapshot
        {
            Meta = new SnapshotMeta { ToolVersion = o.ToolVersion, CollectedUtc = started, IsAdmin = o.IsAdmin, UtcOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(started).TotalMinutes },
        };
        foreach (var skipped in all.Except(toRun))
            typeof(Snapshot).GetProperty(skipped.Section)!.SetValue(snapshot, MissingSection(skipped.Section, "Full scan only"));

        var ctx = new CollectContext { IsAdmin = o.IsAdmin };
        var timings = new Dictionary<string, long>();
        var ready = new HashSet<string>(snapshot.SectionNamesPresent());
        var pending = new List<ICheck>(selected);
        var findings = new List<Finding>();
        var notChecked = new List<NotChecked>();
        var gate = new object();
        var finished = false;

        void RunReadyChecks()
        {
            List<ICheck> runnable;
            lock (gate)
            {
                runnable = pending.Where(c => (finished || c is not IRunsLast) && c.Needs.All(ready.Contains)).ToList();
                pending.RemoveAll(runnable.Contains);
            }
            foreach (var check in runnable)
            {
                var (found, skipped) = RunCheck(check, snapshot);
                lock (gate) { findings.AddRange(found); if (skipped is not null) notChecked.Add(skipped); }
                foreach (var f in found) onFinding?.Invoke(f);
            }
        }

        await Task.WhenAll(toRun.Select(async c =>
        {
            var sw = Stopwatch.StartNew();
            await c.RunAsync(snapshot, ctx, ct).ConfigureAwait(false);
            lock (gate) { timings[c.Section] = Timing.Ms(sw); ready.Add(c.Section); }
            RunReadyChecks();
        })).ConfigureAwait(false);

        // Anything left needs a section no collector provides (or the selection excluded it).
        lock (gate) { finished = true; foreach (var c in pending) ready.UnionWith(c.Needs); }
        RunReadyChecks();

        if (o.Anonymize) Anonymizer.Apply(snapshot);
        return Report.Build(snapshot, findings, notChecked, timings, o, started, Timing.Ms(total));
    }

    /// <summary>Runs checks against an existing snapshot (fixtures, tests, re-evaluating a saved report).</summary>
    public static Report Evaluate(Snapshot snapshot, IEnumerable<ICheck> checks, ScanOptions? o = null)
    {
        o ??= new ScanOptions();
        var findings = new List<Finding>();
        var notChecked = new List<NotChecked>();
        foreach (var check in checks.Where(c => o.Categories is null || o.Categories.Contains(c.Category)))
        {
            var (found, skipped) = RunCheck(check, snapshot);
            findings.AddRange(found);
            if (skipped is not null) notChecked.Add(skipped);
        }
        return Report.Build(snapshot, findings, notChecked, new Dictionary<string, long>(), o, snapshot.Meta.CollectedUtc, 0);
    }

    private static (List<Finding> found, NotChecked? skipped) RunCheck(ICheck check, Snapshot s)
    {
        var missing = check.Needs.Select(n => (n, why: s.WhyMissing(n))).FirstOrDefault(x => x.why is not null);
        if (missing.why is not null) return ([], new NotChecked(check.Id, check.Category, $"{missing.n}: {missing.why}"));
        if (check is IPhysicalHardwareCheck && s.System?.Value?.IsVirtualMachine == true) return ([], new NotChecked(check.Id, check.Category, "virtual machine"));
        try { return (check.Run(s).ToList(), null); }
        catch (Exception e) { return ([], new NotChecked(check.Id, check.Category, $"check failed: {e.GetType().Name}: {e.Message}")); }
    }

    private static object MissingSection(string section, string reason)
    {
        var type = typeof(Snapshot).GetProperty(section)!.PropertyType;
        return type.GetMethod(nameof(Section<object>.Missing))!.Invoke(null, [reason])!;
    }

    private static IEnumerable<string> SectionNamesPresent(this Snapshot s) =>
        Snapshot.SectionNames.Where(n => typeof(Snapshot).GetProperty(n)!.GetValue(s) is not null);
}
