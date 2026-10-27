using System.Runtime.InteropServices;
using System.Text;

namespace Troubleshootr.Collectors.Support;

/// <summary>SetupAPI and Configuration Manager: Device Manager's data without WMI (Windows 7+).</summary>
internal static class SetupApi
{
    public const uint DigcfPresent = 0x2;
    public const uint DigcfAllClasses = 0x4;
    public const uint SpdrpDeviceDesc = 0x0;
    public const uint SpdrpHardwareId = 0x1;
    public const uint SpdrpService = 0x4;
    public const uint SpdrpClass = 0x7;
    public const uint SpdrpDriver = 0x9;
    public const uint SpdrpMfg = 0xB;
    public const uint SpdrpFriendlyName = 0xC;
    public const uint DnHasProblem = 0x400;
    public const int CrSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType, byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    public const uint DigcfDeviceInterface = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc: the product name the device itself reports ("USB Receiver").</summary>
    public static readonly DEVPROPKEY BusReportedDeviceDesc = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid interfaceGuid, uint index, ref SP_DEVICE_INTERFACE_DATA iface);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[]? buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data, uint scope, uint profile, uint keyType, int access);

    /// <summary>The device path of an interface (to open with CreateFile), and the device it belongs to.</summary>
    public static string? InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface, ref SP_DEVINFO_DATA data)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out var size, ref data);
        if (size is < 6 or > 4096) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is the fixed part (8 on 64-bit, 6 on 32-bit), then the path.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetailW(set, ref iface, buffer, size, out _, ref data) ? Marshal.PtrToStringUni(buffer + 4) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string? StringProperty(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        try
        {
            SetupDiGetDevicePropertyW(set, ref data, ref key, out _, null, 0, out var size, 0);
            if (size is 0 or > 4096) return null;
            var buffer = new byte[size];
            if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, size, out _, 0)) return null;
            var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
            return text.Length == 0 ? null : text;
        }
        catch (EntryPointNotFoundException) { return null; } // Windows XP-era setupapi
    }

    /// <summary>The device's hardware key (Enum\...\Device Parameters), read-only. Null if it has none.</summary>
    public static Microsoft.Win32.RegistryKey? DeviceKey(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        const uint DicsFlagGlobal = 1, DiregDev = 1;
        const int KeyRead = 0x20019;
        var h = SetupDiOpenDevRegKey(set, ref data, DicsFlagGlobal, 0, DiregDev, KeyRead);
        if (h == Invalid || h == IntPtr.Zero) return null;
        return Microsoft.Win32.RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(h, ownsHandle: true));
    }

    public static readonly IntPtr Invalid = new(-1);

    /// <summary>A string or the first string of a multi-string property, or null.</summary>
    public static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out var size);
        if (size is 0 or > 65536) return null;
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, size, out _)) return null;
        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        var first = (end >= 0 ? text.Substring(0, end) : text).Trim();
        return first.Length == 0 ? null : first;
    }

    public static string? InstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : null;
    }
}
