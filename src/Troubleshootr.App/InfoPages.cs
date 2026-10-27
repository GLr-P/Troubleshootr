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
    /// <summary>Optional status pill next to the title ("Good", "Bad"), coloured by a theme key (Ok, Warning, Critical, Info).</summary>
    public string? Badge { get; set; }
    public string BadgeKey { get; set; } = "Info";
    /// <summary>Show a filter box above the table (long lists like the error timeline).</summary>
    public bool Filterable { get; set; }
    /// <summary>A button under the card that runs something on demand (the speed test) and shows the text it returns.</summary>
    public (string Label, Func<CancellationToken, Task<string>> Run)? Action { get; set; }

    public InfoGroup Row(string label, object? value)
    {
        var text = value switch
        {
            null => null,
            bool b => b ? "Yes" : "No",
            DateTime d => d.ToString("yyyy-MM-dd"),
            long l => l.ToString("N0"),
            _ => value.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(text)) Rows.Add((label, text!));
        return this;
    }
}

/// <summary>Builds each info tab from the Snapshot. A tab is a list of groups, rendered by InfoPage.</summary>
public static class InfoPages
{
    public static readonly string[] Names = ["Summary", "System", "Updates", "CPU", "Memory", "Motherboard", "Storage", "Graphics", "Devices", "Drivers", "Events & Crashes", "Performance", "Processes", "Sensors", "Startup", "Software", "Network", "Security", "Peripherals", "Battery"];

    public static List<InfoGroup> Build(string page, Snapshot s) => page switch
    {
        "Summary" => Summary(s),
        "System" => SystemPage(s),
        "CPU" => Cpu(s),
        "Memory" => Memory(s),
        "Motherboard" => Board(s),
        "Storage" => Storage(s),
        "Graphics" => Graphics(s),
        "Events & Crashes" => EventsPage.Build(s),
        "Devices" => DevicePages.Devices(s),
        "Drivers" => DevicePages.Drivers(s),
        "Updates" => DevicePages.Updates(s),
        "Performance" => PerfPages.Performance(s),
        "Startup" => PerfPages.Startup(s),
        "Software" => PerfPages.Software(s),
        "Network" => NetPages.Network(s),
        "Security" => SecurityPage.Build(s),
        "Peripherals" => PeripheralsPage.Build(s),
        "Battery" => BatteryPage.Build(s),
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
        foreach (var disk in s.Storage?.Value?.Disks ?? [])
            g.Row($"Disk {disk.Number}", $"{disk.Model} ({Gb(disk.SizeBytes)}): {DiskHealth.Assess(disk).State}");
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
        var groups = new List<InfoGroup>();
        if (s.Storage?.Value is { } st)
            foreach (var d in st.Disks.OrderBy(d => d.Number)) groups.AddRange(Disk(d));
        else if (Missing("Drives", s, nameof(Snapshot.Storage)) is { } missing) groups.Add(missing);

        if (s.Volumes?.Value is { } vols)
        {
            var v = new InfoGroup("Volumes") { Columns = ["Drive", "Label", "File system", "Type", "Free", "Size", "System", "Needs chkdsk"] };
            foreach (var x in vols)
                v.Table.Add([x.Root, x.Label ?? "", x.FileSystem ?? "", x.DriveType ?? "", Gb(x.FreeBytes), Gb(x.TotalBytes), x.IsSystem ? "Yes" : "",
                             x.Dirty switch { true => "Yes", false => "No", null => "?" }]);
            groups.Add(v);
        }
        if (s.Storage?.Value is { VirtualDisks.Count: > 0 } sp)
        {
            var g = new InfoGroup("Storage Spaces") { Columns = ["Name", "Type", "Health", "Status"] };
            foreach (var v in sp.VirtualDisks) g.Table.Add([v.Name ?? "", v.Resiliency ?? "", v.Health ?? "", string.Join(", ", v.OperationalStatus)]);
            groups.Add(g);
        }
        return groups;
    }

