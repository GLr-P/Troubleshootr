using System.Runtime.InteropServices;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Active displays: which GPU drives each one, the mode in use and the best refresh rate on offer; per-app GPU choices.</summary>
public sealed class DisplaysCollector : Collector<DisplaysInfo>
{
    public override string Section => nameof(Snapshot.Displays);

    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;
    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE devMode);

    protected override DisplaysInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new DisplaysInfo();
        var adapter = NewDevice();
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref adapter, 0); i++, adapter = NewDevice())
        {
            if ((adapter.StateFlags & AttachedToDesktop) == 0) continue;
            var current = NewMode();
            if (!EnumDisplaySettingsW(adapter.DeviceName, EnumCurrentSettings, ref current)) continue;
            var max = current.dmDisplayFrequency;
            var mode = NewMode();
            for (var m = 0; m < 5000 && EnumDisplaySettingsW(adapter.DeviceName, m, ref mode); m++, mode = NewMode())
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmDisplayFrequency > max) max = mode.dmDisplayFrequency;
            var monitor = NewDevice();
            info.Displays.Add(new DisplayInfo
            {
                Adapter = adapter.DeviceString,
                Monitor = EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceString : null,
                Primary = (adapter.StateFlags & PrimaryDevice) != 0,
                Width = current.dmPelsWidth,
                Height = current.dmPelsHeight,
                RefreshHz = current.dmDisplayFrequency,
                MaxRefreshHz = max,
                BitsPerPixel = current.dmBitsPerPel,
            });
        }
        foreach (var (app, pref) in GpuPreferences())
            if (pref == 1) info.PowerSavingApps.Add(app);
        return info;
    }

    private static DISPLAY_DEVICE NewDevice() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    /// <summary>HKCU\Software\Microsoft\DirectX\UserGpuPreferences: value name = app path, data "GpuPreference=1;".</summary>
    private static IEnumerable<(string App, int Preference)> GpuPreferences()
    {
        RegistryKey? key = null;
        try { key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        if (key is null) yield break;
        using (key)
            foreach (var name in key.GetValueNames())
                if (name != "DirectXUserGlobalSettings" && key.GetValue(name) is string v
                    && System.Text.RegularExpressions.Regex.Match(v, @"GpuPreference=(\d)") is { Success: true } m)
                    yield return (name, m.Groups[1].Value[0] - '0');
    }
}
