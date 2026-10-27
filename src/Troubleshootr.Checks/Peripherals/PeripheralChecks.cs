using Troubleshootr.Core;

namespace Troubleshootr.Checks.Peripherals;

/// <summary>PER-001: no working speakers or headphones (the "no sound" ticket), or every microphone disabled.</summary>
public sealed class Per001Audio : Check
{
    public override string Id => "PER-001";
    public override Category Category => Category.Peripherals;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Peripherals)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Peripherals!.Value!;
        if (p.AudioUnavailable is not null) yield break;
        var output = p.Audio.Where(a => a.Playback).ToList();
        if (!output.Any(a => a.State == AudioEndpoint.Active))
        {
            var disabled = output.Where(a => a.State == AudioEndpoint.Disabled).ToList();
            var unplugged = output.Where(a => a.State == AudioEndpoint.Unplugged).ToList();
            if (disabled.Count > 0)
                yield return F(Severity.Warning, "Sound output is disabled", $"No playback device is ready. Disabled: {List(disabled)}.",
                    "Settings > System > Sound > All sound devices: open the speakers or headphones and choose Allow. Then pick it as the output.");
            else if (unplugged.Count > 0)
                yield return F(Severity.Warning, "No speakers or headphones are plugged in", $"Every playback device is unplugged: {List(unplugged)}.",
                    "Plug the speakers or headphones in firmly (the right jack, often green). If they are plugged in, the jack's sensing may be faulty: try another jack or a USB/Bluetooth headset.");
            else if (s.System?.Value is not { IsServer: true } and not { IsVirtualMachine: true })
                yield return F(Severity.Warning, "No sound output device", "Windows has no speakers, headphones or HDMI audio device.",
                    "Check Device Manager under Sound for a device with a problem, and install the audio driver (Realtek/Intel/AMD) from the PC maker's support page.");
        }
        var mics = p.Audio.Where(a => !a.Playback && !a.IsLoopback).ToList();
        if (mics.Count > 0 && !mics.Any(a => a.State == AudioEndpoint.Active) && mics.Where(a => a.State == AudioEndpoint.Disabled).ToList() is { Count: > 0 } off)
            yield return F(Severity.Info, "The microphone is disabled", $"Disabled recording devices: {List(off)}.",
                "Settings > System > Sound > All sound devices: open the microphone and choose Allow.");
    }

    private static string List(IEnumerable<AudioEndpoint> a) => string.Join(", ", a.Take(5).Select(x => x.Device is { } d ? $"{x.Name} ({d})" : x.Name));
}

/// <summary>PER-002: printing broken: the spooler stopped, jobs stuck in a queue, the default printer offline or out of paper.</summary>
public sealed class Per002Printers : Check
{
    private const int StuckMinutes = 30;

    public override string Id => "PER-002";
    public override Category Category => Category.Peripherals;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Peripherals)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Peripherals!.Value!;
        var real = p.Printers.Where(x => !x.IsVirtual).ToList();
        // A disabled spooler is WIN-010's finding; a stopped one with printers installed means nothing prints.
        // The printer list comes through the spooler, so when it has crashed the list is usually empty: an Automatic spooler that's stopped is enough.
        if (p.SpoolerStatus is { } status && status != "Running" && p.SpoolerStartType != "Disabled"
            && (p.SpoolerStartType == "Automatic" || real.Count > 0 || p.PrintersUnavailable is not null))
            yield return F(Severity.Warning, "The Print Spooler service isn't running", $"Print Spooler is {status} (start type {p.SpoolerStartType}).",
                "Start it in Services (services.msc). If it keeps stopping, clear C:\\Windows\\System32\\spool\\PRINTERS with the service stopped, then remove and re-add the printer whose driver crashes it.", fix: "spooler-restart");

        foreach (var x in real.Where(x => x.ErrorJobs > 0 || (x.Jobs > 0 && x.OldestJobMinutes >= StuckMinutes)))
            yield return F(Severity.Warning, $"Print jobs are stuck on {x.Name}",
                $"{N(x.Jobs, "job")} in the queue{(x.OldestJobMinutes is { } m ? $", the oldest {Age(m)}" : "")}{(x.ErrorJobs > 0 ? $", {x.ErrorJobs} in error" : "")}.",
                "Open the printer's queue and cancel the stuck jobs. If they won't go, restart the Print Spooler service. Then check the printer is on, has paper and is on the same network.", fix: "spooler-restart");

        if (real.FirstOrDefault(x => x.IsDefault) is { } def && IsOffline(def))
            yield return F(Severity.Warning, $"The default printer is offline: {def.Name}",
                $"{def.Name} ({(def.IsNetwork ? "network" : def.Port)}) is {(def.Offline ? "set to work offline" : "reported offline")}.",
                "Turn the printer on and check its cable or Wi-Fi. In its queue window untick Printer > Use Printer Offline. A network printer that changed its IP address needs its port updated, or remove and re-add it.");

        var problems = real.Where(x => PrinterInfo.ErrorText(x.ErrorState) is { Length: > 0 } && x.ErrorState != 9).ToList();
        if (problems.Count > 0)
            yield return F(Severity.Info, problems.Count == 1 ? $"{problems[0].Name} needs attention" : $"{problems.Count} printers need attention",
                string.Join("; ", problems.Select(x => $"{x.Name}: {PrinterInfo.ErrorText(x.ErrorState)}")) + ".",
                "Check the printer's own screen: paper, ink or toner, doors, and jams.");
    }

    private static bool IsOffline(PrinterInfo x) => x.Offline || x.Status == 7 || x.ErrorState == 9;

    private static string Age(int minutes) => minutes >= 1440 ? $"{minutes / 1440} d old" : minutes >= 60 ? $"{minutes / 60} h old" : $"{minutes} min old";
}

