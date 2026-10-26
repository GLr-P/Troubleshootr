using System.Text.RegularExpressions;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Security;

internal static class Sec
{
    public static SecurityInfo Of(Snapshot s) => s.Security!.Value!;

    /// <summary>Antivirus products actually protecting the PC: Defender when it's in normal mode, plus every enabled third-party product.</summary>
    public static List<string> ActiveAv(SecurityInfo x)
    {
        var list = x.Antivirus.Where(a => a.Enabled && !a.IsDefender).Select(a => a.Name).ToList();
        var defenderOn = x.Defender is { } d ? d.IsPrimary && d.RealTimeProtection : x.Antivirus.Any(a => a.IsDefender && a.Enabled);
        if (defenderOn) list.Insert(0, "Microsoft Defender");
        return list;
    }
}

/// <summary>SEC-001: no antivirus running, or the running one is out of date.</summary>
public sealed class Sec001Antivirus : Check
{
    public override string Id => "SEC-001";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = Sec.Of(s);
        if (!x.SecurityCenter) yield break;  // Windows Server
        var active = Sec.ActiveAv(x);
        if (active.Count == 0)
        {
            var installed = x.Antivirus.Select(a => $"{a.Name} ({(a.Enabled ? "real-time protection off" : "off")})").ToList();
            yield return F(Severity.Critical, "No antivirus is protecting this PC",
                installed.Count > 0 ? $"Installed but switched off: {string.Join(", ", installed)}." : "Windows Security reports no antivirus at all.",
                "Turn Microsoft Defender back on (Windows Security > Virus & threat protection), or renew/repair the antivirus you use. An expired trial often leaves the PC with nothing.");
            yield break;
        }
        foreach (var a in x.Antivirus.Where(a => a.Enabled && !a.UpToDate && !a.IsDefender))
            yield return F(Severity.Warning, $"{a.Name} is out of date", "Windows Security reports its virus definitions as out of date.",
                "Open it and update, or renew the subscription. If it can't update, uninstall it and let Microsoft Defender take over.");
    }
}

/// <summary>SEC-002: Defender's real-time protection and tamper protection, and threats it found.</summary>
public sealed class Sec002Defender : Check
{
    public override string Id => "SEC-002";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = Sec.Of(s);
        if (x.Defender is { } d && d.RunningMode == "Normal" && d.AntivirusEnabled)
        {
            if (!d.RealTimeProtection)
                yield return F(Severity.Warning, "Defender's real-time protection is off", "Defender is the antivirus, but it only scans when asked.",
                    "Windows Security > Virus & threat protection > Manage settings: turn on Real-time protection. If it keeps switching off, malware or a policy may be doing it.");
            if (d.TamperProtection == false)
                yield return F(Severity.Info, "Defender's tamper protection is off", "Programs can change Defender's settings.",
                    "Turn on Tamper protection in Windows Security > Virus & threat protection > Manage settings, unless an IT department manages this PC.");
        }

        var open = x.Threats.Where(t => t.Active || t.Removed == false).ToList();
        if (open.Count > 0)
            yield return F(Severity.Critical, "Defender found a threat it couldn't remove",
                string.Join("; ", open.Take(5).Select(Describe)) + ".",
                "Run a Microsoft Defender Offline scan (Windows Security > Virus & threat protection > Scan options), then a second opinion scanner such as Malwarebytes. Change passwords from a clean device if it was a password stealer.");
        var cleaned = x.Threats.Except(open).ToList();
        if (cleaned.Count > 0)
            yield return F(Severity.Info, $"Defender removed {N(cleaned.Count, "threat")} in the last 30 days",
                string.Join("; ", cleaned.Take(5).Select(Describe)) + (cleaned.Count > 5 ? $" and {cleaned.Count - 5} more" : "") + ".",
                "Handled, but worth knowing where it came from (a download, a USB stick, a cracked program). Repeated detections mean the source is still around.");

        string Describe(ThreatDetection t) =>
            $"{t.Name}{(t.DetectedUtc is { } when ? $" on {s.Meta.Local(when):yyyy-MM-dd}" : "")}{(t.Location is { } w ? $" in {w}" : "")}";
    }
}

/// <summary>UPD-007: Defender's virus definitions are old.</summary>
public sealed class Upd007DefenderDefinitions : Check
{
    private const int OldDays = 7;   // Defender updates several times a day when it can reach Windows Update

