using Troubleshootr.Checks.Crashes;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Drivers;

/// <summary>DRV-001: devices with a Device Manager problem (the yellow "!"). Missing drivers are DRV-002; disabled ones only an Info.</summary>
public sealed class Drv001ProblemDevices : Check
{
    public override string Id => "DRV-001";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Devices)];

    // Virtual network adapters (VPNs, WAN miniports) failing doesn't take the PC offline.
    internal static readonly string[] VirtualNet = ["WAN Miniport", "TAP", "Virtual", "VPN", "Personal Area Network", "Wintun", "WireGuard", "Tailscale", "Hyper-V"];

    private static bool Essential(DeviceEntry d) =>
        DevicesInfo.EssentialClasses.Contains(d.Class ?? "", StringComparer.OrdinalIgnoreCase)
        && !(d.Class == "Net" && VirtualNet.Any(v => d.Name.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0));

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var devices = s.Devices!.Value!.Devices;
        var broken = devices.Where(d => d.ProblemCode is not (0 or DeviceProblems.Disabled or DeviceProblems.NoDriver or DeviceProblems.NotPresent)).ToList();
        if (broken.Count > 0)
            yield return F(broken.Any(Essential) ? Severity.Critical : Severity.Warning,
                broken.Count == 1 ? $"A device isn't working: {broken[0].Name}" : $"{broken.Count} devices aren't working",
                string.Join("; ", broken.Take(5).Select(d => $"{d.Name} ({d.Class ?? "no class"}) {DeviceProblems.Meaning(d.ProblemCode)} (code {d.ProblemCode})")) + (broken.Count > 5 ? "; …" : "") + ".",
                "Open the Devices tab. Reinstall the device's driver from the PC or part maker (not Windows Update's generic one), restart, and for code 10/43 also try another port or cable.");

        var disabled = devices.Where(d => d.ProblemCode == DeviceProblems.Disabled).ToList();
        if (disabled.Count > 0)
            yield return F(Severity.Info, disabled.Count == 1 ? $"A device is disabled: {disabled[0].Name}" : $"{disabled.Count} devices are disabled",
                string.Join("; ", disabled.Take(6).Select(d => $"{d.Name} ({d.Class})")) + ".",
                "Fine if done on purpose. If something like the webcam, Wi-Fi or sound is missing, enable it in Device Manager.");
    }
}

/// <summary>DRV-002: devices with no driver ("Unknown device"), named by their hardware id's vendor where possible.</summary>
public sealed class Drv002MissingDrivers : Check
{
    public override string Id => "DRV-002";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Devices)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var missing = s.Devices!.Value!.Devices.Where(d => d.ProblemCode == DeviceProblems.NoDriver).ToList();
        if (missing.Count == 0) yield break;
        yield return F(Severity.Warning, missing.Count == 1 ? "A device has no driver" : $"{missing.Count} devices have no driver",
            string.Join("; ", missing.Take(5).Select(d => $"{d.Name}{(d.Vendor is { } v ? $" made by {v}" : "")} [{d.HardwareId ?? d.InstanceId}]")) + ".",
            "Install the chipset and device drivers from the PC or motherboard maker's support page. Search the hardware id in brackets to identify anything else.");
    }
}

/// <summary>DRV-004: Windows is set to load unsigned drivers (test-signing or integrity checks off): a security risk and a sign of tampering or cheat tools.</summary>
public sealed class Drv004DriverSigning : Check
{
    public override string Id => "DRV-004";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Devices)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var d = s.Devices!.Value!;
        var modes = new[] { d.TestSigning == true ? "test-signing mode is on" : null, d.IntegrityChecksOff == true ? "driver signature checks are turned off" : null }
            .Where(x => x is not null).ToList();
        if (modes.Count == 0) yield break;
        yield return F(Severity.Warning, "Windows is allowing unsigned drivers", $"Boot options: {string.Join("; ", modes)}.",
            "Unless a developer set this on purpose, turn it off as admin ('bcdedit /set testsigning off' and 'bcdedit /deletevalue nointegritychecks') and restart. Anti-cheat games refuse to run in this mode.");
    }
}

/// <summary>DRV-005: network, storage and display drivers from the maker that are very old.</summary>
public sealed class Drv005OldDrivers : Check
{
    private const int Years = 5;
    private static readonly string[] Classes = ["Net", "SCSIAdapter", "HDC", "Display"];

    public override string Id => "DRV-005";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Devices)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var old = s.Devices!.Value!.Devices
            .Where(d => Classes.Contains(d.Class ?? "", StringComparer.OrdinalIgnoreCase) && d.ProblemCode == 0
                        && d.DriverDate is { } date && (s.Meta.CollectedUtc - date).TotalDays > Years * 365.25
                        && d.DriverProvider is { } p && p.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0)
            .Where(d => !(d.Class == "Net" && Drv001ProblemDevices.VirtualNet.Any(v => d.Name.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)))
            .OrderBy(d => d.DriverDate).ToList();
        if (old.Count == 0) yield break;
        yield return F(Severity.Info, old.Count == 1 ? $"An old driver: {old[0].Name}" : "Some important drivers are very old",
            string.Join("; ", old.Take(4).Select(d => $"{d.Name}: {d.DriverProvider} {d.DriverVersion} from {d.DriverDate:yyyy-MM-dd}")) + ".",
            "Check the PC or part maker's support page for newer network, storage and graphics drivers, especially if those devices misbehave.");
    }
}

