using System.IO;

namespace Troubleshootr.Core;

/// <summary>
/// Everything Troubleshootr changes on a PC is written to Troubleshootr-actions.log next to the exe on a USB stick, or in
/// %LOCALAPPDATA%\Troubleshootr when run from the Windows drive or a read-only stick (see AppFiles). Used by the app and by the CLI's fix command.
/// </summary>
public static class ActionLog
{
    public static void Write(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.MachineName}: {text}{Environment.NewLine}";
        foreach (var dir in AppFiles.Folders())
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, AppFiles.LogFile), line);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
