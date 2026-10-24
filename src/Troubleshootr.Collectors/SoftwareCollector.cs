using System.Globalization;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Installed programs from the Uninstall keys (both views + per-user), the default browser, and Chromium extension counts.</summary>
public sealed class SoftwareCollector : Collector<SoftwareInfo>
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public override string Section => nameof(Snapshot.Software);

    protected override SoftwareInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new SoftwareInfo();
        var list = new List<InstalledProgram>();
        Read(list, RegistryHive.LocalMachine, Reg.NativeView, "Machine");
        if (Environment.Is64BitOperatingSystem) Read(list, RegistryHive.LocalMachine, RegistryView.Registry32, "Machine (32-bit)");
        Read(list, RegistryHive.CurrentUser, RegistryView.Default, "User");
        info.Programs = list.GroupBy(p => (p.Name.ToLowerInvariant(), p.Version)).Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            info.DefaultBrowser = key?.GetValue("ProgId") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (browser, dir) in new[] { ("Chrome", @"Google\Chrome\User Data"), ("Edge", @"Microsoft\Edge\User Data"), ("Brave", @"BraveSoftware\Brave-Browser\User Data") })
            Extensions(info, browser, Path.Combine(local, dir));
        return info;
    }

    private static void Read(List<InstalledProgram> list, RegistryHive hive, RegistryView view, string scope)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(Uninstall);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || name.Trim().Length == 0) continue;
                // Windows components, patches and child entries of bigger products aren't "programs" to a user.
                if (k.GetValue("SystemComponent") is 1 || k.GetValue("ParentKeyName") is not null
                    || k.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update") continue;
                list.Add(new InstalledProgram
                {
                    Name = name.Trim(),
                    Version = (k.GetValue("DisplayVersion") as string)?.Trim(),
                    Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                    InstallDate = k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null,
                    SizeKB = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb : null,
                    Scope = scope,
                });
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static void Extensions(SoftwareInfo info, string browser, string userData)
    {
        if (!Directory.Exists(userData)) return;
        try
        {
            foreach (var profile in Directory.GetDirectories(userData))
            {
                var name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                var ext = Path.Combine(profile, "Extensions");
                if (!Directory.Exists(ext)) continue;
                info.BrowserExtensions[$"{browser}: {name}"] = Directory.GetDirectories(ext).Count(d => Path.GetFileName(d) != "Temp");
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
    }
}
