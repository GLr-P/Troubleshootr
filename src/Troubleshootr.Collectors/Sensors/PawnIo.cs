using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;

namespace Troubleshootr.Collectors.Sensors;

/// <summary>
/// The PawnIO driver (signed, by namazso; https://pawnio.eu) that LibreHardwareMonitor uses for CPU, RAM and motherboard
/// sensors. Installed only when the user asks: download the official setup from GitHub into a fresh folder, check its
/// signature (valid, not revoked, signed by namazso) while holding the file locked, then run it elevated with
/// -install -silent. Removable the same way.
/// </summary>
public static class PawnIo
{
    private const string SetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
    /// <summary>The signer's certificate name on PawnIO 2.x releases ("namazso.eu").</summary>
    private const string ExpectedSigner = "namazso";

    public static bool Installed => Reg.KeyExists(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\PawnIO");

    /// <summary>A downloaded, verified setup. The file stays locked against changes until disposed, so what runs is what was checked.</summary>
    public sealed class Setup : IDisposable
    {
        private readonly FileStream _lock;
        public string Path { get; }
        public string Publisher { get; }

        internal Setup(string path, string publisher, FileStream fileLock) { Path = path; Publisher = publisher; _lock = fileLock; }

        public void Dispose()
        {
            _lock.Dispose();
            try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Downloads the setup and verifies it. Throws (and deletes the file) if the signature isn't valid and namazso's.</summary>
    public static async Task<Setup> DownloadAsync(CancellationToken ct)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var dir = Path.Combine(Path.GetTempPath(), "Troubleshootr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "PawnIO_setup.exe");
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        using (var response = await client.GetAsync(SetupUrl, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using var file = File.Create(path);
            await response.Content.CopyToAsync(file).ConfigureAwait(false);
        }
        // Lock first (others may read and run it, nobody may change or delete it), then verify.
        var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (signed, publisher) = Signatures.Check(path, checkRevocation: true);
        if (signed != true || publisher?.IndexOf(ExpectedSigner, StringComparison.OrdinalIgnoreCase) is null or < 0)
        {
            fileLock.Dispose();
            Directory.Delete(dir, recursive: true);
            throw new InvalidOperationException(signed == true
                ? $"the downloaded setup is signed by {publisher}, not by PawnIO's author, so it wasn't run"
                : "the downloaded PawnIO setup isn't validly signed, so it wasn't run");
        }
        return new Setup(path, publisher!, fileLock);
    }

    /// <summary>Runs the setup elevated (one UAC prompt). Returns a plain-English result; false if the user said no or it failed.</summary>
    public static (bool Ok, string Message) Run(Setup setup, bool uninstall)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(setup.Path, uninstall ? "-uninstall -silent" : "-install -silent") { Verb = "runas", UseShellExecute = true })!;
            if (!p.WaitForExit(120_000)) return (false, "the PawnIO setup didn't finish within 2 minutes");
            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED
            if (p.ExitCode == 3010) return (true, uninstall ? "PawnIO removed; restart Windows to finish." : "PawnIO installed; restart Windows before using advanced sensors.");
            if (p.ExitCode == 0 && (uninstall ? !Installed : Installed)) return (true, uninstall ? "PawnIO removed." : "PawnIO installed.");
            if (p.ExitCode == 0 && uninstall) return (true, "PawnIO is being removed; restart Windows to finish.");  // service marked for deletion
            return (false, $"the PawnIO setup ended with code {p.ExitCode}{(p.ExitCode != 0 ? $" ({new Win32Exception(p.ExitCode).Message})" : "")}");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return (false, "cancelled at the Windows admin prompt"); }
    }
}
