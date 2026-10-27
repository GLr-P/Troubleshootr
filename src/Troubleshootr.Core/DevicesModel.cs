namespace Troubleshootr.Core;

/// <summary>Device Manager's view of the PC, read through SetupAPI (much faster than WMI Win32_PnPEntity).</summary>
public sealed class DevicesInfo
{
    /// <summary>Devices that are connected now.</summary>
    public List<DeviceEntry> Devices { get; set; } = new();
    /// <summary>Hidden devices from hardware that's no longer connected ("ghosts"), only the classes that matter (display, network, storage).</summary>
    public List<DeviceEntry> Hidden { get; set; } = new();
    public int HiddenCount { get; set; }
    /// <summary>Kernel drivers registered as services (for known-bad drivers like WinRing0).</summary>
    public List<KernelDriver> KernelDrivers { get; set; } = new();
    /// <summary>Test-signing mode is on (bcdedit /set testsigning on): unsigned drivers can load.</summary>
    public bool? TestSigning { get; set; }
    /// <summary>Driver signature checks are disabled at boot (DISABLE_INTEGRITY_CHECKS / nointegritychecks).</summary>
    public bool? IntegrityChecksOff { get; set; }

    /// <summary>Device classes whose failure stops the PC working (Critical in DRV-001).</summary>
    public static readonly string[] EssentialClasses = ["Display", "Net", "DiskDrive", "SCSIAdapter", "HDC"];
}

public sealed class DeviceEntry
{
    public string Name { get; set; } = "";
    /// <summary>Setup class: Display, Net, USB, AudioEndpoint… Empty for unknown devices.</summary>
    public string? Class { get; set; }
    public string? InstanceId { get; set; }
    public string? HardwareId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Service { get; set; }
    /// <summary>Device Manager's problem code (the yellow "!"): 0 when fine. See <see cref="DeviceProblems"/>.</summary>
    public int ProblemCode { get; set; }
    public string? DriverProvider { get; set; }
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    public string? Inf { get; set; }

    /// <summary>Vendor from the hardware id (PCI VEN_xxxx, USB VID_xxxx), when the id is in the small built-in table.</summary>
    public string? Vendor => Vendors.FromHardwareId(HardwareId ?? InstanceId);
}

public sealed class KernelDriver
{
    public string Name { get; set; } = "";
    /// <summary>The .sys file name.</summary>
    public string? File { get; set; }
    /// <summary>0 boot, 1 system, 2 automatic, 3 manual (on demand), 4 disabled.</summary>
    public int? Start { get; set; }
}

/// <summary>Device Manager problem codes in plain English (learn.microsoft.com "Device Manager error messages").</summary>
public static class DeviceProblems
{
    public const int Disabled = 22;
    public const int NoDriver = 28;
    public const int NotPresent = 45;

    private static readonly Dictionary<int, string> Meanings = new()
    {
        [1] = "isn't configured correctly",
        [3] = "driver may be corrupted, or the PC is low on memory",
        [10] = "can't start",
        [12] = "can't find enough free resources",
        [14] = "needs a restart to work",
        [18] = "drivers need reinstalling",
        [19] = "registry settings are damaged",
        [21] = "is being removed by Windows",
        [22] = "is disabled",
        [24] = "isn't present, isn't working or doesn't have all its drivers",
        [28] = "has no driver installed",
        [29] = "is disabled in the BIOS/firmware",
        [31] = "isn't working: Windows can't load its driver",
        [32] = "has its driver service disabled",
        [37] = "driver failed to start",
        [38] = "driver couldn't load because an old copy is still loaded",
        [39] = "driver is corrupted or missing",
        [40] = "registry settings are damaged",
        [41] = "driver loaded but the hardware wasn't found",
        [43] = "was stopped because it reported problems",
        [44] = "was shut down by an app or service",
        [45] = "isn't connected",
        [47] = "was prepared for safe removal",
        [48] = "driver is blocked because it causes problems",
        [49] = "can't start because the system registry is too large",
        [50] = "can't apply all its settings",
        [51] = "is waiting for another device to start",
        [52] = "driver's digital signature can't be verified",
        [53] = "is reserved for the kernel debugger",
        [54] = "failed and is being reset",
        [56] = "is still being set up",
    };

    public static string Meaning(int code) => Meanings.TryGetValue(code, out var m) ? m : $"has problem code {code}";
}