/// <summary>DRV-006: kernel drivers on Microsoft's vulnerable-driver blocklist that tools commonly leave behind.</summary>
public sealed class Drv006KnownBadDrivers : Check
{
    public override string Id => "DRV-006";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Devices)];

    /// <summary>File name → what installs it. All are on Microsoft's recommended driver block rules (vulnerable drivers attackers abuse).</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WinRing0x64.sys"] = "WinRing0 (old fan control, RGB and monitoring tools)",
        ["WinRing0.sys"] = "WinRing0 (old fan control, RGB and monitoring tools)",
        ["RTCore64.sys"] = "an old MSI Afterburner / RivaTuner",
        ["gdrv.sys"] = "Gigabyte App Center / RGB Fusion",
        ["AsrDrv10.sys"] = "ASRock utilities", ["AsrDrv101.sys"] = "ASRock utilities", ["AsrDrv102.sys"] = "ASRock utilities", ["AsrDrv103.sys"] = "ASRock utilities",
        ["AsIO.sys"] = "ASUS AI Suite / Armoury Crate", ["AsIO2.sys"] = "ASUS AI Suite / Armoury Crate", ["AsIO3.sys"] = "ASUS AI Suite / Armoury Crate",
        ["dbutil_2_3.sys"] = "Dell BIOS update utilities",
        ["ene.sys"] = "ENE RGB lighting control",
        ["speedfan.sys"] = "SpeedFan",
        ["phymemx64.sys"] = "a hardware-access tool",
        ["atillk64.sys"] = "an old AMD/ATI overclocking tool",
        ["cpuz141_x64.sys"] = "an old CPU-Z",
    };

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var found = s.Devices!.Value!.KernelDrivers.Where(k => k.File is { } f && Known.ContainsKey(f) && k.Start != 4).ToList();
        if (found.Count == 0) yield break;
        yield return F(Severity.Warning, "A driver with a known security hole is installed",
            string.Join("; ", found.Select(k => $"{k.File} from {Known[k.File!]}")) + ".",
            "Update or uninstall the program that installed it (newer versions use safe drivers). Malware uses these drivers to switch off antivirus, and Windows' Vulnerable Driver Blocklist will stop them loading.");
    }
}

/// <summary>DRV-008: drivers that fail to load or devices that fail to start (Kernel-PnP 219 and 411).</summary>
public sealed class Drv008DriverLoadFailures : Check
{
    private const int Repeated = 3;

    public override string Id => "DRV-008";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var failures = s.Events!.Value!.Find("Microsoft-Windows-Kernel-PnP", 219, 411).Within(s, Ev.Window)
            // WUDFRd (the user-mode driver host) logs 219 at boot for sensors and portable devices that then start fine.
            .Where(e => (e.Get("FailureName") ?? "").IndexOf("WUDFRd", StringComparison.OrdinalIgnoreCase) < 0)
            .Select(e => e.Get("DeviceInstanceId") ?? e.Get("DriverName") ?? "")
            .Where(id => id.Length > 0)
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= Repeated).OrderByDescending(g => g.Count()).ToList();
        if (failures.Count == 0) yield break;
        yield return F(Severity.Warning, "A driver keeps failing to load",
            string.Join("; ", failures.Take(4).Select(g => $"{g.Key} {g.Count()} times")) + " in 30 days.",
            "Find the device in the Devices tab (or search the id), then reinstall or update its driver. If it's for hardware that's gone, uninstall the device.");
    }
}

/// <summary>DRV-010: leftover drivers for a graphics card that's no longer in the PC (a common cause of display problems after a GPU swap).</summary>
public sealed class Drv010GhostDevices : Check
{
    public override string Id => "DRV-010";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Devices)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var d = s.Devices!.Value!;
        var current = new HashSet<string>(d.Devices.Where(x => x.Class == "Display").Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        // Only graphics cards (PCI): USB display adapters and docks come and go.
        var ghosts = d.Hidden.Where(x => x.Class == "Display" && (x.InstanceId ?? "").StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) && !current.Contains(x.Name)).Select(x => x.Name).Distinct().ToList();
        if (ghosts.Count == 0) yield break;
        yield return F(Severity.Info, "Drivers for an old graphics card are still installed",
            $"Not connected any more: {string.Join(", ", ghosts)}.",
            "If there are display problems, remove them with DDU (Display Driver Uninstaller) in Safe Mode, then reinstall the current card's driver.");
    }
}
