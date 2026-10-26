using System.Text.RegularExpressions;

namespace Troubleshootr.Core;

/// <summary>Antivirus, Defender, firewall, UAC, accounts, encryption, remote access and the usual malware footholds.</summary>
public sealed class SecurityInfo
{
    /// <summary>False on Windows Server, which has no Security Center: antivirus checks don't apply.</summary>
    public bool SecurityCenter { get; set; }
    public List<AvProduct> Antivirus { get; set; } = new();
    /// <summary>Third-party firewalls registered with Security Center and switched on.</summary>
    public List<string> Firewalls { get; set; } = new();
    public DefenderStatus? Defender { get; set; }
    public List<ThreatDetection> Threats { get; set; } = new();

    /// <summary>Windows Firewall per profile (policy settings win over local ones). Null = couldn't be read.</summary>
    public bool? FirewallDomain { get; set; }
    public bool? FirewallPrivate { get; set; }
    public bool? FirewallPublic { get; set; }

    /// <summary>EnableLUA: false = UAC is off (every program runs with full rights).</summary>
    public bool? UacOn { get; set; }
    /// <summary>ConsentPromptBehaviorAdmin: 0 = elevate without asking.</summary>
    public int? UacAdminPrompt { get; set; }

    public string? TpmVersion { get; set; }
    public bool TpmPresent { get; set; }
    public List<BitLockerVolume> BitLocker { get; set; } = new();

    public List<LocalAccount> Accounts { get; set; } = new();

    /// <summary>SMB1 client driver (mrxsmb10) installed and not disabled.</summary>
    public bool? Smb1Client { get; set; }
    /// <summary>SMB1 server enabled (LanmanServer SMB1 value, or the srv.sys driver present).</summary>
    public bool? Smb1Server { get; set; }
    public bool? RdpEnabled { get; set; }
    public bool? RdpNla { get; set; }
    public int? RdpPort { get; set; }
    /// <summary>TCP ports listening on all addresses that remote-access and file-sharing services use.</summary>
    public List<int> ListeningPorts { get; set; } = new();

    public List<BrowserPolicy> BrowserPolicies { get; set; } = new();
    /// <summary>Known remote-access tools running right now (process names).</summary>
    public List<string> RemoteToolsRunning { get; set; } = new();
    /// <summary>Scheduled tasks (outside \Microsoft) whose command looks like script-based persistence.</summary>
    public List<SuspiciousTask> SuspiciousTasks { get; set; } = new();

    public static readonly int[] WatchedPorts = [21, 22, 23, 445, 3389, 5900, 5938];
}

public sealed class AvProduct
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool UpToDate { get; set; }
    /// <summary>Security Center productState, kept for reference.</summary>
    public int State { get; set; }

    public bool IsDefender => Name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0 && Name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                              || Name.Equals("Microsoft Defender Antivirus", StringComparison.OrdinalIgnoreCase);

    /// <summary>Security Center's productState: bits 12-15 = on (1) / off / snoozed, bits 4-7 = signatures out of date (1).</summary>
    public static (bool Enabled, bool UpToDate) Decode(int state) => ((state >> 12 & 0xF) == 1, (state >> 4 & 0xF) == 0);
}

public sealed class DefenderStatus
{
    /// <summary>"Normal", "Passive Mode", "SxS Passive Mode", "EDR Block Mode", "Not running".</summary>
    public string? RunningMode { get; set; }
    public bool AntivirusEnabled { get; set; }
    public bool RealTimeProtection { get; set; }
    public bool? TamperProtection { get; set; }
    public DateTime? SignaturesUpdatedUtc { get; set; }
    public int? SignatureAgeDays { get; set; }

    public bool IsPrimary => RunningMode == "Normal" && AntivirusEnabled;
}

public sealed class ThreatDetection
{
    public string Name { get; set; } = "";
    /// <summary>1 low, 2 moderate, 4 high, 5 severe.</summary>
    public int Severity { get; set; }
    public DateTime? DetectedUtc { get; set; }
    /// <summary>Defender says the threat is still present.</summary>
    public bool Active { get; set; }
    /// <summary>The removal or quarantine worked.</summary>
    public bool? Removed { get; set; }
    [FreeText] public string? Where { get; set; }

    /// <summary><see cref="Where"/> without Defender's resource-type prefix ("file:_C:\..." becomes "C:\...").</summary>
    public string? Location => Where is null ? null : Regex.Replace(Where, @"^\w+:_", "");
}

public sealed class BitLockerVolume
{
    public string Drive { get; set; } = "";
    /// <summary>Shell System.Volume.BitLockerProtection: 1 on, 2 off, 3 encrypting, 4 decrypting, 5 suspended, 6 on (locked), 8 waiting for activation.</summary>
    public int Protection { get; set; }

    public string Status => Protection switch
    {
        1 => "On", 2 => "Off", 3 => "Encrypting", 4 => "Decrypting", 5 => "Suspended", 6 => "On (locked)", 8 => "Encrypted, waiting for activation",
        0 => "Not supported", var p => $"Unknown ({p})",
    };

    public bool Encrypted => Protection is 1 or 3 or 5 or 6 or 8;
}

