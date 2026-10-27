using System.Diagnostics;

namespace Troubleshootr.Core;

/// <summary>A pure function of a Snapshot. Never touches the system. Ids match docs/CHECKS.md.</summary>
public interface ICheck
{
    string Id { get; }
    Category Category { get; }
    /// <summary>Snapshot section names this check reads. If any is missing, the check is reported as not checked.</summary>
    IReadOnlyList<string> Needs { get; }
    IEnumerable<Finding> Run(Snapshot s);
}

/// <summary>Marker: the check is about physical hardware (RAM sticks, BIOS…) and is skipped on virtual machines.</summary>
public interface IPhysicalHardwareCheck : ICheck { }

/// <summary>Marker: run this check after every collector has finished (it looks across all sections).</summary>
public interface IRunsLast : ICheck { }

public sealed class CollectContext
{
    public bool IsAdmin { get; init; }
}

/// <summary>Fills one Snapshot section. Must never throw; the base class turns failures into Unavailable.</summary>
public interface ICollector
{
    string Section { get; }
    bool NeedsAdmin { get; }
    /// <summary>Slow collectors only run in a Full scan.</summary>
    bool Slow { get; }
    TimeSpan Timeout { get; }
    Task RunAsync(Snapshot s, CollectContext ctx, CancellationToken ct);
}

public abstract class Collector<T> : ICollector where T : class
{
    public abstract string Section { get; }
    public virtual bool NeedsAdmin => false;
    public virtual bool Slow => false;
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(5);

    protected abstract T Collect(CollectContext ctx, CancellationToken ct);

    public async Task RunAsync(Snapshot s, CollectContext ctx, CancellationToken ct)
    {
        var section = await CollectSection(ctx, ct).ConfigureAwait(false);
        var prop = typeof(Snapshot).GetProperty(Section)
            ?? throw new InvalidOperationException($"Snapshot has no section {Section}");
        lock (s) prop.SetValue(s, section);
    }

    private async Task<Section<T>> CollectSection(CollectContext ctx, CancellationToken ct)
    {
        if (NeedsAdmin && !ctx.IsAdmin) return Section<T>.Missing("needs admin");
        var work = Task.Run(() => Collect(ctx, ct), ct);
        var done = await Task.WhenAny(work, Task.Delay(Timeout, ct)).ConfigureAwait(false);
        if (done != work)
        {
            _ = work.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return Section<T>.Missing($"timed out after {Timeout.TotalSeconds:0.#} s");
        }
        try { return Section<T>.Of(await work.ConfigureAwait(false)); }
        catch (Exception e) { return Section<T>.Missing(Describe(e)); }
    }

    private static string Describe(Exception e)
    {
        while (e is AggregateException { InnerException: { } inner }) e = inner;
        return e is CollectorException ? e.Message : $"{e.GetType().Name}: {e.Message}";
    }
}

/// <summary>
/// Something that changes the PC (system file repair, DNS flush…). Runs only after a button press and a confirmation;
/// what it did is written to the action log and the report.
/// </summary>
public interface IFixAction
{
    /// <summary>"system-files", matches Finding.FixActionId.</summary>
    string Id { get; }
    string Title { get; }
    /// <summary>What it changes on the PC, shown in the confirmation.</summary>
    string Changes { get; }
    bool NeedsAdmin { get; }
    /// <summary>"a few seconds", "15–45 minutes".</summary>
    string Duration { get; }
    /// <summary>Runs the fix; <paramref name="output"/> receives progress lines. Never throws: failures come back as Ok = false.</summary>
    Task<FixRecord> RunAsync(Action<string> output, CancellationToken ct);
}

/// <summary>Thrown by collectors with a message meant for the report ("WMI: invalid class").</summary>
public sealed class CollectorException(string message, Exception? inner = null) : Exception(message, inner);

public static class Timing
{
    public static long Ms(Stopwatch sw) => (long)sw.Elapsed.TotalMilliseconds;
}
