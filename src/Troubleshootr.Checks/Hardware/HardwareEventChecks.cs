using System.Text.RegularExpressions;
using Troubleshootr.Checks.Crashes;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Hardware;

/// <summary>STO-007: disk and file-system errors in the System log.</summary>
public sealed class Sto007DiskErrors : Check
{
    private const int RecentDays = 7;
    private const int RepeatedTimeouts = 3; // a single controller reset happens (sleep, a USB drive pulled)

    public override string Id => "STO-007";
    public override Category Category => Category.Storage;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    private static readonly Regex DiskNumber = new(@"Harddisk(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var ev = s.Events!.Value!;
        var badBlocks = ev.Find("disk", 7).Within(s, Ev.Window).ToList();
        var timeouts = ev.Find("disk", 11, 153).Concat(ev.Find("storahci", 129)).Concat(ev.Find("stornvme", 11, 129)).Within(s, Ev.Window).ToList();
        var paging = ev.Find("disk", 51).Within(s, Ev.Window).ToList();
        var ntfs = ev.Find("Ntfs", 55, 98).Concat(ev.Find("Microsoft-Windows-Ntfs", 55, 98)).Where(e => e.Level is >= 1 and <= 3).Within(s, Ev.Window).ToList();

        var parts = new List<string>();
        var severity = Severity.Ok;
        void Raise(Severity x) { if (x > severity) severity = x; }
        if (badBlocks.Count > 0)
        {
            var disks = Disks(badBlocks);
            // A bad block on an internal drive this week is near-certain trouble; on a USB stick or an old event, less so.
            var internalRecent = badBlocks.Any(e => (s.Meta.CollectedUtc - e.TimeUtc).TotalDays <= RecentDays && !IsUsb(s, e));
            Raise(internalRecent ? Severity.Critical : Severity.Warning);
            parts.Add($"bad blocks {Ev.Times(badBlocks.Count)}{disks}");
        }
        if (timeouts.Count >= RepeatedTimeouts) { Raise(Severity.Warning); parts.Add($"timeouts or resets {timeouts.Count} times{Disks(timeouts)}"); }
        if (paging.Count >= RepeatedTimeouts) { Raise(Severity.Warning); parts.Add($"paging errors {paging.Count} times{Disks(paging)}"); }
        if (ntfs.Count > 0)
        {
            Raise(Severity.Warning);
            var drives = ntfs.Select(e => e.Get("DriveName")).Where(d => d is { Length: > 0 }).Distinct().ToList();
            parts.Add($"file system errors reported {Ev.Times(ntfs.Count)}{(drives.Count > 0 ? $" on {string.Join(", ", drives)}" : "")}");
        }
        if (severity == Severity.Ok) yield break;
        yield return F(severity, badBlocks.Count > 0 ? "A drive reported bad blocks" : "Drive errors in the event log",
            $"In the last 30 days: {string.Join("; ", parts)}.",
            badBlocks.Count > 0
                ? "Back up that drive now and check its health on the Storage tab; bad blocks usually mean the drive is failing."
                : "Check the drive's health on the Storage tab, reseat or replace its cable (desktops), update the storage/chipset driver and SSD firmware, and run chkdsk if corruption was reported.");
    }

    private static string Disks(IEnumerable<LogEvent> events)
    {
        var numbers = events.Select(e => DiskNumber.Match(e.Message ?? "")).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n).ToList();
        return numbers.Count == 0 ? "" : $" (disk {string.Join(", ", numbers)})";
    }

    private static bool IsUsb(Snapshot s, LogEvent e) =>
        DiskNumber.Match(e.Message ?? "") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n)
        && s.Storage?.Value?.Disks.FirstOrDefault(d => d.Number == n) is { } disk
        && string.Equals(disk.BusType, "USB", StringComparison.OrdinalIgnoreCase);
}

/// <summary>WHEA hardware-error events, split by component (the message names it: "Component: Memory", "Processor Core"…).</summary>
internal static class Whea
{
    public const string Provider = "Microsoft-Windows-WHEA-Logger";

