using System.Runtime.InteropServices;
using System.Text;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Support;

/// <summary>
/// The USB tree straight from the hub drivers (the same IOCTLs as Microsoft's USBView), without admin: every hub's ports,
/// what's plugged in, the speed it runs at and whether it could go faster. Parsers are separate so tests can feed bytes.
/// </summary>
public static class Usb
{
    private static Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const uint GetNodeInformation = 0x220408;
    private const uint GetDriverKeyName = 0x220420;
    private const uint GetConnectionInfoEx = 0x220448;
    private const uint GetHubInformationEx = 0x220454;
    private const uint GetConnectionInfoExV2 = 0x22045C;
    private const int ConnectionInfoSize = 512; // the fixed part is 35 bytes; the rest holds the pipe list

    private static readonly string[] GenericNames = ["USB Composite Device", "USB Input Device", "USB Mass Storage Device", "Generic USB Hub", "USB Root Hub", "Generic SuperSpeed USB Hub", "USB Root Hub (USB 3.0)"];

    /// <summary>Port details for every occupied port. <paramref name="names"/> maps a driver key to a device name.</summary>
    public static List<UsbPort> Ports(IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        var ports = new List<UsbPort>();
        var set = SetupApi.SetupDiGetClassDevsW(ref HubInterface, null, IntPtr.Zero, SetupApi.DigcfPresent | SetupApi.DigcfDeviceInterface);
        if (set == SetupApi.Invalid) throw new CollectorException($"USB hubs: SetupAPI error {Marshal.GetLastWin32Error()}");
        try
        {
            var iface = new SetupApi.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupApi.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HubInterface, i, ref iface); i++)
            {
                ct.ThrowIfCancellationRequested();
                var data = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
                if (SetupApi.InterfacePath(set, ref iface, ref data) is not { } path) continue;
                var hubName = SetupApi.Property(set, ref data, SetupApi.SpdrpFriendlyName) ?? SetupApi.Property(set, ref data, SetupApi.SpdrpDeviceDesc) ?? "USB hub";
                using var hub = DeviceIo.Open(path, readWrite: false) ?? DeviceIo.Open(path, readWrite: true);
                if (hub is null) continue;
                var count = PortCount(hub);
                for (var port = 1; port <= count; port++)
                {
                    var input = BitConverter.GetBytes(port);
                    Array.Resize(ref input, ConnectionInfoSize);
                    var info = DeviceIo.Ioctl(hub, GetConnectionInfoEx, input, ConnectionInfoSize, out _);
                    if (info is null || ParseConnection(info) is not { } p) continue;
                    p.Hub = hubName;
                    p.Port = port;
                    if (V2(hub, port) is { } v2)
                    {
                        p.PortSupportsUsb3 = v2.PortUsb3;
                        p.SuperSpeedCapable = v2.DeviceCapable;
                        if (v2.Operating) p.Speed = 3; // the EX call reports SuperSpeed as high speed for compatibility
                    }
                    if (DriverKey(hub, port) is { } key && names.TryGetValue(key, out var name)) p.Device = name;
                    ports.Add(p);
                }
            }
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
        return ports;
    }

    public static bool IsGenericName(string name) => GenericNames.Any(g => name.Equals(g, StringComparison.OrdinalIgnoreCase));

    private static int PortCount(Microsoft.Win32.SafeHandles.SafeFileHandle hub)
    {
        // USB_HUB_INFORMATION_EX (Windows 8+): HubType (4 bytes), then HighestPortNumber (2).
        if (DeviceIo.Ioctl(hub, GetHubInformationEx, null, 128, out _) is { Length: >= 6 } ex) return BitConverter.ToUInt16(ex, 4);
        // USB_NODE_INFORMATION: NodeType (4), then the hub descriptor, whose third byte is the port count.
        return DeviceIo.Ioctl(hub, GetNodeInformation, null, 128, out _) is { Length: >= 7 } node ? node[6] : 0;
    }

    /// <summary>USB_NODE_CONNECTION_INFORMATION_EX (packed): null when nothing is plugged into the port.</summary>
    public static UsbPort? ParseConnection(byte[] b)
    {
        if (b.Length < 35) return null;
        var status = BitConverter.ToInt32(b, 31);
        if (status == 0) return null; // NoDeviceConnected
        return new UsbPort
        {
            VendorId = BitConverter.ToUInt16(b, 12),
            ProductId = BitConverter.ToUInt16(b, 14),
            Speed = b[23],
            IsHub = b[24] != 0,
            Status = status,
        };
    }

    /// <summary>USB_NODE_CONNECTION_INFORMATION_EX_V2 (Windows 8+): port supports USB 3, device can do it, device runs at it.</summary>
    public static (bool PortUsb3, bool DeviceCapable, bool Operating)? ParseV2(byte[] b)
    {
        if (b.Length < 16) return null;
        var protocols = BitConverter.ToUInt32(b, 8);
        var flags = BitConverter.ToUInt32(b, 12);
        return ((protocols & 4) != 0, (flags & 2) != 0, (flags & 1) != 0);
    }

    private static (bool PortUsb3, bool DeviceCapable, bool Operating)? V2(Microsoft.Win32.SafeHandles.SafeFileHandle hub, int port)
    {
        var input = new byte[16];
        BitConverter.GetBytes(port).CopyTo(input, 0);
        BitConverter.GetBytes(16).CopyTo(input, 4);
        BitConverter.GetBytes(7).CopyTo(input, 8); // ask about USB 1.1, 2 and 3
        return DeviceIo.Ioctl(hub, GetConnectionInfoExV2, input, 16, out _) is { } b ? ParseV2(b) : null;
    }

    private static string? DriverKey(Microsoft.Win32.SafeHandles.SafeFileHandle hub, int port)
    {
        var input = new byte[8];
        BitConverter.GetBytes(port).CopyTo(input, 0);
        if (DeviceIo.Ioctl(hub, GetDriverKeyName, input, 1024, out _) is not { Length: > 8 } b) return null;
        var name = Encoding.Unicode.GetString(b, 8, b.Length - 8);
        var end = name.IndexOf('\0');
        return (end >= 0 ? name.Substring(0, end) : name).Trim() is { Length: > 0 } key ? key : null;
    }
}
