namespace Troubleshootr.Core;

/// <summary>Sound devices, printers, the USB tree, Bluetooth and the camera/microphone privacy switches.</summary>
public sealed class PeripheralsInfo
{
    /// <summary>Every playback and recording endpoint Windows knows (MMDevices registry), with the default ones marked.</summary>
    public List<AudioEndpoint> Audio { get; set; } = new();
    public string? AudioUnavailable { get; set; }

    public List<PrinterInfo> Printers { get; set; } = new();
    public string? PrintersUnavailable { get; set; }
    /// <summary>Print Spooler service: Running, Stopped… (null if not installed).</summary>
    public string? SpoolerStatus { get; set; }
    public string? SpoolerStartType { get; set; }

    /// <summary>One entry per occupied USB port (hubs included), from the hub drivers.</summary>
    public List<UsbPort> Usb { get; set; } = new();
    public string? UsbUnavailable { get; set; }
    /// <summary>Power plan "USB selective suspend": true = enabled (null if the setting doesn't exist).</summary>
    public bool? UsbSelectiveSuspendAc { get; set; }
    public bool? UsbSelectiveSuspendDc { get; set; }

    public BluetoothInfo Bluetooth { get; set; } = new();

    public PrivacySwitch Camera { get; set; } = new();
    public PrivacySwitch Microphone { get; set; } = new();
}

public sealed class AudioEndpoint
{
    /// <summary>"Speakers", "Headphones", "Microphone Array"… Bluetooth ones carry the owner's device name.</summary>
    [FreeText] public string Name { get; set; } = "";
    /// <summary>The sound card or device it belongs to ("Realtek(R) Audio").</summary>
    [FreeText] public string? Device { get; set; }
    /// <summary>True for playback (render), false for recording (capture).</summary>
    public bool Playback { get; set; }
    /// <summary>DEVICE_STATE: 1 active, 2 disabled, 4 not present, 8 unplugged.</summary>
    public int State { get; set; }
    public bool IsDefault { get; set; }

    public const int Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8;

    /// <summary>"Stereo Mix" and similar record what the PC plays; they aren't microphones.</summary>
    public bool IsLoopback => new[] { "Stereo Mix", "What U Hear", "Wave Out Mix", "Loopback", "Stereomix" }
        .Any(k => Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
    public string StateName => State switch { Active => "Ready", Disabled => "Disabled", Unplugged => "Unplugged", NotPresent => "Not present", _ => $"State {State}" };
}

public sealed class PrinterInfo
{
    /// <summary>Network printers can carry part of their MAC address in the name; the anonymizer masks it.</summary>
    public string Name { get; set; } = "";
    public string? Driver { get; set; }
    public string? Port { get; set; }
    public bool IsDefault { get; set; }
    public bool IsNetwork { get; set; }
    /// <summary>"Use printer offline" is ticked, or Windows marked it offline.</summary>
    public bool Offline { get; set; }
    /// <summary>Win32_Printer.PrinterStatus: 3 idle, 4 printing, 5 warming up, 6 stopped, 7 offline…</summary>
    public int? Status { get; set; }
    /// <summary>Win32_Printer.DetectedErrorState: 0/2 none, 3 low paper, 4 no paper, 5 low toner, 6 no toner, 7 door open, 8 jammed, 9 offline…</summary>
    public int? ErrorState { get; set; }
    public int Jobs { get; set; }
    /// <summary>Age of the oldest job in the queue, minutes.</summary>
    public int? OldestJobMinutes { get; set; }
    /// <summary>Jobs whose status says error, paused or blocked.</summary>
    public int ErrorJobs { get; set; }

    /// <summary>Microsoft Print to PDF, XPS, OneNote, Fax: software printers that never jam or go offline.</summary>
    public bool IsVirtual => Port is { } p && (p.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase) || p.StartsWith("SHRFAX", StringComparison.OrdinalIgnoreCase)
                                               || p.StartsWith("nul", StringComparison.OrdinalIgnoreCase) || p.IndexOf("OneNote", StringComparison.OrdinalIgnoreCase) >= 0
                                               || p.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
                            || Name.IndexOf("Print to PDF", StringComparison.OrdinalIgnoreCase) >= 0 || Name.IndexOf("XPS", StringComparison.OrdinalIgnoreCase) >= 0
                            || Name.IndexOf("OneNote", StringComparison.OrdinalIgnoreCase) >= 0 || Name.StartsWith("Fax", StringComparison.OrdinalIgnoreCase)
                            || Name.IndexOf("(Fax)", StringComparison.OrdinalIgnoreCase) >= 0;

