using System.Globalization;
using Troubleshootr.Checks.Peripherals;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Drivers;

/// <summary>
/// DRV-009: USB problems: a port that shut a device off (power surge, failed to start), repeated hub errors in the
/// event log, and on laptops a USB device that keeps the PC from sleeping properly (USBHUB3 196).
/// </summary>
public sealed class Drv009UsbProblems : Check
{
    public const string Hub3 = "Microsoft-Windows-USB-USBHUB3";
    private const int DrainsPower = 196;
    private const int MinHubErrors = 5; // a few hub warnings after sleep are normal
    private const int MinDrains = 2; // one report can be a device that was busy at the wrong moment

    public override string Id => "DRV-009";
    public override Category Category => Category.Drivers;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Peripherals), nameof(Snapshot.Events)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Peripherals!.Value!;
        var failed = p.Usb.Where(u => u.Failed).ToList();
        if (failed.Count > 0)
            yield return F(Severity.Warning,
                failed.Count == 1 ? $"A USB port shut off its device: {failed[0].StatusName.ToLowerInvariant()}" : $"{failed.Count} USB ports have a failed device",
                string.Join("; ", failed.Take(5).Select(u => $"{u.Hub} port {u.Port}: {u.StatusName}{(u.VendorId != 0 ? $" ({Per003UsbSpeed.Name(u)})" : "")}")) + ".",
                failed.Any(u => u.Status == 4)
                    ? "Unplug the device: it drew more power than the port allows (a short, a damaged cable or a power-hungry drive). Try it in a powered hub; if the port stays dead, restart the PC."
                    : "Unplug it and plug it into another port, ideally directly (no hub). Try another cable. If it fails everywhere, the device or its driver is faulty.");

        var events = s.Events!.Value!.Events.Where(e => e.Provider is Hub3 or "usbhub").ToList();
        var errors = events.Where(e => e.Id != DrainsPower && e.Level is 1 or 2).ToList();
        if (errors.Count >= MinHubErrors)
            yield return F(Severity.Info, $"{N(errors.Count, "USB error")} in the event log",
                $"In the last {s.Events.Value.Days} days. Latest: {errors[0].Message ?? $"event {errors[0].Id}"}",
                "Look for a device that disconnects or a port that stops working. Update the chipset/USB driver from the PC maker, and try the device on another port or cable."
                + (p.UsbSelectiveSuspendAc == true || p.UsbSelectiveSuspendDc == true ? " If a device keeps dropping out, turn off USB selective suspend in the power plan's advanced settings." : ""));

        if (s.System?.Value is { IsLaptop: true })
        {
            var drains = events.Where(e => e.Id == DrainsPower).GroupBy(Device).Where(g => g.Count() >= MinDrains).OrderByDescending(g => g.Count()).ToList();
            if (drains.Count > 0)
                yield return F(Severity.Info, "A USB device keeps the laptop from sleeping properly",
                    string.Join("; ", drains.Take(3).Select(g => $"{Describe(g.Key, p)}: {N(g.Count(), "time")}")) + $" in {s.Events.Value.Days} days (Windows logged it draining power while idle).",
                    "Unplug it before closing the lid, or update its driver. Docks and USB display adapters are the usual cause of a warm bag and a flat battery.");
        }
    }

    private static string Device(LogEvent e) =>
        int.TryParse(e.Get("fid_idVendor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && int.TryParse(e.Get("fid_idProduct"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? $"{v:X4}:{d:X4}" : "";

    private static string Describe(string id, PeripheralsInfo p) =>
        id.Length == 0 ? "A USB device (not named in the event)" : p.Usb.FirstOrDefault(u => u.Id == id)?.Device is { } name ? $"{name} ({id})" : Vendors.FromHardwareId($"VID_{id.Split(':')[0]}") is { } vendor ? $"{vendor} device {id}" : $"USB device {id}";
}