    public override string Id => "UPD-007";
    public override Category Category => Category.Updates;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        if (Sec.Of(s).Defender is not { IsPrimary: true, SignatureAgeDays: { } age } d || age <= OldDays) yield break;
        yield return F(Severity.Warning, $"Defender's virus definitions are {age} days old",
            $"Last updated {(d.SignaturesUpdatedUtc is { } u ? s.Meta.Local(u).ToString("yyyy-MM-dd") : "unknown")}.",
            "Windows Security > Virus & threat protection > Protection updates > Check for updates. If that fails, Windows Update itself is probably broken (see the Updates findings).");
    }
}

/// <summary>PRF-004: two antivirus programs running at once (they fight over every file and slow the PC down).</summary>
public sealed class Prf004TwoAntivirus : Check
{
    public override string Id => "PRF-004";
    public override Category Category => Category.Performance;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var active = Sec.ActiveAv(Sec.Of(s));
        if (active.Count < 2) yield break;
        yield return F(Severity.Warning, "Two antivirus programs are running at once", string.Join(", ", active) + " are all scanning in real time.",
            "Keep one. Uninstall the others (use the vendor's removal tool if the normal uninstall leaves pieces behind). Microsoft Defender steps aside by itself when another antivirus is set up properly.");
    }
}

/// <summary>SEC-003: UAC switched off, or set to never ask.</summary>
public sealed class Sec003Uac : Check
{
    public override string Id => "SEC-003";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = Sec.Of(s);
        if (x.UacOn == false)
            yield return F(Severity.Warning, "User Account Control is switched off", "EnableLUA is 0: every program runs with full admin rights, and Store apps don't work.",
                "Turn it back on: Control Panel > User Accounts > Change User Account Control settings (the default level), then restart.");
        else if (x.UacAdminPrompt == 0)
            yield return F(Severity.Info, "User Account Control never asks", "Admin accounts get full rights without a prompt.",
                "Set it back to the default (Control Panel > User Accounts > Change User Account Control settings), so software can't silently make system changes.");
    }
}

/// <summary>SEC-004: startup entries that look like malware: script hosts, encoded commands, unsigned files in temp or public folders, random names.</summary>
public sealed class Sec004SuspiciousStartup : Check
{
    private static readonly string[] OddFolders = [@"\Temp\", @"\Users\Public\", @"\AppData\Local\Temp\", @"\Windows\Temp\", @"\$Recycle.Bin\"];
    private static readonly Regex RandomName = new(@"^([a-f0-9]{8,}|[bcdfghjklmnpqrstvwxz]{6,}[a-z0-9]*)$", RegexOptions.IgnoreCase);

    public override string Id => "SEC-004";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Startup)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var odd = new List<string>();
        foreach (var e in s.Startup!.Value!.Entries.Where(e => e.Enabled))
        {
            // Script-looking task commands are SEC-010's finding; here tasks are only judged by the file they run.
            var why = (e.IsTask ? null : SuspiciousCommand.Why(e.Command)) ?? (e.Signed == false ? UnsignedReason(e.File!) : null);
            if (why is not null) odd.Add($"{e.Name} ({why}): {e.Command}");
        }
        if (odd.Count == 0) yield break;
        yield return F(Severity.Warning, $"{N(odd.Count, "startup entry looks", "startup entries look")} suspicious", string.Join("; ", odd.Take(5)) + ".",
            "Look each one up. If you don't recognise it, switch it off in Task Manager > Startup apps (or delete the task) and scan with Microsoft Defender Offline and Malwarebytes.");
    }

    private static string? UnsignedReason(string file)
    {
        var dir = Path.GetDirectoryName(file) + @"\";
        if (OddFolders.Any(f => dir.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) return "unsigned, runs from a temp or public folder";
        if (Regex.IsMatch(dir, @"^[A-Z]:\\ProgramData\\$", RegexOptions.IgnoreCase)) return "unsigned, loose in ProgramData";
        if (RandomName.IsMatch(Path.GetFileNameWithoutExtension(file))) return "unsigned, random-looking name";
        return null;
    }
}

/// <summary>SEC-005: browser policies that force a home page, search engine or extension (adware's favourite lock-in).</summary>
public sealed class Sec005BrowserPolicies : Check
{
    public override string Id => "SEC-005";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = Sec.Of(s).BrowserPolicies;
        if (p.Count == 0) yield break;
        yield return F(Severity.Warning, "A browser is locked by policy", string.Join("; ", p.Take(6).Select(x => $"{x.Browser} {x.Kind.ToLowerInvariant()}: {x.Value}")) + (p.Count > 6 ? $" and {p.Count - 6} more" : "") + ".",
            "The browser shows \"Managed by your organization\". Fine on a work PC. Otherwise adware set it: remove the program that did it, then delete the policy keys (HKLM/HKCU\\SOFTWARE\\Policies\\Google\\Chrome, Microsoft\\Edge, Mozilla\\Firefox) and restart the browser.");
    }
}

