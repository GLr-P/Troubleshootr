using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Where Troubleshootr keeps its settings, log and reports (owner, 2026-11-17: "its dumping random files on my desktop").</summary>
public class AppFilesTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tsr-appfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void OnTheWindowsDriveFilesGoToLocalAppDataNotNextToTheExe()
    {
        var desktop = NewDir();
        var local = Path.Combine(NewDir(), "Troubleshootr");
        var windowsRoot = Path.GetPathRoot(desktop)!;
        Assert.Equal([local], AppFiles.Folders(desktop, windowsRoot, local));
    }

    [Fact]
    public void OnAUsbStickFilesStayNextToTheExe()
    {
        var stick = NewDir();
        var local = Path.Combine(NewDir(), "Troubleshootr");
        var otherRoot = Path.GetPathRoot(stick)!.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\";
        Assert.Equal([stick, local], AppFiles.Folders(stick, otherRoot, local));
    }

    [Fact]
    public void TidyMovesFilesAnOlderVersionLeftOnTheDesktop()
    {
        var desktop = NewDir();
        var local = Path.Combine(NewDir(), "Troubleshootr");
        var windowsRoot = Path.GetPathRoot(desktop)!;
        File.WriteAllText(Path.Combine(desktop, AppFiles.SettingsFile), """{"plannrLink":"x"}""");
        File.WriteAllText(Path.Combine(desktop, AppFiles.LogFile), "desktop line\n");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, AppFiles.LogFile), "older line\n");
        File.WriteAllText(Path.Combine(desktop, "Troubleshootr.exe"), "");

        AppFiles.Tidy(desktop, windowsRoot, local);

        Assert.Equal(["Troubleshootr.exe"], Directory.GetFiles(desktop).Select(Path.GetFileName));
        Assert.Equal("""{"plannrLink":"x"}""", File.ReadAllText(Path.Combine(local, AppFiles.SettingsFile)));
        Assert.Equal("older line\ndesktop line\n", File.ReadAllText(Path.Combine(local, AppFiles.LogFile)));
    }

    [Fact]
    public void TidyLeavesAUsbStickAlone()
    {
        var stick = NewDir();
        var local = Path.Combine(NewDir(), "Troubleshootr");
        File.WriteAllText(Path.Combine(stick, AppFiles.SettingsFile), "{}");
        AppFiles.Tidy(stick, @"Z:\" == Path.GetPathRoot(stick) ? @"Y:\" : @"Z:\", local);
        Assert.True(File.Exists(Path.Combine(stick, AppFiles.SettingsFile)));
        Assert.False(Directory.Exists(local));
    }
}
