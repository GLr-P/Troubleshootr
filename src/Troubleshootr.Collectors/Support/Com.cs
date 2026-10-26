using System.Globalization;
using System.Reflection;

namespace Troubleshootr.Collectors.Support;

/// <summary>Late-bound COM calls (Task Scheduler, Shell), so no interop assemblies ship with the app.</summary>
public static class Com
{
    public static object? Create(string progId) => Type.GetTypeFromProgID(progId) is { } t ? Activator.CreateInstance(t) : null;

    public static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    public static object? Get(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args.Length == 0 ? null : args, CultureInfo.InvariantCulture);

    /// <summary>A 1-based COM collection (Count + Item).</summary>
    public static IEnumerable<object> Items(object collection)
    {
        var count = Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
        for (var i = 1; i <= count; i++) yield return Get(collection, "Item", i)!;
    }

    public static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
}