/// <summary>PER-003: a USB 3 device running at USB 2 speed (wrong port, a USB 2 cable or hub, or a dirty connector).</summary>
public sealed class Per003UsbSpeed : Check
{
    public override string Id => "PER-003";
    public override Category Category => Category.Peripherals;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Peripherals)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        // Hubs are left out: a USB 3 hub's USB 2 half always shows up as a USB 3-capable hub at USB 2 speed.
        var slow = s.Peripherals!.Value!.Usb.Where(u => u.Status == UsbPort.Connected && !u.IsHub && u.SuperSpeedCapable == true && u.Speed < 3).ToList();
        if (slow.Count == 0) yield break;
        var onUsb3Port = slow.Any(u => u.PortSupportsUsb3 == true);
        yield return F(Severity.Info, slow.Count == 1 ? $"A USB 3 device is running at USB 2 speed: {Name(slow[0])}" : $"{slow.Count} USB 3 devices are running at USB 2 speed",
            string.Join("; ", slow.Take(5).Select(u => $"{Name(u)} on {u.Hub} port {u.Port}: {u.SpeedName}{(u.PortSupportsUsb3 == false ? " (the port is USB 2 only)" : "")}")) + ".",
            onUsb3Port
                ? "The port supports USB 3, so the cable, an extension or a hub in between is USB 2, or the connector is dirty or worn. Try a short USB 3 cable plugged in directly."
                : "Plug it into a USB 3 port (often blue, or marked SS) for up to 10x faster transfers.");
    }

    internal static string Name(UsbPort u) => u.Device ?? $"USB device {u.Id}";
}

/// <summary>PER-004: a monitor set below its native resolution, so everything looks blurry.</summary>
public sealed class Per004MonitorResolution : Check
{
    public override string Id => "PER-004";
    public override Category Category => Category.Peripherals;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Displays)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        foreach (var d in s.Displays!.Value!.Displays)
        {
            if (d.NativeWidth <= 0 || d.NativeHeight <= 0 || d.Width <= 0) continue;
            var landscape = d.Width == d.NativeWidth && d.Height == d.NativeHeight;
            var portrait = d.Width == d.NativeHeight && d.Height == d.NativeWidth;
            if (landscape || portrait || (long)d.Width * d.Height >= (long)d.NativeWidth * d.NativeHeight) continue;
            var name = d.DisplayName is { Length: > 0 } n ? n : "A monitor";
            yield return F(Severity.Info, $"{name} isn't at its native resolution",
                $"Set to {d.Width} × {d.Height}; the panel is {d.NativeWidth} × {d.NativeHeight}{(d.SizeInches > 0 ? $" ({d.SizeInches:0.#}\")" : "")}.",
                "Settings > System > Display: pick the resolution marked (Recommended), and use Scale to make text bigger instead. If it isn't offered, install the graphics driver or try another cable/port.");
        }
    }
}

/// <summary>PER-005: the PC has a Bluetooth adapter but the Bluetooth service is disabled, so nothing can pair or connect.</summary>
public sealed class Per005Bluetooth : Check
{
    public override string Id => "PER-005";
    public override Category Category => Category.Peripherals;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Peripherals)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var bt = s.Peripherals!.Value!.Bluetooth;
        if (bt.Adapters.Count == 0 || bt.ServiceStartType != "Disabled") yield break;
        yield return F(Severity.Warning, "The Bluetooth service is disabled", $"{bt.Adapters[0]} is installed, but the Bluetooth Support Service is disabled.",
            "In Services (services.msc) set Bluetooth Support Service to Manual and start it. Tune-up tools often disable it.");
    }
}

/// <summary>PER-006: Windows privacy switches or a policy block the camera or microphone (the "camera doesn't work in Zoom" ticket).</summary>
public sealed class Per006PrivacyBlocks : Check
{
    public override string Id => "PER-006";
    public override Category Category => Category.Peripherals;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Peripherals)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var p = s.Peripherals!.Value!;
        foreach (var (what, sw, settings) in new[] { ("camera", p.Camera, "Camera"), ("microphone", p.Microphone, "Microphone") })
        {
            if (!sw.Blocked) continue;
            var off = new List<string>();
            if (sw.PolicyOff) off.Add("blocked by a policy (work or school management)");
            if (sw.DeviceOff) off.Add($"\"{settings} access\" is off for the whole PC");
            if (sw.AppsOff) off.Add($"\"Let apps access your {what}\" is off");
            if (sw.DesktopAppsOff) off.Add($"\"Let desktop apps access your {what}\" is off (Zoom, Teams, Discord, browsers)");
            var devices = sw.Devices.Count > 0 ? $" {(what == "camera" ? "Camera" : "Microphone")}: {string.Join(", ", sw.Devices.Take(3))}." : "";
            yield return F(Severity.Warning, $"Apps can't use the {what}", string.Join("; ", off) + "." + devices,
                sw.PolicyOff ? $"The block comes from a policy; on a managed PC ask its IT admin. Otherwise remove LetAppsAccess{settings} under HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy."
                             : $"Settings > Privacy & security > {settings}: turn on {settings} access, Let apps access your {what}, and Let desktop apps access your {what}.");
        }
    }
}
