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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_3
    {
        public string name; public IntPtr password; public uint password_age; public uint priv; public IntPtr home_dir; public IntPtr comment;
        public uint flags; public IntPtr script_path; public uint auth_flags; public IntPtr full_name; public IntPtr usr_comment; public IntPtr parms;
        public IntPtr workstations; public uint last_logon; public uint last_logoff; public uint acct_expires; public uint max_storage; public uint units_per_week;
        public IntPtr logon_hours; public uint bad_pw_count; public uint num_logons; public IntPtr logon_server; public uint country_code; public uint code_page;
        public uint user_id; public uint primary_group_id; public IntPtr profile; public IntPtr home_dir_drive; public uint password_expired;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? server, int level, int filter, out IntPtr buffer, int maxLength, out int read, out int total, ref int resume);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>Local user accounts (level 3: name, admin, flags, RID, last logon). Null if the call is refused.</summary>
    public static List<USER_INFO_3>? LocalUsers()
    {
        var resume = 0;
        if (NetUserEnum(null, 3, 2 /* FILTER_NORMAL_ACCOUNT */, out var buffer, -1, out var read, out _, ref resume) != 0) return null;
        try
        {
            var size = Marshal.SizeOf<USER_INFO_3>();
            return Enumerable.Range(0, read).Select(i => Marshal.PtrToStructure<USER_INFO_3>(buffer + i * size)).ToList();
        }
        finally { NetApiBufferFree(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TPM_DEVICE_INFO { public uint structVersion; public uint tpmVersion; public uint tpmInterfaceType; public uint tpmImpRevision; }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TPM_DEVICE_INFO info);

    /// <summary>"2.0", "1.2", "" when there's no TPM, null when unknown (Windows 7, or TBS missing). Works without admin.</summary>
    public static string? TpmVersion()
    {
        try
        {
            var r = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TPM_DEVICE_INFO>(), out var info);
            if (r == 0x8028400F) return "";  // TBS_E_TPM_NOT_FOUND
            if (r != 0) return null;
            return info.tpmVersion switch { 2 => "2.0", 1 => "1.2", var v => $"version {v}" };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
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
