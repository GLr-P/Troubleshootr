using Troubleshootr.Checks;
using Troubleshootr.Checks.Drivers;
using Troubleshootr.Checks.Peripherals;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>Phase 10: sound, printers, USB, monitors (EDID), Bluetooth, camera/microphone privacy, USB events.</summary>
public class PeripheralTests
{
    private static List<Finding> Run(ICheck check, Snapshot s) => check.Run(s).ToList();
    private static PeripheralsInfo Per(Snapshot s) => s.Peripherals!.Value!;
    private static Snapshot With(Action<PeripheralsInfo> change) => Snap.Healthy().With(s => change(Per(s)));

    private static byte[] Hex(string h) => Enumerable.Range(0, h.Length / 2).Select(i => Convert.ToByte(h.Substring(i * 2, 2), 16)).ToArray();

    // Real EDIDs from the owner's laptop (serial-number descriptors are part of the bytes but never read).
    private const string AcerV226 = "00FFFFFFFFFFFF0004722D030D1630421718010380301B782A84D5A25A52A2260D5054B30C00D1C0B30095008180810081C0714F0101023A801871382D40582C4500DD0C1100001E000000FD00374C1E5311000A202020202020000000FF004C584C4141303035343231300A000000FC0041636572205632323648514C0A000A";
    private const string LaptopPanel = "00FFFFFFFFFFFF0006AFB05400000000071F0104A5221678034485AD5243B2260D505400000001010101010101010101010101010101E8FD00A0A0404C693020AA0058D7100000180000000F0000000000000000000000000020000000FD0C30A51F1F4E010A202020202020000000FE004231363051414E30322E4E200A0197";

    [Fact]
    public void EdidParsesRealMonitors()
    {
        var acer = Edid.Parse(Hex(AcerV226))!;
        Assert.Equal("Acer", acer.Maker);
        Assert.Equal("Acer V226HQL", acer.Name);
        Assert.Equal((1920, 1080, 60), (acer.NativeWidth, acer.NativeHeight, acer.NativeHz));
        Assert.Equal(21.5, Math.Round(acer.Inches, 1));
        Assert.Equal(76, acer.MaxHz);
        Assert.Equal(2014, acer.Year);

        var panel = Edid.Parse(Hex(LaptopPanel))!;
        Assert.Equal("AU Optronics", panel.Maker);
        Assert.Null(panel.Name);
        Assert.Equal((2560, 1600), (panel.NativeWidth, panel.NativeHeight));
        Assert.Equal(165, panel.MaxHz);
        Assert.Equal(16, Math.Round(panel.Inches));

        var broken = Hex(AcerV226);
        broken[100] ^= 0xFF;
        Assert.Null(Edid.Parse(broken)); // checksum
        Assert.Null(Edid.Parse(new byte[10]));
    }

    [Fact]
    public void UsbConnectionInfoParses()
    {
        var b = new byte[512];
        BitConverter.GetBytes(3).CopyTo(b, 0);            // port
        BitConverter.GetBytes((ushort)0x0781).CopyTo(b, 12); // SanDisk
        BitConverter.GetBytes((ushort)0x5583).CopyTo(b, 14);
        b[23] = 2;                                          // high speed
        BitConverter.GetBytes(1).CopyTo(b, 31);             // connected
        var p = Usb.ParseConnection(b)!;
        Assert.Equal("0781:5583", p.Id);
        Assert.Equal(2, p.Speed);
        Assert.False(p.IsHub);
        BitConverter.GetBytes(0).CopyTo(b, 31);
        Assert.Null(Usb.ParseConnection(b));                // empty port

        var v2 = new byte[16];
        BitConverter.GetBytes(7u).CopyTo(v2, 8);            // port does USB 1.1, 2, 3
        BitConverter.GetBytes(2u).CopyTo(v2, 12);           // device SuperSpeed-capable, not running at it
        Assert.Equal((true, true, false), Usb.ParseV2(v2));
    }

    [Fact]
    public void HealthyPeripheralsHaveNoFindings()
    {
        var s = Snap.Healthy();
        foreach (var c in new ICheck[] { new Per001Audio(), new Per002Printers(), new Per003UsbSpeed(), new Per004MonitorResolution(), new Per005Bluetooth(), new Per006PrivacyBlocks(), new Drv009UsbProblems() })
            Assert.Empty(Run(c, s));
    }

