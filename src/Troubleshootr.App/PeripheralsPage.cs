using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>The Peripherals tab: sound, printers, USB ports, Bluetooth, camera/microphone privacy, and the monitors.</summary>
public static class PeripheralsPage
{
    public static List<InfoGroup> Build(Snapshot s)
    {
        if (s.WhyMissing(nameof(Snapshot.Peripherals)) is { } why)
            return [new InfoGroup("Peripherals") { Note = $"Not available: {why}. Run a scan with Peripherals selected." }];
        var p = s.Peripherals!.Value!;
        var groups = new List<InfoGroup>();

        var sound = new InfoGroup("Sound") { Columns = ["Device", "Sound card", "Type", "State", "Default"], Note = p.AudioUnavailable is { } a ? $"Not available: {a}." : null };
        foreach (var x in p.Audio)
            sound.Table.Add([x.Name, x.Device ?? "", x.Playback ? "Playback" : "Recording", x.StateName, x.IsDefault ? "Yes" : ""]);
        groups.Add(sound);

        var printers = new InfoGroup("Printers")
        {
            Columns = ["Printer", "Connection", "Status", "Queue", "Default"],
            Note = p.PrintersUnavailable is { } pu ? $"Not available: {pu}." : $"Print Spooler: {p.SpoolerStatus ?? "not installed"}{(p.SpoolerStartType is { } st ? $" ({st})" : "")}.",
        };
        foreach (var x in p.Printers)
            printers.Table.Add([x.Name, x.IsVirtual ? "Software" : x.IsNetwork ? "Network (shared)" : Connection(x.Port), Status(x),
                x.Jobs == 0 ? "" : $"{x.Jobs} job{(x.Jobs == 1 ? "" : "s")}{(x.ErrorJobs > 0 ? $", {x.ErrorJobs} in error" : "")}", x.IsDefault ? "Yes" : ""]);
        groups.Add(printers);

        var usb = new InfoGroup("USB ports")
        {
            Columns = ["Device", "Id", "Speed", "Could do", "Status", "Port"],
            Note = p.UsbUnavailable is { } uu ? $"Not available: {uu}."
                : $"USB selective suspend: {OnOff(p.UsbSelectiveSuspendAc)} on power, {OnOff(p.UsbSelectiveSuspendDc)} on battery.",
        };
        foreach (var x in p.Usb.OrderBy(x => x.Hub).ThenBy(x => x.Port))
            usb.Table.Add([x.Device ?? (x.IsHub ? "USB hub" : "USB device"), x.VendorId == 0 ? "" : x.Id, x.SpeedName, x.SuperSpeedCapable == true && x.Speed < 3 ? "USB 3" : "",
                x.StatusName, $"{x.Hub} #{x.Port}"]);
        groups.Add(usb);

        var bt = p.Bluetooth;
        groups.Add(new InfoGroup("Bluetooth")
            .Row("Adapter", bt.Adapters.Count > 0 ? string.Join(", ", bt.Adapters) : "None found")
            .Row("Radio", bt.RadioFound switch { true => bt.Connectable == false ? "Off (not accepting connections)" : "On", false => bt.Adapters.Count > 0 ? "Not available (switched off or not working)" : null, null => null })
            .Row("Paired devices", bt.Paired?.ToString())
            .Row("Bluetooth service", bt.ServiceStatus is { } ss ? $"{ss} ({bt.ServiceStartType})" : null));

        groups.Add(Privacy("Camera", "camera", p.Camera));
        groups.Add(Privacy("Microphone", "microphone", p.Microphone));
        if (DevicePages.Displays(s) is { } displays) groups.Add(displays);
        return groups;
    }

    private static InfoGroup Privacy(string title, string what, PrivacySwitch sw) => new InfoGroup($"{title} privacy")
        .Row($"{title} access (this PC)", Switch(sw.Device))
        .Row($"Apps can use the {what}", Switch(sw.Apps))
        .Row($"Desktop apps can use the {what}", Switch(sw.DesktopApps))
        .Row("Policy", sw.Policy switch { 2 => "Blocked by policy", 1 => "Forced on by policy", 0 => "User decides", _ => null })
        .Row($"{title}s found", sw.Devices.Count > 0 ? string.Join(", ", sw.Devices) : "None");

    private static string? Switch(string? v) => v switch
    {
        null => null,
        _ when v.Equals("Allow", StringComparison.OrdinalIgnoreCase) => "On",
        _ when v.Equals("Deny", StringComparison.OrdinalIgnoreCase) => "Off",
        _ => v,
    };

    private static string OnOff(bool? b) => b switch { true => "on", false => "off", null => "unknown" };

    private static string Connection(string? port) => port switch
    {
        null => "",
        _ when port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) => "USB",
        _ when port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) => "Network (WSD)",
        _ when port.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) => "Parallel",
        _ when System.Text.RegularExpressions.Regex.IsMatch(port, @"^\d{1,3}(\.\d{1,3}){3}") || port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) => $"Network ({port.Replace("IP_", "")})",
        _ => port,
    };

    private static string Status(PrinterInfo x)
    {
        if (x.Offline || x.Status == 7 || x.ErrorState == 9) return "Offline";
        if (PrinterInfo.ErrorText(x.ErrorState) is { Length: > 0 } e) return char.ToUpperInvariant(e[0]) + e.Substring(1);
        return x.Status switch { 3 => "Ready", 4 => "Printing", 5 => "Warming up", 6 => "Stopped", _ => "Ready" };
    }
}