/// <summary>SEC-006: remote-access tools installed or running (useful to a technician, and the first thing tech-support scammers install).</summary>
public sealed class Sec006RemoteAccess : Check
{
    private const int RecentDays = 14;

    public override string Id => "SEC-006";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security), nameof(Snapshot.Software)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = Sec.Of(s);
        var tools = RemoteTools.Find(s.Software?.Value?.Programs ?? [], x.RemoteToolsRunning).Select(t => (t.Name, Installed: t.Program?.InstallDate, t.Running)).ToList();
        if (tools.Count == 0) yield break;
        var recent = tools.Where(t => t.Installed is { } d && (s.Meta.CollectedUtc - d).TotalDays <= RecentDays).ToList();
        yield return F(Severity.Info, recent.Count > 0 ? "A remote-access app was installed recently" : $"{N(tools.Count, "remote-access app")} installed",
            string.Join("; ", tools.Select(t => $"{t.Name}{(t.Installed is { } d ? $" (installed {d:yyyy-MM-dd})" : "")}{(t.Running ? ", running" : "")}")) + ".",
            "Fine if you or your technician use it. If someone phoned, emailed or showed a pop-up asking you to install it, that's a scam: uninstall it, disconnect from the internet, and call your bank if they saw any accounts.");
    }
}

/// <summary>SEC-007: Secure Boot off, no TPM or an old one.</summary>
public sealed class Sec007PlatformSecurity : Check, IPhysicalHardwareCheck
{
    public override string Id => "SEC-007";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = Sec.Of(s);
        var b = s.Board?.Value;
        var issues = new List<string>();
        if (b?.FirmwareType == "UEFI" && b.SecureBoot == false) issues.Add("Secure Boot is off");
        if (b?.FirmwareType == "Legacy BIOS") issues.Add("the PC boots in legacy BIOS mode (no Secure Boot)");
        if (x.TpmVersion == "") issues.Add("no TPM security chip is active");
        else if (x.TpmVersion == "1.2") issues.Add("the TPM is version 1.2 (Windows 11 needs 2.0)");
        if (issues.Count == 0) yield break;
        yield return F(Severity.Info, "Hardware security features are off", char.ToUpperInvariant(issues[0][0]) + string.Join("; ", issues).Substring(1) + ".",
            "Turn on Secure Boot and the TPM (Intel PTT / AMD fTPM) in the BIOS setup. They protect against boot-level malware and are needed for Windows 11 and BitLocker.");
    }
}

/// <summary>STO-017: the system drive is encrypted; before repair work, the recovery key must be at hand.</summary>
public sealed class Sto017BitLocker : Check
{
    public override string Id => "STO-017";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var system = (s.Volumes?.Value ?? []).FirstOrDefault(v => v.IsSystem)?.Root.TrimEnd('\\') ?? "C:";
        if (Sec.Of(s).BitLocker.FirstOrDefault(v => v.Drive.Equals(system, StringComparison.OrdinalIgnoreCase)) is not { Encrypted: true } bl) yield break;
        yield return bl.Protection == 8
            ? F(Severity.Info, $"{system} is encrypted but not protected yet", "Device encryption is waiting for activation: the key isn't backed up and the drive isn't locked.",
                "Sign in with a Microsoft account (or turn on BitLocker in Settings > Privacy & security > Device encryption) so the key is saved and the protection switches on.")
            : F(Severity.Info, $"BitLocker is {bl.Status.ToLowerInvariant()} on {system}", "BIOS updates, motherboard swaps, TPM resets and moving the drive will ask for the 48-digit recovery key.",
                "Before repair work, find the recovery key (https://aka.ms/myrecoverykey for a Microsoft account, or the printout/USB key) or suspend BitLocker first. Without the key the data is gone.");
    }
}

