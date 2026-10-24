using Troubleshootr.Core;

namespace Troubleshootr.Checks.Graphics;

internal static class Gpus
{
    public static bool IsBasic(GpuInfo g) => g.Name is { } n && (n.IndexOf("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || n.IndexOf("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>A graphics card rather than graphics built into the processor (AMD APUs are just "AMD Radeon(TM) Graphics").</summary>
    public static bool IsDedicated(string? name) =>
        name is not null && (name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0
                             || System.Text.RegularExpressions.Regex.IsMatch(name, @"Radeon\s*(\(TM\)\s*)?(RX|Pro|R[579]|HD)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                             || System.Text.RegularExpressions.Regex.IsMatch(name, @"Arc(\(TM\))?\s+[AB]\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
}

/// <summary>GPU-002: the GPU has no driver, so Windows uses its basic one (no acceleration, wrong resolution, no games).</summary>
public sealed class Gpu002BasicDisplay : Check, IPhysicalHardwareCheck
{
    public override string Id => "GPU-002";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Gpus)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var basic = s.Gpus!.Value!.Where(Gpus.IsBasic).ToList();
        if (basic.Count == 0) yield break;
        yield return F(Severity.Critical, "The graphics card has no driver", $"Windows is using {basic[0].Name}{(basic[0].PnpDeviceId is { } id ? $" [{id}]" : "")}.",
            "Install the graphics driver from NVIDIA, AMD or Intel (or the laptop maker). Until then there's no hardware acceleration, games won't run and the resolution may be wrong.");
    }
}

/// <summary>GPU-003: the graphics driver is old.</summary>
public sealed class Gpu003OldDriver : Check, IPhysicalHardwareCheck
{
    private const int InfoMonths = 12;
    private const int WarningMonths = 24;

    public override string Id => "GPU-003";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Gpus)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var g in s.Gpus!.Value!.Where(g => !Gpus.IsBasic(g) && g.DriverDate is not null))
        {
            var months = (s.Meta.CollectedUtc - g.DriverDate!.Value).TotalDays / 30.44;
            if (months < InfoMonths) continue;
            yield return F(months >= WarningMonths ? Severity.Warning : Severity.Info, $"The graphics driver is old: {g.Name}",
                $"Driver {g.DriverVersion} from {g.DriverDate:yyyy-MM-dd} ({months / 12:0.#} years old).",
                "Update it from NVIDIA, AMD or Intel (laptops: the maker's site if the generic driver won't install). New drivers fix crashes, game bugs and security holes.");
        }
    }
}

/// <summary>GPU-006: on a desktop with a graphics card, the monitor is plugged into the motherboard instead of the card.</summary>
public sealed class Gpu006MonitorOnMotherboard : Check, IPhysicalHardwareCheck
{
    public override string Id => "GPU-006";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Gpus), nameof(Snapshot.Displays), nameof(Snapshot.System)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        if (s.System!.Value!.IsLaptop) yield break; // laptops route the panel through the iGPU on purpose
        var card = s.Gpus!.Value!.FirstOrDefault(g => Gpus.IsDedicated(g.Name));
        var displays = s.Displays!.Value!.Displays;
        if (card is null || displays.Count == 0 || displays.Any(d => Gpus.IsDedicated(d.Adapter))) yield break;
        yield return F(Severity.Warning, "The monitor is plugged into the motherboard, not the graphics card",
            $"Displays run on {string.Join(", ", displays.Select(d => d.Adapter).Distinct())} while {card.Name} sits unused.",
            "Move the monitor cable to the ports on the graphics card (the lower, horizontal ones at the back). Games run far slower on the built-in graphics.");
    }
}

/// <summary>GPU-007: a monitor runs below the refresh rate it supports (a 144 Hz screen left at 60 Hz).</summary>
public sealed class Gpu007RefreshRate : Check
{
    private const int Margin = 5; // 59.94 vs 60 and similar aren't worth mentioning

    public override string Id => "GPU-007";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Displays)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var slow = s.Displays!.Value!.Displays.Where(d => d.RefreshHz > 1 && d.MaxRefreshHz > d.RefreshHz + Margin).ToList();
        if (slow.Count == 0) yield break;
        yield return F(Severity.Info, "A display runs below its best refresh rate",
            string.Join("; ", slow.Select(d => $"{d.Monitor ?? "Display"} at {d.Width}×{d.Height}: {d.RefreshHz} Hz, can do {d.MaxRefreshHz} Hz")) + ".",
            "Settings > System > Display > Advanced display > Choose a refresh rate. Laptops may lower it on battery on purpose; desktops may need a DisplayPort or better HDMI cable.");
    }
}

/// <summary>GPU-009: on PCs with two GPUs, apps forced onto the power-saving one.</summary>
public sealed class Gpu009PowerSavingApps : Check
{
    public override string Id => "GPU-009";
    public override Category Category => Category.Graphics;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Displays), nameof(Snapshot.Gpus)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var apps = s.Displays!.Value!.PowerSavingApps;
        if (apps.Count == 0 || !s.Gpus!.Value!.Any(g => Gpus.IsDedicated(g.Name))) yield break;
        yield return F(Severity.Info, "Some apps are set to use the power-saving GPU",
            string.Join(", ", apps.Take(6).Select(a => Path.GetFileName(a))) + ".",
            "If one of these is a game or 3D app that runs slowly, set it to High performance in Settings > System > Display > Graphics.");
    }
}