    public static string ErrorText(int? state) => state switch
    {
        3 => "low on paper", 4 => "out of paper", 5 => "low on toner or ink", 6 => "out of toner or ink", 7 => "a door or cover is open",
        8 => "paper jam", 9 => "offline", 10 => "needs service", 11 => "output tray full", _ => "",
    };
}

public sealed class UsbPort
{
    /// <summary>The hub this port is on ("USB Root Hub (USB 3.0)").</summary>
    public string Hub { get; set; } = "";
    public int Port { get; set; }
    public string? Device { get; set; }
    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public bool IsHub { get; set; }
    /// <summary>USB_CONNECTION_STATUS: 1 connected; 2 failed to start, 3 general failure, 4 drew too much power, 5 not enough power, 6 not enough bandwidth, 10 resetting.</summary>
    public int Status { get; set; }
    /// <summary>0 low (1.5 Mb/s), 1 full (12 Mb/s), 2 high (480 Mb/s), 3 SuperSpeed (5 Gb/s+).</summary>
    public int Speed { get; set; }
    /// <summary>The device can do USB 3 (SuperSpeed). Null on Windows 7, which can't tell.</summary>
    public bool? SuperSpeedCapable { get; set; }
    /// <summary>The port itself supports USB 3.</summary>
    public bool? PortSupportsUsb3 { get; set; }

    public const int Connected = 1;
    public bool Failed => Status is 2 or 3 or 4 or 5 or 6;
    public string SpeedName => Speed switch { 0 => "USB 1 (low speed)", 1 => "USB 1.1 (12 Mb/s)", 2 => "USB 2 (480 Mb/s)", 3 => "USB 3 (5 Gb/s+)", _ => "" };
    public string StatusName => Status switch
    {
        1 => "Connected", 2 => "Failed to start", 3 => "General failure", 4 => "Drew too much power (power surge)", 5 => "Not enough power",
        6 => "Not enough bandwidth", 7 => "Too many hubs in a chain", 8 => "In a USB 1 hub", 9 => "Starting", 10 => "Resetting", _ => $"Status {Status}",
    };
    public string Id => $"{VendorId:X4}:{ProductId:X4}";
}

public sealed class BluetoothInfo
{
    /// <summary>Bluetooth adapters (radios) from Device Manager.</summary>
    public List<string> Adapters { get; set; } = new();
    /// <summary>Windows found a working radio (BluetoothFindFirstRadio). Null if the Bluetooth API isn't there.</summary>
    public bool? RadioFound { get; set; }
    /// <summary>The radio accepts connections (false when Bluetooth is switched off).</summary>
    public bool? Connectable { get; set; }
    /// <summary>Paired devices (count only: their names are personal).</summary>
    public int? Paired { get; set; }
    public string? ServiceStatus { get; set; }
    public string? ServiceStartType { get; set; }
}

/// <summary>Settings > Privacy > Camera (or Microphone): the three switches plus a policy.</summary>
public sealed class PrivacySwitch
{
    /// <summary>"Camera access" for the whole device (HKLM ConsentStore): "Allow" or "Deny".</summary>
    public string? Device { get; set; }
    /// <summary>"Let apps access your camera" (HKCU ConsentStore).</summary>
    public string? Apps { get; set; }
    /// <summary>"Let desktop apps access your camera" (HKCU ConsentStore\NonPackaged).</summary>
    public string? DesktopApps { get; set; }
    /// <summary>Group policy LetAppsAccessCamera/Microphone: 0 user decides, 1 force allow, 2 force deny.</summary>
    public int? Policy { get; set; }
    /// <summary>Present devices of this kind (Camera/Image class, or recording endpoints).</summary>
    public List<string> Devices { get; set; } = new();

    public bool DeviceOff => string.Equals(Device, "Deny", StringComparison.OrdinalIgnoreCase);
    public bool AppsOff => string.Equals(Apps, "Deny", StringComparison.OrdinalIgnoreCase);
    public bool DesktopAppsOff => string.Equals(DesktopApps, "Deny", StringComparison.OrdinalIgnoreCase);
    public bool PolicyOff => Policy == 2;
    public bool Blocked => DeviceOff || AppsOff || DesktopAppsOff || PolicyOff;
}

/// <summary>Monitor facts from its EDID block (the data every monitor sends over the cable).</summary>
public static class Edid
{
    public sealed class Info
    {
        public string? Maker { get; set; }
        public string? Name { get; set; }
        public int Year { get; set; }
        public int WidthMm { get; set; }
        public int HeightMm { get; set; }
        public int NativeWidth { get; set; }
        public int NativeHeight { get; set; }
        public int NativeHz { get; set; }
        /// <summary>Highest refresh rate from the range-limits block (0 if not given).</summary>
        public int MaxHz { get; set; }
        public double Inches => WidthMm > 0 && HeightMm > 0 ? Math.Sqrt(WidthMm * WidthMm + HeightMm * HeightMm) / 25.4 : 0;
    }