    [Fact]
    public void Per001NoSound()
    {
        var disabled = Assert.Single(Run(new Per001Audio(), With(p => p.Audio[0].State = AudioEndpoint.Disabled)));
        Assert.Equal("Sound output is disabled", disabled.Title);
        Assert.Equal("No speakers or headphones are plugged in", Assert.Single(Run(new Per001Audio(), With(p => p.Audio[0].State = AudioEndpoint.Unplugged))).Title);
        Assert.Equal("No sound output device", Assert.Single(Run(new Per001Audio(), With(p => p.Audio.RemoveAt(0)))).Title);
        // A virtual machine with no sound card is normal.
        Assert.Empty(Run(new Per001Audio(), With(p => p.Audio.RemoveAt(0)).With(s => s.Sys().Manufacturer = "VMware, Inc.")));
        Assert.Equal(Severity.Info, Assert.Single(Run(new Per001Audio(), With(p => p.Audio[1].State = AudioEndpoint.Disabled))).Severity);
        Assert.Empty(Run(new Per001Audio(), With(p => { p.Audio.Clear(); p.AudioUnavailable = "audio service stopped"; })));
    }

    [Fact]
    public void Per002Printing()
    {
        Assert.Equal("The Print Spooler service isn't running", Assert.Single(Run(new Per002Printers(), With(p => p.SpoolerStatus = "Stopped"))).Title);
        Assert.Empty(Run(new Per002Printers(), With(p => { p.SpoolerStatus = "Stopped"; p.SpoolerStartType = "Disabled"; }))); // WIN-010's finding
        var stuck = Assert.Single(Run(new Per002Printers(), With(p => { p.Printers[0].Jobs = 3; p.Printers[0].OldestJobMinutes = 180; })));
        Assert.Contains("3 jobs in the queue, the oldest 3 h old", stuck.Evidence);
        Assert.Empty(Run(new Per002Printers(), With(p => { p.Printers[0].Jobs = 1; p.Printers[0].OldestJobMinutes = 2; }))); // printing now
        Assert.Equal("The default printer is offline: Office printer", Assert.Single(Run(new Per002Printers(), With(p => p.Printers[0].Offline = true))).Title);
        Assert.Empty(Run(new Per002Printers(), With(p => { p.Printers[0].IsDefault = false; p.Printers[1].IsDefault = true; p.Printers[0].ErrorState = 9; }))); // a non-default printer that's off
        Assert.Contains("out of paper", Assert.Single(Run(new Per002Printers(), With(p => p.Printers[0].ErrorState = 4))).Evidence);
    }

    [Fact]
    public void Per003Usb3AtUsb2()
    {
        var f = Assert.Single(Run(new Per003UsbSpeed(), With(p => p.Usb.Add(new() { Hub = "USB Root Hub (USB 3.0)", Port = 2, Device = "Samsung T7", VendorId = 0x04E8, ProductId = 0x4001, Status = 1, Speed = 2, SuperSpeedCapable = true, PortSupportsUsb3 = true }))));
        Assert.Contains("Samsung T7 on USB Root Hub (USB 3.0) port 2: USB 2 (480 Mb/s)", f.Evidence);
        Assert.Contains("cable", f.Advice);
        Assert.Empty(Run(new Per003UsbSpeed(), With(p => p.Usb.Add(new() { Hub = "h", Port = 2, Status = 1, Speed = 3, SuperSpeedCapable = true }))));
    }

    [Fact]
    public void Per004NativeResolution()
    {
        var s = Snap.Healthy().With(x => Assign(x.Displays!.Value!.Displays[0], 1920, 1080, 2560, 1440));
        Assert.Contains("the panel is 2560 × 1440", Assert.Single(Run(new Per004MonitorResolution(), s)).Evidence);
        Assert.Empty(Run(new Per004MonitorResolution(), Snap.Healthy().With(x => Assign(x.Displays!.Value!.Displays[0], 1440, 2560, 2560, 1440)))); // portrait
        Assert.Empty(Run(new Per004MonitorResolution(), Snap.Healthy())); // no EDID
    }

    private static void Assign(DisplayInfo d, int w, int h, int nw, int nh) { d.Width = w; d.Height = h; d.NativeWidth = nw; d.NativeHeight = nh; d.MonitorModel = "DELL U2722D"; }

    [Fact]
    public void Per005And006()
    {
        Assert.Equal(Severity.Warning, Assert.Single(Run(new Per005Bluetooth(), With(p => p.Bluetooth.ServiceStartType = "Disabled"))).Severity);
        Assert.Empty(Run(new Per005Bluetooth(), With(p => { p.Bluetooth.Adapters.Clear(); p.Bluetooth.ServiceStartType = "Disabled"; })));

        var cam = Assert.Single(Run(new Per006PrivacyBlocks(), With(p => p.Camera.DesktopApps = "Deny")));
        Assert.Equal("Apps can't use the camera", cam.Title);
        Assert.Contains("desktop apps", cam.Evidence);
        Assert.Contains("HD Webcam", cam.Evidence);
        var mic = Assert.Single(Run(new Per006PrivacyBlocks(), With(p => p.Microphone.Policy = 2)));
        Assert.Contains("policy", mic.Advice);
    }

