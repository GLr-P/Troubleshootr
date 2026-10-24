using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Troubleshootr.Collectors.Support;

/// <summary>Device handles and IOCTLs for disks and volumes. Every call reports failure instead of throwing past the caller.</summary>
public static class DeviceIo
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint FsctlIsVolumeDirty = 0x00090078;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>Opens a device with the least access that works: query-only first, then read/write (admin).</summary>
    public static SafeFileHandle? Open(string path, bool readWrite)
    {
        var h = CreateFile(path, readWrite ? GenericRead | GenericWrite : 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>Sends an IOCTL; returns the output bytes, or null with the Win32 error message.</summary>
    public static byte[]? Ioctl(SafeFileHandle h, uint code, byte[]? input, int outputSize, out string? error)
    {
        var output = new byte[outputSize];
        if (DeviceIoControl(h, code, input, input?.Length ?? 0, output, outputSize, out var returned, IntPtr.Zero))
        {
            error = null;
            if (returned < output.Length) Array.Resize(ref output, returned);
            return output;
        }
        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return null;
    }

    /// <summary>
    /// NVMe SMART/health log (page 02h) through IOCTL_STORAGE_QUERY_PROPERTY with StorageDeviceProtocolSpecificProperty,
    /// as documented in "Working with NVMe drives" (learn.microsoft.com). Works with the inbox stornvme driver; some vendor
    /// drivers and USB enclosures refuse it.
    /// </summary>
    public static byte[]? NvmeHealthLog(int diskNumber, out string? error)
    {
        const int QueryHeader = 8;          // STORAGE_PROPERTY_QUERY: PropertyId, QueryType, then AdditionalParameters
        const int ProtocolData = 40;        // sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA) on Windows 10 1903+ SDKs
        const int LogSize = 512;
        var buffer = new byte[QueryHeader + ProtocolData + LogSize];
        void Put(int offset, int value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);
        Put(0, 50);                         // StorageDeviceProtocolSpecificProperty
        Put(4, 0);                          // PropertyStandardQuery
        Put(QueryHeader + 0, 3);            // ProtocolTypeNvme
        Put(QueryHeader + 4, 2);            // NVMeDataTypeLogPage
        Put(QueryHeader + 8, 2);            // NVME_LOG_PAGE_HEALTH_INFO
        Put(QueryHeader + 12, 0);           // sub value
        Put(QueryHeader + 16, ProtocolData);// ProtocolDataOffset (from the start of the protocol data)
        Put(QueryHeader + 20, LogSize);     // ProtocolDataLength

        error = null;
        foreach (var readWrite in new[] { false, true })
        {
            using var h = Open($@"\\.\PhysicalDrive{diskNumber}", readWrite);
            if (h is null) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; continue; }
            var output = Ioctl(h, IoctlStorageQueryProperty, buffer, buffer.Length, out error);
            if (output is null || output.Length < QueryHeader + 24) continue;
            // Response: STORAGE_PROTOCOL_DATA_DESCRIPTOR = Version, Size, then the protocol data echoed back.
            var offset = BitConverter.ToInt32(output, QueryHeader + 16);
            var length = BitConverter.ToInt32(output, QueryHeader + 20);
            if (length < LogSize || QueryHeader + offset + LogSize > output.Length) { error = $"unexpected NVMe reply (offset {offset}, length {length})"; continue; }
            var log = new byte[LogSize];
            Array.Copy(output, QueryHeader + offset, log, 0, LogSize);
            return log;
        }
        return null;
    }

    /// <summary>Which disk(s) a volume like "C:\" lives on, and where it starts. Works without admin.</summary>
    public static List<(int Disk, long Offset)> VolumeExtents(string root)
    {
        const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
        var result = new List<(int, long)>();
        using var h = Open(@"\\.\" + root.TrimEnd('\\'), readWrite: false);
        if (h is null) return result;
        // VOLUME_DISK_EXTENTS: count (4) + padding (4), then DISK_EXTENT entries of 24 bytes: disk (4), padding (4), offset (8), length (8).
        var output = Ioctl(h, IoctlVolumeGetVolumeDiskExtents, null, 8 + 24 * 8, out _);
        if (output is null || output.Length < 8) return result;
        var count = BitConverter.ToInt32(output, 0);
        for (var i = 0; i < count && 8 + i * 24 + 24 <= output.Length; i++)
            result.Add((BitConverter.ToInt32(output, 8 + i * 24), BitConverter.ToInt64(output, 8 + i * 24 + 8)));
        return result;
    }

    /// <summary>NTFS dirty bit for a volume like "C:\". Null if it can't be read.</summary>
    public static bool? VolumeDirty(string root)
    {
        using var h = Open(@"\\.\" + root.TrimEnd('\\'), readWrite: false);
        if (h is null) return null;
        var output = Ioctl(h, FsctlIsVolumeDirty, null, 4, out _);
        return output is { Length: 4 } ? (BitConverter.ToUInt32(output, 0) & 1) != 0 : null;
    }
}