public sealed class LocalAccount
{
    [Sensitive] public string Name { get; set; } = "";
    /// <summary>Relative id: 500 = built-in Administrator, 501 = Guest.</summary>
    public int Rid { get; set; }
    public bool Admin { get; set; }
    public bool Disabled { get; set; }
    /// <summary>UF_PASSWD_NOTREQD: the account may have an empty password.</summary>
    public bool PasswordNotRequired { get; set; }
    public DateTime? LastLogonUtc { get; set; }
}

public sealed class BrowserPolicy
{
    /// <summary>"Chrome", "Edge", "Firefox", "Brave".</summary>
    public string Browser { get; set; } = "";
    /// <summary>"Forced extensions", "Home page", "Search engine", "Startup pages".</summary>
    public string Kind { get; set; } = "";
    [FreeText] public string Value { get; set; } = "";
}

public sealed class SuspiciousTask
{
    [FreeText] public string Name { get; set; } = "";
    [FreeText] public string Command { get; set; } = "";
    /// <summary>Why it looks suspicious ("encoded PowerShell", "script from a temp folder").</summary>
    public string Reason { get; set; } = "";
}

/// <summary>Remote-access tools scammers ask people to install (and technicians use). Matched by process and program name.</summary>
public static class RemoteTools
{
    public static readonly (string Name, string[] Processes, string[] Programs)[] Known =
    [
        ("AnyDesk", ["anydesk"], ["AnyDesk"]),
        ("TeamViewer", ["teamviewer", "teamviewer_service", "tv_w32", "tv_x64"], ["TeamViewer"]),
        ("RustDesk", ["rustdesk"], ["RustDesk"]),
        ("UltraViewer", ["ultraviewer_desktop", "ultraviewer_service"], ["UltraViewer"]),
        ("Supremo", ["supremo", "supremoservice"], ["Supremo"]),
        ("Splashtop", ["srmanager", "srservice", "strwinclt"], ["Splashtop"]),
        ("LogMeIn / GoTo", ["logmein", "lmiguardiansvc", "gotoassist", "g2ax_comm_expert"], ["LogMeIn", "GoTo Resolve", "GoToAssist"]),
        ("ScreenConnect (ConnectWise)", ["screenconnect.clientservice", "screenconnect.windowsclient"], ["ScreenConnect", "ConnectWise Control"]),
        ("Chrome Remote Desktop", ["remoting_host"], ["Chrome Remote Desktop Host"]),
        ("Ammyy Admin", ["aa_v3", "ammyy_admin"], ["Ammyy"]),
        ("Remote Utilities", ["rutserv", "rfusclient"], ["Remote Utilities"]),
        ("NetSupport", ["client32"], ["NetSupport"]),
        ("Atera", ["ateraagent"], ["AteraAgent"]),
        ("Zoho Assist", ["zohoassist", "za_connect"], ["Zoho Assist"]),
        ("VNC", ["winvnc", "tvnserver", "vncserver"], ["RealVNC", "TightVNC", "UltraVNC", "VNC Server"]),
        ("DWService", ["dwagsvc", "dwagent"], ["DWAgent"]),
    ];

    /// <summary>Each known tool that's installed or running, with its install date.</summary>
    public static List<(string Name, InstalledProgram? Program, bool Running)> Find(IEnumerable<InstalledProgram> programs, ICollection<string> running)
    {
        var list = programs.ToList();
        return Known.Select(t => (t.Name, Program: (InstalledProgram?)list.FirstOrDefault(p => t.Programs.Any(n => p.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)), Running: running.Contains(t.Name)))
            .Where(x => x.Program is not null || x.Running).ToList();
    }
}

/// <summary>Command lines that malware uses to persist: encoded or downloading PowerShell, script hosts on scripts in temp folders, LOLBins.</summary>
public static class SuspiciousCommand
{
    private static readonly (Regex Pattern, string Reason)[] Rules =
    [
        (new(@"powershell(\.exe)?\b.*\s-(e|ec|en|enc|enco|encod|encode|encodedcommand)\s+[A-Za-z0-9+/=]{20,}", RegexOptions.IgnoreCase), "encoded PowerShell command"),
        (new(@"(FromBase64String|DownloadString|DownloadFile|Invoke-WebRequest|\biwr\b|Net\.WebClient|\biex\b|Invoke-Expression)", RegexOptions.IgnoreCase), "PowerShell that downloads or runs hidden code"),
        (new(@"\bmshta(\.exe)?\b", RegexOptions.IgnoreCase), "mshta (runs web scripts)"),
        (new(@"\b(w|c)script(\.exe)?\b.*(\\temp\\|\\appdata\\|\\programdata\\|\\users\\public\\)", RegexOptions.IgnoreCase), "script from a user or temp folder"),
        (new(@"\bregsvr32(\.exe)?\b.*/i:\s*https?:", RegexOptions.IgnoreCase), "regsvr32 loading a script from the internet"),
        (new(@"\brundll32(\.exe)?\b.*(javascript:|\\temp\\|\\appdata\\|\\users\\public\\)", RegexOptions.IgnoreCase), "rundll32 running code from a temp folder or script"),
        (new(@"\bcertutil(\.exe)?\b.*-urlcache", RegexOptions.IgnoreCase), "certutil downloading a file"),
    ];

    public static string? Why(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : Rules.FirstOrDefault(r => r.Pattern.IsMatch(command)).Reason;
}
