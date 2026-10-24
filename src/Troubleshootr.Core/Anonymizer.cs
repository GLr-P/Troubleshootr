using System.Collections;
using System.Reflection;

namespace Troubleshootr.Core;

/// <summary>Blanks every [Sensitive] string in the snapshot, so reports and fixtures can be shared.</summary>
public static class Anonymizer
{
    public const string Redacted = "[redacted]";

    public static void Apply(Snapshot s)
    {
        Walk(s);
        s.Meta.Anonymized = true;
    }

    private static void Walk(object? node)
    {
        if (node is null || node is string || node.GetType().IsPrimitive || node is DateTime) return;
        if (node is IEnumerable list) { foreach (var item in list) Walk(item); return; }
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            if (p.PropertyType == typeof(string))
            {
                if (p.CanWrite && p.GetCustomAttribute<SensitiveAttribute>() is not null && p.GetValue(node) is string { Length: > 0 })
                    p.SetValue(node, Redacted);
            }
            else if (!p.PropertyType.IsValueType) Walk(p.GetValue(node));
        }
    }
}