/// <summary>A compact PCI/USB vendor table (only common vendors, to keep the app small), for naming unknown devices.</summary>
public static class Vendors
{
    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8086"] = "Intel", ["1022"] = "AMD", ["1002"] = "AMD/ATI", ["10DE"] = "NVIDIA", ["10EC"] = "Realtek", ["0BDA"] = "Realtek",
        ["14E4"] = "Broadcom", ["0A5C"] = "Broadcom", ["168C"] = "Qualcomm Atheros", ["17CB"] = "Qualcomm", ["0CF3"] = "Qualcomm Atheros",
        ["1969"] = "Qualcomm Atheros (Killer)", ["1B21"] = "ASMedia", ["1B4B"] = "Marvell", ["14C3"] = "MediaTek", ["0E8D"] = "MediaTek",
        ["1814"] = "Ralink/MediaTek", ["148F"] = "Ralink/MediaTek", ["1912"] = "Renesas", ["104C"] = "Texas Instruments", ["1180"] = "Ricoh",
        ["1217"] = "O2 Micro", ["197B"] = "JMicron", ["152D"] = "JMicron", ["1106"] = "VIA", ["2109"] = "VIA Labs", ["1D6A"] = "Aquantia",
        ["144D"] = "Samsung", ["04E8"] = "Samsung", ["15B7"] = "Western Digital/SanDisk", ["1C5C"] = "SK hynix", ["1344"] = "Micron",
        ["1987"] = "Phison", ["126F"] = "Silicon Motion", ["1E0F"] = "Kioxia", ["1179"] = "Toshiba/Kioxia", ["1CC1"] = "ADATA",
        ["8087"] = "Intel", ["046D"] = "Logitech", ["045E"] = "Microsoft", ["1532"] = "Razer", ["1B1C"] = "Corsair", ["0951"] = "Kingston",
        ["0781"] = "SanDisk", ["05AC"] = "Apple", ["04F2"] = "Chicony", ["5986"] = "Bison (webcam)", ["13D3"] = "IMC Networks", ["0489"] = "Foxconn",
        ["04CA"] = "Lite-On", ["06CB"] = "Synaptics", ["04F3"] = "Elan", ["27C6"] = "Goodix", ["138A"] = "Validity (fingerprint)",
        ["0711"] = "Magic Control (USB display adapter)", ["17E9"] = "DisplayLink", ["1A86"] = "QinHeng (CH340 serial)", ["0403"] = "FTDI", ["067B"] = "Prolific", ["10C4"] = "Silicon Labs", ["0B05"] = "ASUS",
        ["1043"] = "ASUS", ["1462"] = "MSI", ["1458"] = "Gigabyte", ["03F0"] = "HP", ["103C"] = "HP", ["04A9"] = "Canon", ["04B8"] = "Epson",
        ["04F9"] = "Brother", ["413C"] = "Dell", ["1028"] = "Dell", ["17AA"] = "Lenovo", ["0C45"] = "Microdia (webcam)", ["1D50"] = "OpenMoko",
        ["054C"] = "Sony", ["057E"] = "Nintendo", ["28DE"] = "Valve", ["1050"] = "Yubico", ["2357"] = "TP-Link", ["0846"] = "Netgear",
        ["15AD"] = "VMware", ["80EE"] = "VirtualBox", ["1414"] = "Microsoft (Hyper-V)", ["1AF4"] = "Red Hat (VirtIO)",
    };

    private static readonly System.Text.RegularExpressions.Regex Id = new(@"(?:VEN|VID)_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string? FromHardwareId(string? id) =>
        id is not null && Id.Match(id) is { Success: true } m && Ids.TryGetValue(m.Groups[1].Value, out var name) ? name : null;
}

/// <summary>Monitors and how they're driven (EnumDisplayDevices / EnumDisplaySettings), plus per-app GPU choices.</summary>
public sealed class DisplaysInfo
{
    public List<DisplayInfo> Displays { get; set; } = new();
    /// <summary>Apps set to a GPU in Settings > Display > Graphics (HKCU UserGpuPreferences): app path → 0 let Windows decide, 1 power saving, 2 high performance.</summary>
    [FreeText] public List<string> PowerSavingApps { get; set; } = new();
}

public sealed class DisplayInfo
{
    /// <summary>The GPU driving this display.</summary>
    public string? Adapter { get; set; }
    public string? Monitor { get; set; }
    public bool Primary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
    /// <summary>Highest refresh rate the display offers at this resolution.</summary>
    public int MaxRefreshHz { get; set; }
    public int BitsPerPixel { get; set; }

    // From the monitor's EDID (PER-004); zero/null when the monitor didn't send one.
    public string? MonitorMaker { get; set; }
    public string? MonitorModel { get; set; }
    public int MonitorYear { get; set; }
    public double SizeInches { get; set; }
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }

    /// <summary>"Acer V226HQL", or "AU Optronics" for a laptop panel with no model name, or Windows' generic name.</summary>
    public string DisplayName =>
        MonitorModel is { } m ? (MonitorMaker is { } maker && !m.StartsWith(maker.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? $"{maker} {m}" : m)
        : MonitorMaker ?? Monitor ?? "";
}

