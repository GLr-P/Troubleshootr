using System.Text.RegularExpressions;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Memory;

/// <summary>MEM-001: RAM that's installed but Windows can't use (32-bit Windows, iGPU reservation, BIOS memory remap off, a bad slot).</summary>
public sealed class Mem001UnusableRam : Check, IPhysicalHardwareCheck
{
    private const double InfoFraction = 0.10;    // iGPUs commonly reserve 0.5–2 GB
    private const double WarningFraction = 0.25;

    public override string Id => "MEM-001";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Memory), nameof(Snapshot.System)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var m = s.Memory!.Value!;
        var sys = s.System!.Value!;
        if (!sys.Is64BitOs && (m.InstalledMB ?? m.UsableMB) > 4096)
        {
            yield return F(Severity.Warning, "32-bit Windows can't use all the RAM",
                $"{GbFromMb(m.InstalledMB ?? 0)} installed, only {GbFromMb(m.UsableMB)} usable on 32-bit Windows.",
                "Install 64-bit Windows to use all of the memory.");
            yield break;
        }
        if (m.InstalledMB is not { } installed || installed <= 0) yield break;
        var unusable = installed - m.UsableMB;
        var fraction = (double)unusable / installed;
        if (fraction < InfoFraction) yield break;
        var evidence = $"{GbFromMb(installed)} installed, {GbFromMb(m.UsableMB)} usable ({GbFromMb(unusable)} reserved by hardware).";
        if (fraction >= WarningFraction)
            yield return F(Severity.Warning, "A large part of the RAM is unusable", evidence,
                "Check that every stick is detected in the BIOS, reseat the RAM, update the BIOS, and lower the integrated graphics memory if it's set very high.");
        else
            yield return F(Severity.Info, "Some RAM is reserved by hardware", evidence,
                "Usually integrated graphics. Normal unless it's more than you'd expect for this PC.");
    }
}

/// <summary>MEM-003: RAM running below its rated speed (XMP/EXPO off). Laptops and OEM desktops often can't change it.</summary>
public sealed class Mem003BelowRatedSpeed : Check, IPhysicalHardwareCheck
{
    // Speeds sold as XMP/EXPO kits; used to read the rated speed from part numbers like F4-3600C16 or CMK32GX4M2D3600C18.
    private static readonly int[] KitSpeeds = [2400, 2666, 2800, 3000, 3200, 3333, 3466, 3600, 3733, 3800, 4000, 4266, 4400, 4600, 4800,
                                               5200, 5600, 6000, 6200, 6400, 6600, 6800, 7000, 7200, 7600, 8000, 8200, 8400];
    private static readonly Regex Digits = new(@"(?<!\d)(\d{4})(?!\d)", RegexOptions.Compiled);

    public override string Id => "MEM-003";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Memory), nameof(Snapshot.System)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var modules = s.Memory!.Value!.Modules.Where(m => m.ConfiguredSpeedMts is > 0).ToList();
        if (modules.Count == 0) yield break;
        var configured = modules.Min(m => m.ConfiguredSpeedMts!.Value);
        var rated = modules.Select(Rated).Where(r => r > 0).DefaultIfEmpty(0).Min();
        if (rated <= configured * 1.04) yield break; // within 4%: same speed bin
        var where = s.System!.Value!.IsLaptop ? " Most laptops don't allow changing this." : "";
        yield return F(Severity.Info, "RAM runs below its rated speed",
            $"Running at {configured} MT/s; the modules are rated for {rated} MT/s ({modules[0].PartNumber}).",
            $"Enable the XMP / EXPO / DOCP memory profile in the BIOS for free performance.{where}");
    }

    /// <summary>The module's rated speed: from the part number if it names a kit speed, else the SMBIOS speed.</summary>
    internal static int Rated(MemoryModule m)
    {
        if (m.PartNumber is { } pn)
            foreach (Match match in Digits.Matches(pn))
                if (int.Parse(match.Value) is var v && Array.IndexOf(KitSpeeds, v) >= 0) return v;
        return m.SpeedMts ?? 0;
    }
}

/// <summary>MEM-004: mixed RAM sticks (different sizes, speeds or models): can cause instability or disable dual channel.</summary>
public sealed class Mem004MismatchedModules : Check, IPhysicalHardwareCheck
{
    public override string Id => "MEM-004";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Memory)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var modules = s.Memory!.Value!.Modules;
        if (modules.Count < 2) yield break;
        var sizes = modules.Select(m => m.CapacityMB).Distinct().Count();
        var parts = modules.Select(m => m.PartNumber ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var speeds = modules.Select(m => m.SpeedMts ?? 0).Distinct().Count();
        if (sizes == 1 && parts == 1 && speeds == 1) yield break;
        var list = string.Join("; ", modules.Select(m => $"{m.Locator ?? m.BankLabel}: {GbFromMb(m.CapacityMB)} {m.SpeedMts} MT/s {m.Manufacturer} {m.PartNumber}".Trim()));
        yield return F(Severity.Info, "The RAM sticks don't match", list,
            sizes > 1
                ? "Different sizes can run partly in single channel. If the PC crashes or won't boot reliably, test with matching sticks."
                : "Mixed models usually work but are a common cause of instability with XMP/EXPO on. If there are crashes, test each stick on its own.");
    }
}

/// <summary>MEM-005: one stick in a PC with two or more slots = single channel, roughly half the memory bandwidth.</summary>
public sealed class Mem005SingleChannel : Check, IPhysicalHardwareCheck
{
    public override string Id => "MEM-005";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Memory)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var m = s.Memory!.Value!;
        if (m.Modules.Count != 1 || m.SlotCount is not >= 2) yield break;
        // DDR5 sticks have two 32-bit subchannels, but one stick is still half the bandwidth of two.
        var stick = m.Modules[0];
        yield return F(Severity.Info, "RAM runs in single channel",
            $"1 stick ({GbFromMb(stick.CapacityMB)} {stick.Type}) in a board with {m.SlotCount} slots.",
            "Adding a matching second stick doubles memory bandwidth (a big boost for integrated graphics and games).");
    }
}

/// <summary>MEM-006 (partial): memory nearly exhausted right now.</summary>
public sealed class Mem006MemoryPressure : Check
{
    private const double CommitWarning = 0.90;
    private const double PhysicalWarning = 0.95;

    public override string Id => "MEM-006";
    public override Category Category => Category.Memory;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Memory)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var m = s.Memory!.Value!;
        if (m.UsableMB <= 0 || m.CommitLimitMB <= 0) yield break;
        var used = 1 - (double)m.AvailableMB / m.UsableMB;
        var commit = (double)m.CommitUsedMB / m.CommitLimitMB;
        if (commit < CommitWarning && used < PhysicalWarning) yield break;
        yield return F(Severity.Warning, "Memory is nearly full",
            $"{used:P0} of RAM in use ({GbFromMb(m.AvailableMB)} free of {GbFromMb(m.UsableMB)}); commit {GbFromMb(m.CommitUsedMB)} of {GbFromMb(m.CommitLimitMB)} ({commit:P0}).",
            "Close heavy apps or browser tabs and check the Processes tab for a memory hog. If it's always like this, the PC needs more RAM or a bigger pagefile.");
    }
}
