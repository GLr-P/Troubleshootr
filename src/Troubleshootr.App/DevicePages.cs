using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>The Drivers, Devices and Updates tabs, and the display list on the Graphics tab.</summary>
public static class DevicePages
{
    private static InfoGroup? Missing(string title, Snapshot s, string section) =>
        s.WhyMissing(section) is { } why ? new InfoGroup(title) { Note = $"Not available: {why}. Run a scan with Drivers & devices selected." } : null;

    public static List<InfoGroup> Devices(Snapshot s)
    {
        if (Missing("Devices", s, nameof(Snapshot.Devices)) is { } m) return [m];
        var d = s.Devices!.Value!;
        var groups = new List<InfoGroup>();
        var problems = new InfoGroup("Devices with problems") { Columns = ["Device", "Class", "Problem", "Vendor", "Hardware id"] };
        foreach (var x in d.Devices.Where(x => x.ProblemCode != 0))
            problems.Table.Add([x.Name, x.Class ?? "", $"{DeviceProblems.Meaning(x.ProblemCode)} (code {x.ProblemCode})", x.Vendor ?? x.Manufacturer ?? "", x.HardwareId ?? ""]);
        groups.Add(problems);

        var all = new InfoGroup($"All devices ({d.Devices.Count})") { Columns = ["Class", "Device", "Maker", "Driver", "Status"], Filterable = true };
        foreach (var x in d.Devices)
            all.Table.Add([x.Class ?? "(none)", x.Name, x.Manufacturer ?? "", x.DriverVersion ?? "", x.ProblemCode == 0 ? "OK" : $"Code {x.ProblemCode}"]);
        groups.Add(all);

        var hidden = new InfoGroup($"Not connected ({d.HiddenCount} hidden devices)")
        {
            Note = "Hardware that was connected before. Normal for USB devices; graphics, network and storage ones are listed.",
            Columns = ["Class", "Device", "Driver"],
        };
        foreach (var x in d.Hidden) hidden.Table.Add([x.Class ?? "", x.Name, x.DriverVersion ?? ""]);
        groups.Add(hidden);
        return groups;
    }

    public static List<InfoGroup> Drivers(Snapshot s)
    {
        if (Missing("Drivers", s, nameof(Snapshot.Devices)) is { } m) return [m];
        var d = s.Devices!.Value!;
        var signing = new InfoGroup("Driver signing")
            .Row("Test-signing mode", d.TestSigning)
            .Row("Signature checks off", d.IntegrityChecksOff)
            .Row("Kernel drivers registered", d.KernelDrivers.Count.ToString());
        var list = new InfoGroup("Device drivers") { Columns = ["Device", "Provider", "Version", "Date"], Filterable = true };
        foreach (var x in d.Devices.Where(x => x.DriverVersion is not null).OrderBy(x => x.DriverProvider).ThenBy(x => x.Name))
            list.Table.Add([x.Name, x.DriverProvider ?? "", x.DriverVersion ?? "", x.DriverDate?.ToString("yyyy-MM-dd") ?? ""]);
        var kernel = new InfoGroup("Kernel drivers") { Columns = ["Service", "File", "Starts"], Filterable = true };
        foreach (var k in d.KernelDrivers.Where(k => k.Start is not 4).OrderBy(k => k.Name))
            kernel.Table.Add([k.Name, k.File ?? "", k.Start switch { 0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "On demand", _ => "" }]);
        return [signing, list, kernel];
    }

    public static List<InfoGroup> Updates(Snapshot s)
    {
        var groups = new List<InfoGroup>();
        var h = s.Health?.Value;
        var status = new InfoGroup("Windows")
            .Row("Activation", h?.LicenseStatus switch { 1 => "Activated", null => null, var x => $"Not activated (status {x})" })
            .Row("License", h?.LicenseName);
        if (s.Devices?.Value is { } dev)
            status.Row("TPM", dev.Devices.FirstOrDefault(x => x.Name.IndexOf("Trusted Platform Module", StringComparison.OrdinalIgnoreCase) >= 0)?.Name ?? "Not found");
        if (s.Board?.Value is { } b) status.Row("Firmware", b.FirmwareType).Row("Secure Boot", b.SecureBoot);
        groups.Add(status);

        if (Missing("Windows Update", s, nameof(Snapshot.Updates)) is { } m) { groups.Add(m); return groups; }
        var u = s.Updates!.Value!;
        var last = u.History.FirstOrDefault(e => e.IsWindowsUpdate && e.Operation == 1 && e.Succeeded);
        var p = u.Policy;
        var policy = new InfoGroup("Windows Update")
            .Row("Last Windows update", last is null ? "None in the history" : $"{last.DateUtc.ToLocalTime():yyyy-MM-dd}: {last.Title}")
            .Row("Paused until", p.PausedUntilUtc is { } until && until > DateTime.UtcNow ? until.ToLocalTime().ToString("yyyy-MM-dd") : null)
            .Row("Automatic updates", p.AutoUpdateOff ? "Off (policy)" : "On")
            .Row("Blocked by policy", p.WindowsUpdateBlocked ? "Yes" : null)
            .Row("Update server", p.UsesWsus ? p.WsusServer ?? "Company server (WSUS)" : "Microsoft")
            .Row("Pinned version", p.TargetVersion)
            .Row("History", u.HistoryUnavailable);
        foreach (var block in u.UpgradeBlocks)
            policy.Row($"Upgrade to {System.Text.RegularExpressions.Regex.Match(block.Target, @"\d{2}H\d").Value}", string.Join(", ", new[] { block.RedReason is { Length: > 0 } r && r != "None" ? r : null, block.GatedBlockId is { Length: > 0 } g && g != "None" ? $"safeguard hold {g}" : null }.Where(x => x is not null)) is { Length: > 0 } why ? why : "Not blocked");
        groups.Add(policy);

        var history = new InfoGroup("Update history") { Columns = ["When", "Result", "Update", "Error"], Filterable = true };
        foreach (var e in u.History)
            history.Table.Add([e.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                e.Result switch { 2 => "Installed", 3 => "Installed with errors", 4 => "Failed", 5 => "Cancelled", 1 => "In progress", _ => $"{e.Result}" } + (e.Operation == 2 ? " (uninstall)" : ""),
                e.Title, e.Failed ? UpdateErrors.Describe(e.HResult) : ""]);
        groups.Add(history);
        return groups;
    }

    public static InfoGroup? Displays(Snapshot s)
    {
        if (s.Displays?.Value is not { } d) return null;
        var g = new InfoGroup("Displays") { Columns = ["Monitor", "Size", "Connected to", "Resolution", "Native", "Refresh", "Best refresh", "Main"] };
        foreach (var x in d.Displays)
            g.Table.Add([x.DisplayName, x.SizeInches > 0 ? $"{x.SizeInches:0.#}\"{(x.MonitorYear > 0 ? $" ({x.MonitorYear})" : "")}" : "", x.Adapter ?? "", $"{x.Width} × {x.Height}",
                x.NativeWidth > 0 ? $"{x.NativeWidth} × {x.NativeHeight}" : "", $"{x.RefreshHz} Hz", x.MaxRefreshHz > x.RefreshHz ? $"{x.MaxRefreshHz} Hz" : "", x.Primary ? "Yes" : ""]);
        if (d.PowerSavingApps.Count > 0) g.Note = $"Set to the power-saving GPU: {string.Join(", ", d.PowerSavingApps.Select(System.IO.Path.GetFileName))}.";
        return g;
    }
}
