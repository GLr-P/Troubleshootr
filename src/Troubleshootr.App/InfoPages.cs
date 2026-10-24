using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>One card on an info tab: label/value rows, or a table, or a "not available" note.</summary>
public sealed class InfoGroup(string title)
{
    public string Title { get; } = title;
    public List<(string Label, string Value)> Rows { get; } = new();
    public string[]? Columns { get; set; }
    public List<string[]> Table { get; } = new();
    public string? Note { get; set; }

    public InfoGroup Row(string label, object? value)
    {
        var text = value switch
        {
            null => null,
            bool b => b ? "Yes" : "No",
            DateTime d => d.ToString("yyyy-MM-dd"),
            _ => value.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(text)) Rows.Add((label, text!));
        return this;
    }
}

/// <summary>Builds each info tab from the Snapshot. A tab is a list of groups, rendered by InfoPage.</summary>
public static class InfoPages
{
    public static readonly string[] Names = ["Summary", "System", "CPU", "Memory", "Motherboard", "Storage", "Graphics"];

    public static List<InfoGroup> Build(string page, Snapshot s) => page switch
    {
        "Summary" => Summary(s),
        "System" => SystemPage(s),
        "CPU" => Cpu(s),
        "Memory" => Memory(s),
        "Motherboard" => Board(s),
        "Storage" => Storage(s),
        "Graphics" => Graphics(s),
        _ => [],
    };

    private static InfoGroup? Missing(string title, Snapshot s, string section)
    {
        var why = s.WhyMissing(section);
        return why is null ? null : new InfoGroup(title) { Note = $"Not available: {why}." };
    }

    private static List<InfoGroup> Summary(Snapshot s)
    {
        var d = DeviceSummary.From(s);
        var sys = s.System?.Value;
        var g = new InfoGroup("This PC")
            .Row("Device name", d.DeviceName)
            .Row("Maker and model", $"{d.Manufacturer} {d.Model}".Trim())
            .Row("Type", d.IsLaptop is null ? null : d.IsLaptop.Value ? "Laptop" : "Desktop")
            .Row("Windows", d.Windows)
            .Row("Processor", d.Cpu)
            .Row("Memory", d.RamGB is { } gb ? $"{gb:0.#} GB" : null)
            .Row("Graphics", d.Gpu)
            .Row("Uptime", sys is null ? null : Ui.Duration(sys.UptimeSeconds))
            .Row("Running as admin", s.Meta.IsAdmin);
        var groups = new List<InfoGroup> { g };
        if (s.Volumes?.Value is { Count: > 0 } vols)
        {
            var v = new InfoGroup("Drives") { Columns = ["Drive", "Label", "Used", "Free", "Size"] };
            foreach (var x in vols)
                v.Table.Add([x.Root, x.Label ?? "", $"{100 - 100.0 * x.FreeBytes / Math.Max(1, x.TotalBytes):0}%", Gb(x.FreeBytes), Gb(x.TotalBytes)]);
            groups.Add(v);
        }
        return groups;
    }

    private static List<InfoGroup> SystemPage(Snapshot s)
    {
        if (Missing("Windows", s, nameof(Snapshot.System)) is { } m) return [m];
        var x = s.System!.Value!;
        return
        [
            new InfoGroup("Windows")
                .Row("Version", WindowsVersion.Describe(x))
                .Row("Edition", x.EditionId)
                .Row("Architecture", $"{x.Architecture} ({(x.Is64BitOs ? "64-bit" : "32-bit")})")
                .Row("Installed", x.InstallDateUtc?.ToLocalTime())
                .Row("Support ends", WindowsVersion.EndOfSupport(x))
                .Row("Uptime", Ui.Duration(x.UptimeSeconds))
                .Row("Fast Startup", x.FastStartupEnabled)
                .Row("Restart pending", x.PendingRebootReasons.Count == 0 ? "No" : string.Join(", ", x.PendingRebootReasons)),
            new InfoGroup("Computer")
                .Row("Device name", x.DeviceName)
                .Row("User", x.UserName)
                .Row("Domain / workgroup", x.Domain)
                .Row("Manufacturer", x.Manufacturer)
                .Row("Model", x.Model)
                .Row("Serial number", x.SerialNumber)
                .Row("Has battery", x.HasBattery),
        ];
    }

