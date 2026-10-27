using System.IO;
using System.Security.Cryptography;
using System.Text;
using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>
/// Troubleshootr-settings.json next to the exe (the USB stick), or in %LOCALAPPDATA%\Troubleshootr if that's read-only.
/// The Claude API key is encrypted with Windows DPAPI for the current Windows user, one entry per PC and user: a stick
/// carried to a customer's PC can't decrypt the technician's key there (it asks again), and the customer's PC never
/// gets a readable copy.
/// </summary>
public sealed class Settings
{
    public Dictionary<string, string> ClaudeKeys { get; set; } = new();

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Troubleshootr Claude key v1");

    private static string ThisPc => Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes($"{Environment.MachineName}\\{Environment.UserName}".ToUpperInvariant())));

    public static Settings Load()
    {
        foreach (var path in Paths())
        {
            try { if (File.Exists(path)) return Json.Deserialize<Settings>(File.ReadAllText(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
        }
        return new Settings();
    }

    /// <summary>The key saved for this PC and user, or null (none, or saved on another PC).</summary>
    public string? ClaudeKey
    {
        get
        {
            if (!ClaudeKeys.TryGetValue(ThisPc, out var blob)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
        }
    }

    /// <summary>Saves (or with null, forgets) this PC's key. Returns false if no settings file could be written.</summary>
    public bool SetClaudeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) ClaudeKeys.Remove(ThisPc);
        else ClaudeKeys[ThisPc] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key!.Trim()), Entropy, DataProtectionScope.CurrentUser));
        foreach (var path in Paths())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Json.Serialize(this));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    private static IEnumerable<string> Paths()
    {
        yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Troubleshootr-settings.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Troubleshootr", "Troubleshootr-settings.json");
    }
}
