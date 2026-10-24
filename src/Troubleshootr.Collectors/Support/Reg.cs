using Microsoft.Win32;

namespace Troubleshootr.Collectors.Support;

/// <summary>Registry reads that never throw. Uses the 64-bit view on 64-bit Windows unless asked otherwise.</summary>
public static class Reg
{
    public static RegistryView NativeView => Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;

    public static object? Get(RegistryHive hive, string path, string name, RegistryView? view = null)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view ?? NativeView);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static string? Str(RegistryHive hive, string path, string name) => Get(hive, path, name) switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
        _ => null,
    };

    public static long? Num(RegistryHive hive, string path, string name) => Get(hive, path, name) switch
    {
        int i => i,
        long l => l,
        string s when long.TryParse(s, out var l) => l,
        _ => null,
    };

    public static bool KeyExists(RegistryHive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, NativeView);
            using var key = root.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    public static string[] SubKeys(RegistryHive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, NativeView);
            using var key = root.OpenSubKey(path);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }
}
