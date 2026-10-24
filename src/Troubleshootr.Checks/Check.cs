using Troubleshootr.Core;

namespace Troubleshootr.Checks;

/// <summary>Base class: declare Id, Category and Needs, implement Run, and build findings with <see cref="F"/>.</summary>
public abstract class Check : ICheck
{
    public abstract string Id { get; }
    public abstract Category Category { get; }
    public abstract IReadOnlyList<string> Needs { get; }
    public abstract IEnumerable<Finding> Run(Snapshot s);

    protected Finding F(Severity severity, string title, string evidence, string advice, string? fix = null) =>
        new(Id, Category, severity, title, evidence, advice, fix);

    /// <summary>"1 error", "3 errors".</summary>
    protected static string N(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    protected static string Gb(long bytes) => $"{bytes / 1073741824.0:0.#} GB";
    protected static string GbFromMb(long mb) => $"{mb / 1024.0:0.#} GB";
}

public static class CheckRegistry
{
    /// <summary>Every check in this assembly, found by reflection, so adding one is a single file.</summary>
    public static IReadOnlyList<ICheck> All { get; } = typeof(CheckRegistry).Assembly.GetTypes()
        .Where(t => typeof(ICheck).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .Select(t => (ICheck)Activator.CreateInstance(t)!)
        .OrderBy(c => c.Id, StringComparer.Ordinal)
        .ToList();
}