/// <summary>Windows Update: install history, policies and upgrade blocks.</summary>
public sealed class UpdatesInfo
{
    /// <summary>Newest first (the Windows Update agent's own history, which includes Store and Defender updates).</summary>
    public List<UpdateEntry> History { get; set; } = new();
    public string? HistoryUnavailable { get; set; }
    public UpdatePolicy Policy { get; set; } = new();
    /// <summary>Feature-update readiness per target version (TargetVersionUpgradeExperienceIndicators).</summary>
    public List<UpgradeBlock> UpgradeBlocks { get; set; } = new();
}

public sealed class UpdateEntry
{
    public DateTime DateUtc { get; set; }
    public string Title { get; set; } = "";
    /// <summary>1 in progress, 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted.</summary>
    public int Result { get; set; }
    /// <summary>The error code (HRESULT) when it failed.</summary>
    public int HResult { get; set; }
    /// <summary>1 install, 2 uninstall.</summary>
    public int Operation { get; set; } = 1;

    public bool Succeeded => Result is 2 or 3;
    public bool Failed => Result is 4 or 5;

    /// <summary>Defender definitions and Store apps update constantly; they don't show whether Windows itself is patched.</summary>
    public bool IsWindowsUpdate =>
        Title.IndexOf("Security Intelligence Update", StringComparison.OrdinalIgnoreCase) < 0
        && Title.IndexOf("Definition Update", StringComparison.OrdinalIgnoreCase) < 0
        && Title.IndexOf("Antimalware", StringComparison.OrdinalIgnoreCase) < 0
        && !System.Text.RegularExpressions.Regex.IsMatch(Title, @"^9[A-Z0-9]{11}-"); // Store app package ids

    public string? Kb => System.Text.RegularExpressions.Regex.Match(Title, @"KB\d{6,8}") is { Success: true } m ? m.Value : null;
}

public sealed class UpdatePolicy
{
    /// <summary>A WSUS server set by policy (a managed/work PC). It can name the employer's server, so it's redacted in fixtures.</summary>
    [Sensitive] public string? WsusServer { get; set; }
    public bool UsesWsus { get; set; }
    /// <summary>Policy NoAutoUpdate = 1: automatic updates are off.</summary>
    public bool AutoUpdateOff { get; set; }
    /// <summary>Policy blocks Windows Update entirely (DoNotConnectToWindowsUpdateInternetLocations / DisableWindowsUpdateAccess).</summary>
    public bool WindowsUpdateBlocked { get; set; }
    public DateTime? PausedUntilUtc { get; set; }
    /// <summary>Pinned to a feature version by policy (TargetReleaseVersionInfo, e.g. "23H2").</summary>
    public string? TargetVersion { get; set; }
}

public sealed class UpgradeBlock
{
    /// <summary>"GE25H2" style key: the feature update this applies to.</summary>
    public string Target { get; set; } = "";
    /// <summary>"None" when nothing blocks it; otherwise e.g. "SystemDriveSize", "CompatBlock", "TPM", "CPU".</summary>
    public string? RedReason { get; set; }
    /// <summary>A Microsoft safeguard hold id, if any.</summary>
    public string? GatedBlockId { get; set; }
}

/// <summary>Common Windows Update error codes (learn.microsoft.com "Windows Update error codes").</summary>
public static class UpdateErrors
{
    private static readonly Dictionary<uint, string> Codes = new()
    {
        [0x80070002] = "files missing from the update download (clear the SoftwareDistribution folder)",
        [0x80070003] = "a path is missing (damaged update cache)",
        [0x80070005] = "access denied",
        [0x8007000E] = "out of memory",
        [0x80070070] = "not enough disk space",
        [0x80070643] = "the installer failed (often the WinRE partition is too small for KB5034441-type updates)",
        [0x800705B4] = "timed out",
        [0x80073712] = "component store damaged (run DISM RestoreHealth)",
        [0x800F081F] = "component store files missing (run DISM RestoreHealth)",
        [0x800F0922] = "couldn't finish (not enough space on the System Reserved partition, or a VPN/proxy blocked it)",
        [0x800F0988] = "component store needs cleanup (DISM StartComponentCleanup)",
        [0x8024000B] = "the operation was cancelled",
        [0x80240016] = "another installation was already running (usually succeeds on retry)",
        [0x8024001E] = "the operation was cancelled (shutdown or service stopped)",
        [0x80242016] = "the update was interrupted (usually succeeds on retry)",
        [0x8024200D] = "the download was incomplete",
        [0x8024402C] = "can't reach the update server (proxy or DNS)",
        [0x8024401C] = "the connection timed out",
        [0x80244022] = "the update server is unavailable",
        [0x80246007] = "the download failed",
        [0x80248007] = "the update database is damaged",
        [0xC1900101] = "a driver blocked the upgrade (rolled back)",
        [0xC1900208] = "an incompatible app blocks the upgrade",
        [0xC190020E] = "not enough disk space for the upgrade",
    };

    public static string Describe(int hresult) => $"0x{(uint)hresult:X8}" + (Codes.TryGetValue((uint)hresult, out var m) ? $" ({m})" : "");
}
