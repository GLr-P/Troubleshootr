using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

public static class CollectorRegistry
{
    /// <summary>Every collector in this assembly, found by reflection, so adding one is a single file.</summary>
    public static IReadOnlyList<ICollector> All { get; } = typeof(CollectorRegistry).Assembly.GetTypes()
        .Where(t => typeof(ICollector).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .Select(t => (ICollector)Activator.CreateInstance(t)!)
        .OrderBy(c => c.Section, StringComparer.Ordinal)
        .ToList();

    public static bool IsElevated() => Native.IsElevated();
}
