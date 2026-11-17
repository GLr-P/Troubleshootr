using System.IO;

namespace Troubleshootr.Core;

/// <summary>
/// Where Troubleshootr keeps its own files (settings, the actions log, saved reports).
/// From a USB stick (or any drive other than Windows'): next to the exe, so the settings and the Plannr link travel with it.
/// From the Windows drive (the Desktop, Downloads…): %LOCALAPPDATA%\Troubleshootr, so nothing piles up next to the exe.
/// </summary>
public static class AppFiles
{
    public const string SettingsFile = "Troubleshootr-settings.json";
    public const string LogFile = "Troubleshootr-actions.log";

    public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory;

    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Troubleshootr");

    private static string WindowsRoot => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    /// <summary>True when the exe sits on the drive Windows is installed on.</summary>
    public static bool OnWindowsDrive(string exeDir, string windowsRoot)
    {
        try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(exeDir)), windowsRoot, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Folders to try, in order: the first writable one is used.</summary>
    public static string[] Folders(string exeDir, string windowsRoot, string localDir) =>
        OnWindowsDrive(exeDir, windowsRoot) ? [localDir] : [exeDir, localDir];

    public static string[] Folders() => Folders(ExeDir, WindowsRoot, LocalDir);

    /// <summary>
    /// Versions up to 1.1.0 kept their files next to the exe even on the Desktop. Moves them to %LOCALAPPDATA% (newest
    /// settings win, log lines are appended), so the Desktop is left with just Troubleshootr.exe.
    /// </summary>
    public static void Tidy() => Tidy(ExeDir, WindowsRoot, LocalDir);

    public static void Tidy(string exeDir, string windowsRoot, string localDir)
    {
        if (!OnWindowsDrive(exeDir, windowsRoot) || PathsEqual(exeDir, localDir)) return;
        foreach (var name in new[] { SettingsFile, LogFile })
        {
            var from = Path.Combine(exeDir, name);
            var to = Path.Combine(localDir, name);
            try
            {
                if (!File.Exists(from)) continue;
                Directory.CreateDirectory(localDir);
                if (name == LogFile && File.Exists(to)) File.AppendAllText(to, File.ReadAllText(from));
                else if (!File.Exists(to) || File.GetLastWriteTimeUtc(from) > File.GetLastWriteTimeUtc(to)) File.Copy(from, to, overwrite: true);
                File.Delete(from);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }  // left where it was; tried again next start
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