    public static bool IsFatal(LogEvent e) => e.Id is 1 or 18 || e.Level == 1;
    public static bool IsMemory(LogEvent e) => e.Id == 47 || (e.Message ?? "").IndexOf("Component: Memory", StringComparison.OrdinalIgnoreCase) >= 0;
    public static bool IsPcie(LogEvent e) => e.Id == 17 || (e.Message ?? "").IndexOf("PCI Express", StringComparison.OrdinalIgnoreCase) >= 0;

    public static string Kind(LogEvent e) =>
        Regex.Match(e.Message ?? "", @"Error Type:\s*([^.\r\n]+?)(?:\s+Processor APIC ID|\s+The details|$)") is { Success: true } m ? m.Groups[1].Value.Trim() : $"event {e.Id}";
}

/// <summary>MEM-007: the hardware reported memory errors (corrected ECC, or worse).</summary>
public sealed class Mem007MemoryErrors : Check
{
    public override string Id => "MEM-007";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var events = s.Events!.Value!.Find(Whea.Provider).Within(s, Ev.Window).Where(Whea.IsMemory).ToList();
        if (events.Count == 0) yield break;
        var fatal = events.Count(Whea.IsFatal);
        yield return F(fatal > 0 ? Severity.Critical : Severity.Warning,
            fatal > 0 ? "The RAM had an uncorrectable error" : "The RAM reported corrected errors",
            $"{N(events.Count, "WHEA memory error event")} in 30 days{(fatal > 0 ? $", {fatal} uncorrectable" : "")}.",
            "Test the RAM (MemTest86, several passes), remove memory overclocks/XMP, and reseat or replace the module that fails.");
    }
}

/// <summary>MEM-008: Windows Memory Diagnostic found errors (1102/1202 are the "errors detected" results).</summary>
public sealed class Mem008MemoryDiagnostic : Check
{
    public override string Id => "MEM-008";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var bad = s.Events!.Value!.Find("Microsoft-Windows-MemoryDiagnostics-Results").Within(s, Ev.Window)
            .Where(e => e.Id is 1102 or 1202 || e.Level is 1 or 2).ToList();
        if (bad.Count == 0) yield break;
        yield return F(Severity.Critical, "Windows Memory Diagnostic found RAM errors",
            $"Memory test failed on {bad[0].TimeUtc.ToLocalTime():yyyy-MM-dd}{(bad.Count > 1 ? $" ({bad.Count} failed results)" : "")}.",
            "A RAM module is faulty. Test the sticks one at a time to find it, then replace it; until then expect crashes and corrupted files.");
    }
}

/// <summary>CPU-005: firmware is holding the processor's speed down (Kernel-Processor-Power 37): heat, power limits or a power plan.</summary>
public sealed class Cpu005Throttling : Check
{
    private const int DesktopWarning = 5;

    public override string Id => "CPU-005";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var events = s.Events!.Value!.Find("Microsoft-Windows-Kernel-Processor-Power", 37).Within(s, Ev.Window).ToList();
        if (events.Count == 0) yield break;
        // Laptops log this on battery and in quiet fan modes all the time, so there it's only worth an Info.
        var laptop = s.System?.Value?.IsLaptop == true;
        yield return F(!laptop && events.Count >= DesktopWarning ? Severity.Warning : Severity.Info,
            "The processor's speed is being limited by the firmware",
            $"{events.Count} speed-limit events in 30 days (Kernel-Processor-Power 37).",
            laptop
                ? "Normal on battery or in a quiet/eco mode. If the PC is slow while plugged in, clean the vents and fan and use the maker's performance mode."
                : "Check CPU temperatures and cooler mounting, clean dust out, and check the BIOS power limits and the Windows power plan.");
    }
}

