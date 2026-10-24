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

    /// <summary>
    /// SMART READ DATA (feature D0h) or READ THRESHOLDS (D1h) sent as a SCSI "ATA PASS-THROUGH" command (SAT, T10 SAT-4),
    /// which most USB-to-SATA bridges and some RAID/SCSI drivers translate. Needs admin. Returns the 512-byte page only if
    /// its checksum is valid, so a bridge that ignores the command can't produce garbage values.
    /// </summary>
    public static byte[]? SatSmartPage(int diskNumber, byte feature, out string? error)
    {
        const uint IoctlScsiPassThrough = 0x0004D004;
        var x64 = IntPtr.Size == 8;
        // SCSI_PASS_THROUGH layout differs by pointer size (DataBufferOffset is a ULONG_PTR).
        int sptSize = x64 ? 56 : 44, dataOffsetField = x64 ? 24 : 20, senseOffsetField = x64 ? 32 : 24, cdbField = x64 ? 36 : 28;
        const int SenseSize = 32, DataSize = 512;
        int senseAt = sptSize, dataAt = sptSize + SenseSize;

        using var h = Open($@"\\.\PhysicalDrive{diskNumber}", readWrite: true);
        if (h is null) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return null; }

        // ATA PASS-THROUGH(16) first, then (12): bridges differ in which they accept.
        var cdbs = new[]
        {
            new byte[] { 0x85, 0x08, 0x0E, 0x00, feature, 0x00, 0x01, 0x00, 0x00, 0x00, 0x4F, 0x00, 0xC2, 0xA0, 0xB0, 0x00 },
            new byte[] { 0xA1, 0x08, 0x0E, feature, 0x01, 0x00, 0x4F, 0xC2, 0xA0, 0xB0, 0x00, 0x00 },
        };
        error = null;
        foreach (var cdb in cdbs)
        {
            var buf = new byte[dataAt + DataSize];
            BitConverter.GetBytes((ushort)sptSize).CopyTo(buf, 0);   // Length
            buf[6] = (byte)cdb.Length;                               // CdbLength
            buf[7] = SenseSize;                                      // SenseInfoLength
            buf[8] = 1;                                              // DataIn = SCSI_IOCTL_DATA_IN
            BitConverter.GetBytes(DataSize).CopyTo(buf, 12);         // DataTransferLength
            BitConverter.GetBytes(5).CopyTo(buf, 16);                // TimeOutValue (s)
            if (x64) BitConverter.GetBytes((long)dataAt).CopyTo(buf, dataOffsetField);
            else BitConverter.GetBytes(dataAt).CopyTo(buf, dataOffsetField);
            BitConverter.GetBytes(senseAt).CopyTo(buf, senseOffsetField);
            cdb.CopyTo(buf, cdbField);

            var output = Ioctl(h, IoctlScsiPassThrough, buf, buf.Length, out error);
            if (output is null || output.Length < dataAt + DataSize) continue;
            if (output[2] != 0) { error = $"SCSI status 0x{output[2]:X2}"; continue; }
            var page = new byte[DataSize];
            Array.Copy(output, dataAt, page, 0, DataSize);
            if (ValidSmartPage(page)) return page;
            error = "the bridge answered but the data wasn't a valid SMART page";
        }
        return null;
    }

    /// <summary>A SMART data/threshold page is valid if it isn't all zeros and its 512 bytes sum to 0 mod 256.</summary>
    public static bool ValidSmartPage(byte[] page)
    {
        if (page.Length != 512 || page.All(b => b == 0)) return false;
        var sum = 0;
        foreach (var b in page) sum += b;
        return (sum & 0xFF) == 0;
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
        // FSCTL_IS_VOLUME_DIRTY needs a read handle on the volume (admin).
        foreach (var readWrite in new[] { false, true })
        {
            using var h = Open(@"\\.\" + root.TrimEnd('\\'), readWrite);
            if (h is null) continue;
            var output = Ioctl(h, FsctlIsVolumeDirty, null, 4, out _);
            if (output is { Length: 4 }) return (BitConverter.ToUInt32(output, 0) & 1) != 0;
        }
        return null;
    }
}