    [Fact]
    public void Drv009UsbEvents()
    {
        var surge = Assert.Single(Run(new Drv009UsbProblems(), With(p => p.Usb.Add(new() { Hub = "USB Root Hub (USB 3.0)", Port = 3, Status = 4 }))));
        Assert.Contains("power surge", surge.Title);

        static Dictionary<string, string> Mct(int _) => new() { ["fid_idVendor"] = "1809", ["fid_idProduct"] = "22528" };
        var laptop = Snap.Healthy().With(s => { s.Sys().PcSystemType = 2; }).Events(3, Drv009UsbProblems.Hub3, 196, level: 3, data: Mct);
        var drain = Assert.Single(Run(new Drv009UsbProblems(), laptop));
        Assert.Contains("Magic Control (USB display adapter) device 0711:5800: 3 times", drain.Evidence);
        Assert.Empty(Run(new Drv009UsbProblems(), Snap.Healthy().Events(3, Drv009UsbProblems.Hub3, 196, level: 3, data: Mct))); // desktops sleep plugged in
        Assert.Empty(Run(new Drv009UsbProblems(), Snap.Healthy().With(s => s.Sys().PcSystemType = 2).Events(1, Drv009UsbProblems.Hub3, 196, level: 3, data: Mct)));

        Assert.Single(Run(new Drv009UsbProblems(), Snap.Healthy().Events(6, Drv009UsbProblems.Hub3, 10, message: "port reset failed")));
        Assert.Empty(Run(new Drv009UsbProblems(), Snap.Healthy().Events(2, Drv009UsbProblems.Hub3, 10)));
    }

    [Fact]
    public void ReviewRegressions()
    {
        // A crashed spooler hides every printer, so an empty list mustn't silence the finding.
        Assert.Single(Run(new Per002Printers(), With(p => { p.Printers.Clear(); p.SpoolerStatus = "Stopped"; })));
        Assert.Empty(Run(new Per002Printers(), With(p => { p.Printers.Clear(); p.SpoolerStatus = "Stopped"; p.SpoolerStartType = "Manual"; })));
        // A USB 3 hub's USB 2 half isn't a slow device.
        Assert.Empty(Run(new Per003UsbSpeed(), With(p => p.Usb.Add(new() { Hub = "h", Port = 2, Device = "Generic SuperSpeed USB Hub", IsHub = true, Status = 1, Speed = 2, SuperSpeedCapable = true, PortSupportsUsb3 = true }))));
        // Stereo Mix records what the PC plays: it isn't a working microphone.
        Assert.Single(Run(new Per001Audio(), With(p => { p.Audio[1].State = AudioEndpoint.Disabled; p.Audio.Add(new() { Name = "Stereo Mix", Device = "Realtek(R) Audio", State = AudioEndpoint.Active }); })));
        // Events without device ids are still reported, under a plain name.
        var laptop = Snap.Healthy().With(s => s.Sys().PcSystemType = 2).Events(2, Drv009UsbProblems.Hub3, 196, level: 3);
        Assert.Contains("A USB device (not named in the event): 2 times", Assert.Single(Run(new Drv009UsbProblems(), laptop)).Evidence);
    }

    [Fact]
    public void AnonymizerStripsOwnerNamesFromSoundDevices()
    {
        var s = Snap.Healthy().With(x =>
        {
            Per(x).Audio.Add(new() { Name = "Speakers", Device = "Alexander’s iPhone Hands-Free HF Audio", Playback = true, State = 8 });
            Per(x).Printers.Add(new() { Name = "HP732C80 (HP OfficeJet 8020 series)" });
            Per(x).Microphone.Devices.Add("Microphone (Sam's AirPods Hands-Free)");
        });
        Anonymizer.Apply(s);
        Assert.Equal("[redacted]'s iPhone Hands-Free HF Audio", Per(s).Audio[2].Device);
        Assert.Equal("Realtek(R) Audio", Per(s).Audio[0].Device);
        Assert.Equal("HPXXXXXX (HP OfficeJet 8020 series)", Per(s).Printers[2].Name);
        Assert.Equal("Microphone ([redacted]'s AirPods Hands-Free)", Assert.Single(Per(s).Microphone.Devices));
    }
}
