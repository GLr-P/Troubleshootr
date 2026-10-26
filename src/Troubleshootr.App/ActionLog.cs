using System.IO;

namespace Troubleshootr.App;

/// <summary>
/// Everything Troubleshootr changes on a PC is written to Troubleshootr-actions.log next to the exe (on the USB stick),
/// or in %LOCALAPPDATA%\Troubleshootr if that folder is read-only. The fix-action framework (Phase 12) builds on it.
/// </summary>
public static class ActionLog
{
    public static void Write(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.MachineName}: {text}{Environment.NewLine}";
        foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Troubleshootr") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "Troubleshootr-actions.log"), line);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