    private static readonly Dictionary<string, string> Makers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer", ["AOC"] = "AOC", ["AUO"] = "AU Optronics", ["BOE"] = "BOE", ["BNQ"] = "BenQ", ["CMN"] = "Innolux", ["DEL"] = "Dell",
        ["ENC"] = "Eizo", ["GSM"] = "LG", ["HPN"] = "HP", ["HWP"] = "HP", ["IVM"] = "Iiyama", ["LEN"] = "Lenovo", ["LGD"] = "LG Display",
        ["MEI"] = "Panasonic", ["MSI"] = "MSI", ["NEC"] = "NEC", ["PHL"] = "Philips", ["SAM"] = "Samsung", ["SDC"] = "Samsung Display",
        ["SEC"] = "Samsung", ["SHP"] = "Sharp", ["SNY"] = "Sony", ["VSC"] = "ViewSonic", ["AUS"] = "ASUS", ["GBT"] = "Gigabyte", ["APP"] = "Apple",
        ["CSO"] = "CSOT", ["IVO"] = "InfoVision", ["TCL"] = "TCL", ["HSD"] = "HannStar", ["HKC"] = "HKC", ["XMI"] = "Xiaomi", ["RKU"] = "Roku",
        ["VIZ"] = "Vizio", ["HIS"] = "Hisense", ["TSB"] = "Toshiba", ["KOA"] = "Konka", ["MS_"] = "Microsoft (wireless display)",
    };

    /// <summary>Parses a 128+ byte EDID base block; null if the header or checksum is wrong.</summary>
    public static Info? Parse(byte[]? e)
    {
        if (e is null || e.Length < 128 || e[0] != 0 || e[1] != 0xFF || e[7] != 0) return null;
        var sum = 0;
        for (var i = 0; i < 128; i++) sum += e[i];
        if ((sum & 0xFF) != 0) return null;
        var code = (e[8] << 8) | e[9];
        var pnp = new string([(char)('A' - 1 + ((code >> 10) & 31)), (char)('A' - 1 + ((code >> 5) & 31)), (char)('A' - 1 + (code & 31))]);
        var info = new Info
        {
            Maker = Makers.TryGetValue(pnp, out var m) ? m : pnp,
            Year = e[17] is > 0 and < 0xFF ? 1990 + e[17] : 0,
            WidthMm = e[21] * 10,
            HeightMm = e[22] * 10,
        };
        for (var d = 54; d <= 108; d += 18)
        {
            if (e[d] != 0 || e[d + 1] != 0)
            {
                if (info.NativeWidth != 0) continue; // the first detailed timing is the preferred (native) mode
                var clock = (e[d] | (e[d + 1] << 8)) * 10000L;
                var hActive = e[d + 2] | ((e[d + 4] & 0xF0) << 4);
                var hBlank = e[d + 3] | ((e[d + 4] & 0x0F) << 8);
                var vActive = e[d + 5] | ((e[d + 7] & 0xF0) << 4);
                var vBlank = e[d + 6] | ((e[d + 7] & 0x0F) << 8);
                info.NativeWidth = hActive;
                info.NativeHeight = vActive;
                var total = (long)(hActive + hBlank) * (vActive + vBlank);
                if (total > 0) info.NativeHz = (int)Math.Round(clock / (double)total);
                var wMm = e[d + 12] | ((e[d + 14] & 0xF0) << 4);
                var hMm = e[d + 13] | ((e[d + 14] & 0x0F) << 8);
                if (wMm > 0 && hMm > 0 && wMm < 5000 && hMm < 5000) { info.WidthMm = wMm; info.HeightMm = hMm; }
                continue;
            }
            switch (e[d + 3])
            {
                case 0xFC: info.Name = Text(e, d + 5); break;
                case 0xFD: info.MaxHz = e[d + 6] + ((e[d + 4] & 0x02) != 0 ? 255 : 0); break;
            }
        }
        return info;
    }

    private static string? Text(byte[] e, int start)
    {
        var chars = new List<char>();
        for (var i = start; i < start + 13 && e[i] != 0x0A; i++) chars.Add((char)e[i]);
        var s = new string(chars.ToArray()).Trim();
        return s.Length == 0 ? null : s;
    }
}
