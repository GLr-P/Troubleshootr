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
