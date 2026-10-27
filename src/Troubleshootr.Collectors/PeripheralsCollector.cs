using System.Globalization;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Sound, printers, USB ports, Bluetooth and the camera/microphone privacy switches. Each part fails on its own.</summary>
public sealed class PeripheralsCollector : Collector<PeripheralsInfo>
{
    private const string MmDevices = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string ConsentStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string NameKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string DeviceDescKey = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private static readonly TimeSpan PrinterBudget = TimeSpan.FromSeconds(3); // network printers can make WMI wait

    public override string Section => nameof(Snapshot.Peripherals);

    protected override PeripheralsInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new PeripheralsInfo();
        var printers = Task.Run(Printers, ct);
        var devices = PresentDevices();
        ct.ThrowIfCancellationRequested();

        try { Audio(info); }
        catch (Exception e) when (e is not OperationCanceledException) { info.AudioUnavailable = e.Message; }
        try { info.Usb = Usb.Ports(devices.Names, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { info.UsbUnavailable = e.Message; }
        UsbSuspend(info);
        Bluetooth(info, devices.BluetoothRadios);
        info.Camera = Privacy("webcam", "LetAppsAccessCamera");
        info.Camera.Devices = devices.Cameras;
        info.Microphone = Privacy("microphone", "LetAppsAccessMicrophone");
        info.Microphone.Devices = info.Audio.Where(a => !a.Playback && !a.IsLoopback && a.State == AudioEndpoint.Active).Select(a => a.Device is { } d ? $"{a.Name} ({d})" : a.Name).ToList();
        (info.SpoolerStatus, info.SpoolerStartType) = Service("Spooler");

        try
        {
            if (printers.Wait((int)PrinterBudget.TotalMilliseconds, ct)) info.Printers = printers.Result;
            else info.PrintersUnavailable = $"timed out after {PrinterBudget.TotalSeconds:0} s (a network printer may not be answering)";
        }
        catch (AggregateException e) { info.PrintersUnavailable = e.InnerException?.Message ?? e.Message; }
        return info;
    }

    private sealed record Devices(Dictionary<string, string> Names, List<string> Cameras, List<string> BluetoothRadios);

    /// <summary>One pass over present devices: names by driver key (for the USB ports), cameras and Bluetooth radios.</summary>
    private static Devices PresentDevices()
    {
        var result = new Devices(new(StringComparer.OrdinalIgnoreCase), new(), new());
        var set = SetupApi.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, SetupApi.DigcfAllClasses | SetupApi.DigcfPresent);
        if (set == SetupApi.Invalid) return result;
        try
        {
            var data = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupApi.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var cls = SetupApi.Property(set, ref data, SetupApi.SpdrpClass);
                var id = SetupApi.InstanceId(set, ref data) ?? "";
                var name = SetupApi.Property(set, ref data, SetupApi.SpdrpFriendlyName) ?? SetupApi.Property(set, ref data, SetupApi.SpdrpDeviceDesc) ?? "";
                if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && SetupApi.Property(set, ref data, SetupApi.SpdrpDriver) is { } key)
                {
                    if (Usb.IsGenericName(name) && SetupApi.StringProperty(set, ref data, SetupApi.BusReportedDeviceDesc) is { } reported) name = reported;
                    result.Names[key] = name;
                }
                // Webcams are class Camera (older ones Image); network printers' scanners are Image too, so only USB Image devices count.
                if (cls is "Camera" || (cls is "Image" && id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                                        && name.IndexOf("scan", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("print", StringComparison.OrdinalIgnoreCase) < 0))
                    result.Cameras.Add(name);
                // The radio itself, not the devices paired to it (those enumerate under BTHENUM/BTHLE/BTHHFENUM).
                if (cls is "Bluetooth" && !id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) && !id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)
                    && name.IndexOf("Enumerator", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("RFCOMM", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Microsoft Bluetooth LE", StringComparison.OrdinalIgnoreCase) < 0)
                    result.BluetoothRadios.Add(name);
            }
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static void Audio(PeripheralsInfo info)
    {
        var defaults = DefaultEndpoints();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Reg.NativeView);
        foreach (var (flow, playback) in new[] { ("Render", true), ("Capture", false) })
        {
            using var root = hklm.OpenSubKey($@"{MmDevices}\{flow}");
            if (root is null) continue;
            foreach (var guid in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(guid);
                // The high bits are flags; the low nibble is the DEVICE_STATE.
                var state = (k?.GetValue("DeviceState") is int s ? s : 0) & 0xF;
                if (state is 0 or AudioEndpoint.NotPresent) continue; // leftovers from hardware that's gone
                using var props = k!.OpenSubKey("Properties");
                info.Audio.Add(new AudioEndpoint
                {
                    Name = (props?.GetValue(NameKey) as string)?.Trim() ?? "Audio device",
                    Device = (props?.GetValue(DeviceDescKey) as string)?.Trim() is { Length: > 0 } d ? d : null,
                    Playback = playback,
                    State = state,
                    IsDefault = defaults.Contains(guid),
                });
            }
        }
        info.Audio = info.Audio.OrderByDescending(a => a.Playback).ThenByDescending(a => a.IsDefault).ThenBy(a => a.State).ThenBy(a => a.Name).ToList();
    }

    // Core Audio: only the calls needed to ask which endpoints are the defaults. Methods are declared in vtable order.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    /// <summary>The registry key names ({guid}) of the default playback and recording endpoints.</summary>
    private static HashSet<string> DefaultEndpoints()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                foreach (var flow in new[] { 0, 1 }) // eRender, eCapture; role eConsole
                {
                    if (enumerator.GetDefaultAudioEndpoint(flow, 0, out var device) != 0 || device is null) continue;
                    // "{0.0.0.00000000}.{guid}": the part after the dot is the MMDevices registry key name.
                    if (device.GetId(out var id) == 0 && id.LastIndexOf('.') is var dot and > 0) ids.Add(id.Substring(dot + 1));
                    Marshal.ReleaseComObject(device);
                }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException) { } // audio service not running
        return ids;
    }

    private static List<PrinterInfo> Printers()
    {
        var list = new List<PrinterInfo>();
        foreach (var r in Wmi.Query("SELECT Name, Default, Network, WorkOffline, PrinterStatus, DetectedErrorState, PortName, DriverName FROM Win32_Printer"))
            list.Add(new PrinterInfo
            {
                Name = r.Str("Name") ?? "Printer",
                Driver = r.Str("DriverName"),
                Port = r.Str("PortName"),
                IsDefault = r["Default"] is true,
                IsNetwork = r["Network"] is true,
                Offline = r["WorkOffline"] is true,
                Status = (int?)r.Long("PrinterStatus"),
                ErrorState = (int?)r.Long("DetectedErrorState"),
            });
        // Win32_PrintJob.Name is "<printer>, <job id>". A broken queue mustn't lose the printer list.
        List<WmiRow> jobs;
        try { jobs = Wmi.Query("SELECT Name, TimeSubmitted, StatusMask FROM Win32_PrintJob"); }
        catch (CollectorException) { jobs = []; }
        foreach (var j in jobs)
        {
            var name = j.Str("Name") ?? "";
            var comma = name.LastIndexOf(',');
            var printer = list.FirstOrDefault(p => comma > 0 && string.Equals(p.Name, name.Substring(0, comma).Trim(), StringComparison.OrdinalIgnoreCase));
            if (printer is null) continue;
            printer.Jobs++;
            if (Wmi.ParseCimDate(j.Str("TimeSubmitted")) is { } submitted)
            {
                var minutes = (int)(DateTime.Now - submitted).TotalMinutes;
                if (printer.OldestJobMinutes is not { } old || minutes > old) printer.OldestJobMinutes = Math.Max(0, minutes);
            }
            const long ErrorBits = 0x1 | 0x2 | 0x20 | 0x40 | 0x200 | 0x400; // paused, error, offline, paper out, blocked, needs the user
            if (((j.Long("StatusMask") ?? 0) & ErrorBits) != 0) printer.ErrorJobs++;
        }
        return list.OrderByDescending(p => p.IsDefault).ThenBy(p => p.IsVirtual).ThenBy(p => p.Name).ToList();
    }

    private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid SelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    private static void UsbSuspend(PeripheralsInfo info)
    {
        try
        {
            if (PerfApi.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return;
            var scheme = Marshal.PtrToStructure<Guid>(ptr);
            PerfApi.LocalFree(ptr);
            var sub = UsbSubgroup;
            var setting = SelectiveSuspend;
            if (PerfApi.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac) == 0) info.UsbSelectiveSuspendAc = ac == 1;
            if (PerfApi.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc) == 0) info.UsbSelectiveSuspendDc = dc == 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public int dwSize;
        public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected, fRemembered, fAuthenticated;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] stLastSeen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr radio);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothIsConnectable(IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr find, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr find);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    private static void Bluetooth(PeripheralsInfo info, List<string> radios)
    {
        var bt = info.Bluetooth;
        bt.Adapters = radios;
        (bt.ServiceStatus, bt.ServiceStartType) = Service("bthserv");
        try
        {
            var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            var find = BluetoothFindFirstRadio(ref p, out var radio);
            bt.RadioFound = find != IntPtr.Zero;
            if (find == IntPtr.Zero) return;
            BluetoothFindRadioClose(find);
            try
            {
                bt.Connectable = BluetoothIsConnectable(radio);
                bt.Paired = PairedCount(radio);
            }
            finally { CloseHandle(radio); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { } // no Bluetooth stack (Windows Server, N editions)
    }

    private static int PairedCount(IntPtr radio)
    {
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(), fReturnAuthenticated = 1, fReturnRemembered = 1, hRadio = radio,
        };
        var device = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
        var find = BluetoothFindFirstDevice(ref search, ref device);
        if (find == IntPtr.Zero) return 0;
        var count = 1;
        try
        {
            while (count < 500 && BluetoothFindNextDevice(find, ref device)) count++;
        }
        finally { BluetoothFindDeviceClose(find); }
        return count;
    }

    private static PrivacySwitch Privacy(string capability, string policy) => new()
    {
        Device = Reg.Str(RegistryHive.LocalMachine, $@"{ConsentStore}\{capability}", "Value"),
        Apps = Reg.Str(RegistryHive.CurrentUser, $@"{ConsentStore}\{capability}", "Value"),
        DesktopApps = Reg.Str(RegistryHive.CurrentUser, $@"{ConsentStore}\{capability}\NonPackaged", "Value"),
        Policy = (int?)Reg.Num(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", policy),
    };

    private static (string? Status, string? StartType) Service(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return (sc.Status.ToString(), sc.StartType.ToString());
        }
        catch (InvalidOperationException) { return (null, null); } // not installed
        catch (System.ComponentModel.Win32Exception) { return (null, null); }
    }
}