    private static List<InfoGroup> Cpu(Snapshot s)
    {
        if (Missing("Processor", s, nameof(Snapshot.Cpu)) is { } m) return [m];
        return s.Cpu!.Value!.Packages.Select((p, i) => new InfoGroup(s.Cpu.Value.Packages.Count > 1 ? $"Processor {i + 1}" : "Processor")
            .Row("Name", p.Name)
            .Row("Manufacturer", p.Manufacturer)
            .Row("Cores / threads", $"{p.Cores} / {p.Threads}")
            .Row("Base clock", p.MaxClockMhz > 0 ? $"{p.MaxClockMhz} MHz" : null)
            .Row("Current clock", p.CurrentClockMhz > 0 ? $"{p.CurrentClockMhz} MHz" : null)
            .Row("Socket", p.Socket)
            .Row("L2 cache", p.L2KB > 0 ? Kb(p.L2KB) : null)
            .Row("L3 cache", p.L3KB > 0 ? Kb(p.L3KB) : null)
            .Row("Virtualization", p.VirtualizationFirmwareEnabled)
            .Row("Microcode", p.Microcode)
            .Row("Identifier", p.Identifier)).ToList();
    }

    private static List<InfoGroup> Memory(Snapshot s)
    {
        if (Missing("Memory", s, nameof(Snapshot.Memory)) is { } m) return [m];
        var x = s.Memory!.Value!;
        var summary = new InfoGroup("Memory")
            .Row("Installed", x.InstalledMB is { } i ? GbMb(i) : null)
            .Row("Usable", GbMb(x.UsableMB))
            .Row("In use", $"{GbMb(x.UsableMB - x.AvailableMB)} ({100.0 * (x.UsableMB - x.AvailableMB) / Math.Max(1, x.UsableMB):0}%)")
            .Row("Committed", $"{GbMb(x.CommitUsedMB)} of {GbMb(x.CommitLimitMB)}")
            .Row("Slots", x.SlotCount is { } n ? $"{x.Modules.Count} of {n} used" : null)
            .Row("Maximum supported", x.MaxCapacityMB is { } max ? GbMb(max) : null);
        var modules = new InfoGroup("Modules") { Columns = ["Slot", "Size", "Type", "Speed", "Running at", "Maker", "Part number"] };
        foreach (var mod in x.Modules)
            modules.Table.Add([mod.Locator ?? mod.BankLabel ?? "", GbMb(mod.CapacityMB), $"{mod.Type} {mod.FormFactor}".Trim(),
                mod.SpeedMts is { } sp ? $"{sp} MT/s" : "", mod.ConfiguredSpeedMts is { } c ? $"{c} MT/s" : "", mod.Manufacturer ?? "", mod.PartNumber ?? ""]);
        return [summary, modules];
    }

    private static List<InfoGroup> Board(Snapshot s)
    {
        if (Missing("Motherboard", s, nameof(Snapshot.Board)) is { } m) return [m];
        var b = s.Board!.Value!;
        return
        [
            new InfoGroup("Motherboard").Row("Manufacturer", b.BoardManufacturer).Row("Model", b.BoardProduct).Row("Version", b.BoardVersion).Row("Serial number", b.BoardSerial),
            new InfoGroup("Firmware").Row("Vendor", b.BiosVendor).Row("Version", b.BiosVersion).Row("Date", b.BiosDate).Row("Type", b.FirmwareType).Row("Secure Boot", b.SecureBoot),
        ];
    }

    private static List<InfoGroup> Storage(Snapshot s)
    {
        if (Missing("Volumes", s, nameof(Snapshot.Volumes)) is { } m) return [m];
        var v = new InfoGroup("Volumes") { Columns = ["Drive", "Label", "File system", "Type", "Free", "Size", "System"] };
        foreach (var x in s.Volumes!.Value!)
            v.Table.Add([x.Root, x.Label ?? "", x.FileSystem ?? "", x.DriveType ?? "", Gb(x.FreeBytes), Gb(x.TotalBytes), x.IsSystem ? "Yes" : ""]);
        return [v, new InfoGroup("Drive health") { Note = "Drive health (SMART / NVMe) arrives in a later version." }];
    }

    private static List<InfoGroup> Graphics(Snapshot s)
    {
        if (Missing("Graphics", s, nameof(Snapshot.Gpus)) is { } m) return [m];
        return s.Gpus!.Value!.Select(g => new InfoGroup(g.Name ?? "Display adapter")
            .Row("Video memory", g.VramBytes is { } v and > 0 ? Gb(v) : null)
            .Row("Driver version", g.DriverVersion)
            .Row("Driver date", g.DriverDate)
            .Row("Resolution", g.ResolutionWidth is { } w and > 0 ? $"{w} × {g.ResolutionHeight} at {g.RefreshHz} Hz" : null)
            .Row("Status", g.Status)
            .Row("Device ID", g.PnpDeviceId)).ToList();
    }

    private static string Gb(long bytes) => bytes >= 1073741824L * 1000 ? $"{bytes / 1099511627776.0:0.##} TB" : $"{bytes / 1073741824.0:0.#} GB";
    private static string GbMb(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";
    private static string Kb(int kb) => kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";
}
