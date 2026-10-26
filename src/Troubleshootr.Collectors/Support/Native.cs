using System.Runtime.InteropServices;

namespace Troubleshootr.Collectors.Support;

/// <summary>Every P/Invoke signature lives here. Functions newer than Windows 7 are wrapped so a missing export returns null.</summary>
public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsUserAnAdmin();

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, EntryPoint = "DnsQuery_W")]
    private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr list, int freeType);

    private const ushort DnsTypeA = 1;
    private const uint DnsQueryBypassCache = 0x8;

    /// <summary>Looks a name up through Windows' configured DNS servers, skipping the local cache. 0 = found, else a Win32/DNS error code (9003 = no such name, 1460 = timeout).</summary>
    public static int DnsLookupUncached(string name)
    {
        var code = DnsQuery(name, DnsTypeA, DnsQueryBypassCache, IntPtr.Zero, out var list, IntPtr.Zero);
        if (list != IntPtr.Zero) DnsRecordListFree(list, 1);
        return code;
    }

    public static MEMORYSTATUSEX? MemoryStatus()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m : null;
    }

    /// <summary>KB of RAM installed according to the firmware (SMBIOS). Null if the firmware tables are bad.</summary>
    public static ulong? InstalledMemoryKB() => GetPhysicallyInstalledSystemMemory(out var kb) ? kb : null;

    public static ulong UptimeMs() => GetTickCount64();

    /// <summary>"UEFI", "Legacy BIOS", or null (unknown, or Windows 7 which lacks the API).</summary>
    public static string? FirmwareType()
    {
        try
        {
            if (!GetFirmwareType(out var t)) return null;
            return t switch { 1 => "Legacy BIOS", 2 => "UEFI", _ => null };
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>True when the process runs elevated.</summary>
    public static bool IsElevated()
    {
        try { return IsUserAnAdmin(); }
        catch (EntryPointNotFoundException) { return false; }
    }
}