/// <summary>SEC-008: the Guest account or the built-in Administrator enabled, accounts allowed an empty password.</summary>
public sealed class Sec008Accounts : Check
{
    public override string Id => "SEC-008";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var on = Sec.Of(s).Accounts.Where(a => !a.Disabled).ToList();
        if (on.Any(a => a.Rid == 501))
            yield return F(Severity.Warning, "The Guest account is enabled", "Anyone can sign in (or connect over the network) without a password.",
                "Disable it: Computer Management > Local Users and Groups > Users > Guest > Account is disabled (or run: net user guest /active:no).");
        if (on.Any(a => a.Rid == 500))
            yield return F(Severity.Warning, "The built-in Administrator account is enabled", "It's the account attackers try first, it can't be locked out, and it skips UAC prompts.",
                "Use a normal admin account instead and disable this one (net user administrator /active:no), unless it's needed for a repair right now.");
        var blank = on.Where(a => a.PasswordNotRequired && a.Rid != 501).ToList();
        if (blank.Count > 0)
            yield return F(Severity.Info, $"{N(blank.Count, "account is", "accounts are")} allowed an empty password", string.Join(", ", blank.Select(a => a.Name)) + ".",
                "Make sure each has a password (or a PIN/Windows Hello with a Microsoft account).");
    }
}

/// <summary>SEC-009: SMB1 installed (WannaCry's way in) and Remote Desktop open without Network Level Authentication.</summary>
public sealed class Sec009SmbRdp : Check
{
    public override string Id => "SEC-009";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = Sec.Of(s);
        var smb = new List<string>();
        if (x.Smb1Server == true) smb.Add("server (shares files with SMB1)");
        if (x.Smb1Client == true) smb.Add("client (connects to SMB1 shares)");
        if (smb.Count > 0)
            yield return F(Severity.Warning, "The old SMB1 file-sharing protocol is on", $"SMB1 {string.Join(" and ", smb)}.",
                "Turn it off in Windows Features (untick \"SMB 1.0/CIFS File Sharing Support\"). Only very old NAS drives and printers need it; update or replace those.");
        if (x.RdpEnabled == true)
        {
            var port = x.RdpPort is { } p && p != 3389 ? $" on port {p}" : "";
            yield return x.RdpNla == false
                ? F(Severity.Warning, "Remote Desktop is on without Network Level Authentication", $"Anyone who can reach this PC{port} gets the sign-in screen before proving who they are.",
                    "Settings > System > Remote Desktop > Advanced: require Network Level Authentication. Never forward port 3389 on the router.")
                : F(Severity.Info, "Remote Desktop is on", $"Remote Desktop accepts connections{port} (Network Level Authentication required).",
                    "Fine if you use it. Make sure the account passwords are strong and port 3389 isn't forwarded on the router.");
        }
    }
}

/// <summary>NET-011: Windows Firewall switched off with no other firewall, and remote-access ports listening.</summary>
public sealed class Net011Firewall : Check
{
    public override string Id => "NET-011";
    public override Category Category => Category.Network;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var x = Sec.Of(s);
        if (x.Firewalls.Count > 0) yield break;  // a third-party firewall is in charge
        var off = new List<string>();
        if (x.FirewallPublic == false) off.Add("public");
        if (x.FirewallPrivate == false) off.Add("private");
        if (x.FirewallDomain == false) off.Add("domain");
        if (off.Count == 0) yield break;
        var open = x.ListeningPorts.Where(p => p is not 445).Select(p => p switch { 3389 => "Remote Desktop 3389", 5900 => "VNC 5900", 5938 => "TeamViewer 5938", 22 => "SSH 22", 23 => "Telnet 23", 21 => "FTP 21", _ => p.ToString() }).ToList();
        yield return F(Severity.Warning, $"Windows Firewall is off for {string.Join(", ", off)} networks",
            $"No other firewall is registered.{(open.Count > 0 ? $" Listening: {string.Join(", ", open)}." : "")}",
            "Turn it back on: Windows Security > Firewall & network protection. Public networks (cafes, hotels) matter most.");
    }
}

/// <summary>SEC-010: scheduled tasks that run encoded or downloading scripts (a common way malware survives restarts).</summary>
public sealed class Sec010ScriptTasks : Check
{
    public override string Id => "SEC-010";
    public override Category Category => Category.Security;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Security)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var t = Sec.Of(s).SuspiciousTasks;
        if (t.Count == 0) yield break;
        yield return F(Severity.Warning, $"{N(t.Count, "scheduled task runs", "scheduled tasks run")} suspicious scripts",
            string.Join("; ", t.Take(4).Select(x => $"{x.Name} ({x.Reason}): {Cut(x.Command)}")) + ".",
            "Look the task up in Task Scheduler. Unless it belongs to software you trust, disable it, delete the files it points to, and run Microsoft Defender Offline.");
    }

    private static string Cut(string c) => c.Length > 160 ? c.Substring(0, 160) + "..." : c;
}
