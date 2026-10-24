using System.Management;
using System.Runtime.InteropServices;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Support;

/// <summary>
/// WMI access with the speed rules from docs/ARCHITECTURE.md §4: list the columns, forward-only enumeration, and
/// failures turned into CollectorException("WMI: …") so check WIN-011 can spot a broken WMI repository.
/// </summary>
public static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string RootWmi = @"root\wmi";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string ErrorPrefix = "WMI: ";

    public static List<WmiRow> Query(string wql, string ns = Cimv2)
    {
        try
        {
            var options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(wql), options);
            using var results = searcher.Get();
            var rows = new List<WmiRow>();
            foreach (var o in results)
                using (o) rows.Add(new WmiRow(o.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase)));
            return rows;
        }
        catch (ManagementException e) { throw new CollectorException($"{ErrorPrefix}{e.ErrorCode} in {ns} ({wql})", e); }
        catch (COMException e) { throw new CollectorException($"{ErrorPrefix}0x{e.ErrorCode:X8} in {ns} ({wql})", e); }
    }

    /// <summary>Like Query, but returns an empty list when the class or namespace doesn't exist (older Windows).</summary>
    public static List<WmiRow> QueryOptional(string wql, string ns = Cimv2)
    {
        try { return Query(wql, ns); }
        catch (CollectorException e) when (e.InnerException is ManagementException { ErrorCode: ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace or ManagementStatus.NotFound or ManagementStatus.InvalidQuery }) { return []; }
    }

    public static DateTime? ParseCimDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or FormatException) { return null; }
    }
}

public sealed class WmiRow(Dictionary<string, object?> values)
{
    public object? this[string name] => values.TryGetValue(name, out var v) ? v : null;

    public string? Str(string name) => this[name] switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        var v => v.ToString(),
    };

    public long? Long(string name) => this[name] switch
    {
        null => null,
        byte b => b, sbyte b => b, short s => s, ushort s => s, int i => i, uint u => u, long l => l,
        ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
        string s when long.TryParse(s, out var l) => l,
        _ => null,
    };

    public int? Int(string name) => Long(name) is { } l ? (int)Math.Min(l, int.MaxValue) : null;
    public bool? Bool(string name) => this[name] as bool?;
    public ushort[]? UShorts(string name) => this[name] as ushort[];
}
