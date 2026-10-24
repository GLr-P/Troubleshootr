namespace Troubleshootr.Core;

/// <summary>Windows naming and support dates. Update the table when Microsoft publishes new end dates.</summary>
public static class WindowsVersion
{
    public static bool IsWindows11(SystemInfo s) => s.MajorVersion == 10 && s.Build >= 22000;

    /// <summary>"Windows 11 Pro 24H2 (build 26100.6584)". The registry ProductName says "Windows 10" on Windows 11.</summary>
    public static string Describe(SystemInfo s)
    {
        var name = s.WindowsProductName ?? "Windows";
        if (IsWindows11(s)) name = name.Replace("Windows 10", "Windows 11");
        var version = string.IsNullOrEmpty(s.WindowsDisplayVersion) ? "" : " " + s.WindowsDisplayVersion;
        return $"{name}{version} (build {s.Build}.{s.Ubr})";
    }

    /// <summary>LTSC/LTSB (EditionId EnterpriseS/EnterpriseSN), IoT or Server editions have their own, longer support dates.</summary>
    public static bool IsLongTermEdition(SystemInfo s) =>
        s.EditionId is { } e && (e.IndexOf("EnterpriseS", StringComparison.OrdinalIgnoreCase) >= 0
            || e.IndexOf("IoT", StringComparison.OrdinalIgnoreCase) >= 0
            || e.StartsWith("Server", StringComparison.OrdinalIgnoreCase));

    public static bool IsEnterpriseOrEducation(SystemInfo s) =>
        s.EditionId is { } e && (e.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) || e.StartsWith("Education", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// End of security updates for this Windows version, or null if unknown / long-term edition / newer than the table.
    /// Sources: Microsoft lifecycle pages for Windows 10 and Windows 11 (Home/Pro dates; Enterprise/Education get +12 months on H2 releases of Windows 11).
    /// </summary>
    public static DateTime? EndOfSupport(SystemInfo s)
    {
        if (IsLongTermEdition(s)) return null;
        if (s.MajorVersion == 6 && s.MinorVersion == 1) return new DateTime(2020, 1, 14);   // Windows 7
        if (s.MajorVersion == 6 && s.MinorVersion == 2) return new DateTime(2016, 1, 12);   // Windows 8
        if (s.MajorVersion == 6 && s.MinorVersion == 3) return new DateTime(2023, 1, 10);   // Windows 8.1
        if (s.MajorVersion != 10) return null;
        if (!IsWindows11(s)) return new DateTime(2025, 10, 14);                              // every Windows 10 consumer release
        var ent = IsEnterpriseOrEducation(s);
        return s.Build switch
        {
            22000 => ent ? new DateTime(2024, 10, 8) : new DateTime(2023, 10, 10),           // 21H2
            22621 => ent ? new DateTime(2025, 10, 14) : new DateTime(2024, 10, 8),           // 22H2
            22631 => ent ? new DateTime(2026, 11, 10) : new DateTime(2025, 11, 11),          // 23H2
            26100 => ent ? new DateTime(2027, 10, 12) : new DateTime(2026, 10, 13),          // 24H2
            26200 => ent ? new DateTime(2028, 10, 10) : new DateTime(2027, 10, 12),          // 25H2
            _ => null,
        };
    }
}