/// <summary>CPU-006: the processor reported machine-check errors (WHEA): unstable overclock or failing hardware.</summary>
public sealed class Cpu006MachineChecks : Check
{
    public override string Id => "CPU-006";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        // PCIe corrected errors (WHEA 17) are mostly link power-saving noise, so they're left out.
        var events = s.Events!.Value!.Find(Whea.Provider, 1, 18, 19).Within(s, Ev.Window).Where(e => !Whea.IsMemory(e) && !Whea.IsPcie(e)).ToList();
        if (events.Count == 0) yield break;
        var fatal = events.Count(Whea.IsFatal);
        var kinds = string.Join(", ", events.Select(Whea.Kind).Distinct().Take(3));
        yield return F(fatal > 0 ? Severity.Critical : Severity.Warning,
            fatal > 0 ? "The processor reported a fatal hardware error" : "The processor reported corrected hardware errors",
            $"{N(events.Count, "WHEA processor event")} in 30 days{(fatal > 0 ? $", {fatal} fatal" : "")}: {kinds}.",
            "Remove any CPU overclock or undervolt, load BIOS defaults and update the BIOS. Check temperatures. If it continues at stock settings, the CPU, motherboard or power supply may be failing.");
    }
}

/// <summary>CPU-007: Intel 13th/14th-gen desktop processors on microcode older than 0x12B degrade permanently (Vmin shift instability).</summary>
public sealed class Cpu007Microcode : Check, IPhysicalHardwareCheck
{
    // Intel, "Vmin Shift Instability" root cause and 0x12B microcode (2024-09-25): the final mitigation for affected desktop parts.
    private const uint FixedRevision = 0x12B;
    private const int RaptorLakeModel = 183; // family 6 model 0xB7: the Raptor Lake die used by the affected i5/i7/i9 desktop chips

    public override string Id => "CPU-007";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Cpu)];

    private static readonly Regex Model = new(@"Family\s+6\s+Model\s+(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Desktop i5/i7/i9 13xxx/14xxx with K, KF, KS, F or no suffix (laptop H/HX/P/U and 35 W T parts aren't affected).
    private static readonly Regex Desktop = new(@"\bi[579]-1[34]\d{3}(K|KF|KS|F)?(?![A-Z0-9])", RegexOptions.CultureInvariant);

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var p in s.Cpu!.Value!.Packages)
        {
            if (p.Name is null || p.Identifier is null || !Desktop.IsMatch(p.Name)) continue;
            if (Model.Match(p.Identifier) is not { Success: true } m || int.Parse(m.Groups[1].Value) != RaptorLakeModel) continue;
            if (p.Microcode is not { } mc || !uint.TryParse(mc.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var rev)) continue;
            if (rev >= FixedRevision) continue;
            yield return F(Severity.Critical, "This Intel processor needs a BIOS update to stop permanent damage",
                $"{p.Name.Trim()} runs microcode {mc}; Intel's fix for 13th/14th-gen instability is 0x12B or newer.",
                "Update the motherboard BIOS now (it carries the new microcode) and load its Intel default settings. Crashes that already happen may mean the chip is degraded; Intel extended the warranty for these CPUs.");
            yield break;
        }
    }
}

/// <summary>GPU-004: the graphics driver stopped responding and Windows reset it (TDR: Display 4101, nvlddmkm, amdkmdag).</summary>
public sealed class Gpu004DriverResets : Check
{
    private const int Repeated = 3;
    private const int ManyPerDay = 5;

    public override string Id => "GPU-004";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var ev = s.Events!.Value!;
        var resets = ev.Find("Display", 4101).Concat(ev.Find("nvlddmkm", 13, 14, 153)).Concat(ev.Find("amdkmdag")).Within(s, Ev.Window).ToList();
        if (resets.Count == 0) yield break;
        var worstDay = resets.GroupBy(e => e.TimeUtc.ToLocalTime().Date).Max(g => g.Count());
        var severity = worstDay >= ManyPerDay ? Severity.Critical : resets.Count >= Repeated ? Severity.Warning : Severity.Info;
        yield return F(severity, resets.Count >= Repeated ? "The graphics driver keeps resetting" : "The graphics driver stopped responding",
            $"{resets.Count} graphics driver timeouts or errors in 30 days (up to {worstDay} in one day).",
            "Clean-install the latest graphics driver, remove any GPU overclock, and check GPU temperatures and power cables. Screen flicker or black screens that recover are this.");
    }
}
