using System.Runtime.InteropServices;
using System.Text;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Support;

/// <summary>
/// Native Wi-Fi (wlanapi.dll): the current connection of each Wi-Fi adapter. Structures are read by byte offset
/// (documented layouts, natural alignment) so nothing depends on marshalling nested fixed arrays.
/// Windows 11 24H2+ refuses SSID/BSSID unless location access is allowed for desktop apps; that comes back as an error code.
/// </summary>
public static class Wlan
{
    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out int size, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")] private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

    private const int OpCurrentConnection = 7;
    private const int OpChannelNumber = 8;
    private const int OpRssi = 0x10000102;
    private const int InterfaceInfoSize = 532;   // GUID + WCHAR[256] + state
    private const int BssEntrySize = 360;

    private static readonly string[] States = ["not ready", "connected", "ad hoc", "disconnecting", "disconnected", "associating", "discovering", "authenticating"];

    /// <summary>One entry per Wi-Fi adapter. Throws CollectorException with the Win32 error if the WLAN service isn't running.</summary>
    public static List<WifiConnection> Connections()
    {
        var result = new List<WifiConnection>();
        IntPtr handle;
        try
        {
            var err = WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (err == 1062) return result;  // ERROR_SERVICE_NOT_ACTIVE: WLAN AutoConfig is off (desktops without Wi-Fi)
            if (err != 0) throw new CollectorException($"WlanOpenHandle error {err}");
        }
        catch (DllNotFoundException) { return result; }  // Server editions without the Wireless LAN feature

        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return result;
            try
            {
                var count = Marshal.ReadInt32(list, 0);
                for (var i = 0; i < count; i++)
                {
                    var item = list + 8 + i * InterfaceInfoSize;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    var w = new WifiConnection
                    {
                        Adapter = Marshal.PtrToStringUni(item + 16),
                        State = State(Marshal.ReadInt32(item, 16 + 512)),
                    };
                    if (w.Connected) ReadConnection(handle, guid, w);
                    result.Add(w);
                }
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
        return result;
    }

    private static string State(int s) => s >= 0 && s < States.Length ? States[s] : $"state {s}";

    private static void ReadConnection(IntPtr handle, Guid guid, WifiConnection w)
    {
        byte[]? ssid = null;
        byte[]? bssid = null;
        var err = WlanQueryInterface(handle, ref guid, OpCurrentConnection, IntPtr.Zero, out _, out var conn, IntPtr.Zero);
        if (err != 0)
        {
            w.Problem = err == 5
                ? "Windows hides Wi-Fi details from apps until location access is on (Settings > Privacy & security > Location > Let desktop apps access your location)."
                : $"Wi-Fi details couldn't be read (error {err}).";
            return;
        }
        {
            try
            {
                // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile WCHAR[256], then WLAN_ASSOCIATION_ATTRIBUTES at 520.
                const int a = 520;
                var ssidLen = Math.Min(32, Marshal.ReadInt32(conn, a));
                ssid = new byte[ssidLen];
                Marshal.Copy(conn + a + 4, ssid, 0, ssidLen);
                bssid = new byte[6];
                Marshal.Copy(conn + a + 40, bssid, 0, 6);
                w.Ssid = Encoding.UTF8.GetString(ssid);
                w.Bssid = string.Join(":", bssid.Select(b => b.ToString("x2")));
                w.PhyType = Marshal.ReadInt32(conn, a + 48);
                w.SignalPercent = Marshal.ReadInt32(conn, a + 56);
                w.RxMbps = Marshal.ReadInt32(conn, a + 60) / 1000;
                w.TxMbps = Marshal.ReadInt32(conn, a + 64) / 1000;
                w.Auth = Marshal.ReadInt32(conn, a + 76);
            }
            finally { WlanFreeMemory(conn); }
        }
        if (WlanQueryInterface(handle, ref guid, OpChannelNumber, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0)
        {
            w.Channel = Marshal.ReadInt32(ch);
            WlanFreeMemory(ch);
        }
        if (WlanQueryInterface(handle, ref guid, OpRssi, IntPtr.Zero, out _, out var rssi, IntPtr.Zero) == 0)
        {
            w.Rssi = Marshal.ReadInt32(rssi);
            WlanFreeMemory(rssi);
        }
        if (bssid is not null) w.FrequencyMHz = Frequency(handle, guid, bssid);
    }

    /// <summary>The connected access point's centre frequency, from the BSS list (the only way to tell 5 GHz from 6 GHz channels).</summary>
    private static int? Frequency(IntPtr handle, Guid guid, byte[] bssid)
    {
        if (WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 3, false, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            var count = Marshal.ReadInt32(list, 4);
            for (var i = 0; i < count; i++)
            {
                var e = list + 8 + i * BssEntrySize;
                var mac = new byte[6];
                Marshal.Copy(e + 40, mac, 0, 6);
                if (mac.SequenceEqual(bssid)) return Marshal.ReadInt32(e, 92) / 1000;  // kHz
            }
            return null;
        }
        finally { WlanFreeMemory(list); }
    }
}
