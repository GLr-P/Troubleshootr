namespace Troubleshootr.Core;

public enum Severity { Ok, Info, Warning, Critical }

public enum Category
{
    Storage, Memory, CpuThermal, Graphics, WindowsHealth, Updates, Drivers, Crashes,
    Performance, Network, Security, Battery, Peripherals,
}

/// <summary>One problem (or notable fact) a check found. Evidence holds the real numbers; Advice says what to do.</summary>
public sealed record Finding(
    string CheckId,
    Category Category,
    Severity Severity,
    string Title,
    string Evidence,
    string Advice,
    string? FixActionId = null);

/// <summary>A check that couldn't run, and why (missing admin, not supported, collector failed).</summary>
public sealed record NotChecked(string CheckId, Category Category, string Reason);

/// <summary>
/// One part of the Snapshot: either a value or the reason it's missing. A null section on the Snapshot means
/// the collector wasn't run in this scan.
/// </summary>
public sealed class Section<T> where T : class
{
    public T? Value { get; set; }
    public string? Unavailable { get; set; }

    public static Section<T> Of(T value) => new() { Value = value };
    public static Section<T> Missing(string reason) => new() { Unavailable = reason };
}

/// <summary>Marks a string property that identifies a person or device; the anonymizer blanks it.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute { }

/// <summary>Marks free text (messages, paths) that may contain user or device names; the anonymizer scrubs those names out.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FreeTextAttribute : Attribute { }