    /// <summary>One CrystalDiskInfo-style card per drive, plus its SMART table for ATA drives.</summary>
    private static IEnumerable<InfoGroup> Disk(DiskInfo d)
    {
        var (state, reasons) = DiskHealth.Assess(d);
        var g = new InfoGroup($"Disk {d.Number}: {d.Model}")
        {
            Badge = state switch { DiskState.Good => "Good", DiskState.Caution => "Caution", DiskState.Bad => "Bad", _ => "Unknown" },
            BadgeKey = state switch { DiskState.Good => "Ok", DiskState.Caution => "Warning", DiskState.Bad => "Critical", _ => "Info" },
        };
        var n = d.Nvme;
        var r = d.Reliability;
        var hours = DiskHealth.PowerOnHoursOf(d);
        var wear = DiskHealth.Wear(d);
        g.Row("Health", state == DiskState.Unknown ? $"Unknown: {d.HealthDataUnavailable ?? "no health data"}" : reasons.Count == 0 ? "No problems reported" : string.Join("; ", reasons))
         .Row("Type", $"{d.MediaType ?? "Drive"} on {d.BusType ?? "unknown bus"}{(d.SpindleRpm is { } rpm ? $", {rpm} rpm" : "")}")
         .Row("Capacity", d.SizeBytes > 0 ? Gb(d.SizeBytes) : null)
         .Row("Drive letters", d.DriveLetters.Count > 0 ? string.Join(" ", d.DriveLetters) : null)
         .Row("Holds Windows", d.IsBoot ? "Yes" : null)
         .Row("Temperature", DiskHealth.TemperatureC(d) is { } t ? $"{t} °C{(r?.TemperatureMaxC is { } max and > 0 ? $" (max {max} °C)" : "")}" : null)
         .Row("Life used", wear is { } w ? $"{w}% ({Math.Max(0, 100 - w)}% left)" : null)
         .Row("Power-on hours", hours is { } h ? $"{h:N0} h ({h / 24.0:N0} days)" : null)
         .Row("Power cycles", n?.PowerCycles ?? d.Ata?[0x0C]?.Raw)
         .Row("Data written", n is not null ? $"{n.TerabytesWritten:0.##} TB" : null)
         .Row("Data read", n is not null ? $"{n.DataUnitsRead * 512000.0 / 1e12:0.##} TB" : null)
         .Row("Available spare", n is not null ? $"{n.AvailableSparePercent}% (threshold {n.SpareThresholdPercent}%)" : null)
         .Row("Unsafe shutdowns", n?.UnsafeShutdowns)
         .Row("Media errors", n?.MediaErrors)
         .Row("Uncorrected errors", r is { } rr && (rr.ReadErrorsUncorrected is not null || rr.WriteErrorsUncorrected is not null)
             ? $"read {rr.ReadErrorsUncorrected ?? 0}, write {rr.WriteErrorsUncorrected ?? 0}" : null)
         .Row("Windows health", d.Health is null ? null : $"{d.Health}{(d.OperationalStatus.Count > 0 ? $" ({string.Join(", ", d.OperationalStatus)})" : "")}")
         .Row("Firmware", d.Firmware)
         .Row("Serial number", d.SerialNumber);
        yield return g;

        if (d.Ata is { Attributes.Count: > 0 } a)
        {
            var table = new InfoGroup($"Disk {d.Number}: SMART attributes") { Columns = ["ID", "Attribute", "Current", "Worst", "Threshold", "Raw", ""] };
            foreach (var x in a.Attributes)
                table.Table.Add([x.Id.ToString("X2"), x.Name, x.Current.ToString(), x.Worst.ToString(), x.Threshold.ToString(), x.Raw.ToString("N0"),
                             x.BelowThreshold ? "FAILING" : Flag(x)]);
            yield return table;
        }
    }

    private static string Flag(SmartAttribute x) =>
        x.Id is DiskHealth.PendingSectors or DiskHealth.UncorrectableSectors && x.Raw > 0 ? "Bad"
        : x.Id is DiskHealth.ReallocatedSectors && x.Raw > 0 ? "Caution" : "";

    private static List<InfoGroup> Graphics(Snapshot s)
    {
        var groups = Missing("Graphics", s, nameof(Snapshot.Gpus)) is { } m ? [m] : s.Gpus!.Value!.Select(g => new InfoGroup(g.Name ?? "Display adapter")
            .Row("Video memory", g.VramBytes is { } v and > 0 ? Gb(v) : null)
            .Row("Driver version", g.DriverVersion)
            .Row("Driver date", g.DriverDate)
            .Row("Resolution", g.ResolutionWidth is { } w and > 0 ? $"{w} × {g.ResolutionHeight} at {g.RefreshHz} Hz" : null)
            .Row("Status", g.Status)
            .Row("Device ID", g.PnpDeviceId)).ToList();
        if (DevicePages.Displays(s) is { } displays) groups.Add(displays);
        return groups;
    }

    private static string Gb(long bytes) => bytes >= 1073741824L * 1000 ? $"{bytes / 1099511627776.0:0.##} TB" : $"{bytes / 1073741824.0:0.#} GB";
    private static string GbMb(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";
    private static string Kb(int kb) => kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";
}
