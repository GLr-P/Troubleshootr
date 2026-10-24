using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Storage;

/// <summary>
/// Physical disks and their health. Sources, best first:
/// Windows Storage WMI (MSFT_Disk / MSFT_PhysicalDisk, Windows 8+), NVMe health log IOCTL, ATA SMART via root\wmi (admin),
/// MSFT_StorageReliabilityCounter (admin). Windows 7 falls back to Win32_DiskDrive.
/// </summary>
public sealed class StorageCollector : Collector<StorageInfo>
{
    public override string Section => nameof(Snapshot.Storage);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    protected override StorageInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new StorageInfo
        {
            TrimDisabled = Reg.Num(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "DisableDeleteNotification") is { } t ? t == 1 : null,
        };
        var legacy = Wmi.Query("SELECT Index, Model, SerialNumber, Size, InterfaceType, FirmwareRevision, PNPDeviceID, MediaType FROM Win32_DiskDrive");
        var disks = Wmi.QueryOptional("SELECT Number, Model, FriendlyName, SerialNumber, Size, BusType, IsBoot, FirmwareVersion, HealthStatus, OperationalStatus FROM MSFT_Disk", Wmi.Storage);
        var physical = Wmi.QueryOptional("SELECT DeviceId, MediaType, SpindleSpeed, BusType, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk", Wmi.Storage);

        if (disks.Count > 0)
        {
            foreach (var d in disks)
            {
                var number = d.Int("Number") ?? -1;
                var p = physical.FirstOrDefault(x => x.Str("DeviceId") == number.ToString());
                var l = legacy.FirstOrDefault(x => x.Int("Index") == number);
                info.Disks.Add(new DiskInfo
                {
                    Number = number,
                    Model = d.Str("Model") ?? d.Str("FriendlyName"),
                    SerialNumber = d.Str("SerialNumber"),
                    Firmware = d.Str("FirmwareVersion"),
                    BusType = BusName(d.Int("BusType") ?? p?.Int("BusType")),
                    MediaType = p?.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SSD", _ => null },
                    SpindleRpm = p?.Long("SpindleSpeed") is { } rpm and > 0 and < 100000 ? (int)rpm : null,
                    SizeBytes = d.Long("Size") ?? 0,
                    IsBoot = d.Bool("IsBoot") ?? false,
                    Health = HealthName(p?.Int("HealthStatus") ?? d.Int("HealthStatus")),
                    OperationalStatus = OpStatus(p?.UShorts("OperationalStatus") ?? d.UShorts("OperationalStatus")),
                    PnpDeviceId = l?.Str("PNPDeviceID"),
                });
            }
            foreach (var part in Wmi.QueryOptional("SELECT DiskNumber, PartitionNumber, DriveLetter, Offset, Size, Type FROM MSFT_Partition", Wmi.Storage))
            {
                var letter = part.Long("DriveLetter") is { } c and > 64 ? $"{(char)c}:" : null;
                info.Partitions.Add(new PartitionInfo
                {
                    DiskNumber = part.Int("DiskNumber") ?? -1, PartitionNumber = part.Int("PartitionNumber") ?? 0, DriveLetter = letter,
                    OffsetBytes = part.Long("Offset") ?? 0, SizeBytes = part.Long("Size") ?? 0, Type = part.Str("Type"),
                });
            }
            // MSFT_Partition needs admin; without it, map lettered volumes to disks with an IOCTL that doesn't.
            if (info.Partitions.Count == 0)
                foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType is DriveType.Fixed or DriveType.Removable))
                    foreach (var (disk, offset) in DeviceIo.VolumeExtents(drive.Name))
                        info.Partitions.Add(new PartitionInfo { DiskNumber = disk, DriveLetter = drive.Name.Substring(0, 2), OffsetBytes = offset });
            foreach (var disk in info.Disks)
                disk.DriveLetters = info.Partitions.Where(x => x.DiskNumber == disk.Number && x.DriveLetter is not null).Select(x => x.DriveLetter!).Distinct().ToList();
            foreach (var v in Wmi.QueryOptional("SELECT FriendlyName, HealthStatus, OperationalStatus, ResiliencySettingName FROM MSFT_VirtualDisk", Wmi.Storage))
                info.VirtualDisks.Add(new VirtualDiskInfo
                {
                    Name = v.Str("FriendlyName"), Health = HealthName(v.Int("HealthStatus")), Resiliency = v.Str("ResiliencySettingName"),
                    OperationalStatus = OpStatus(v.UShorts("OperationalStatus")),
                });
        }
        else
        {
            // Windows 7: no Storage WMI namespace.
            var windowsDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.Substring(0, 2);
            foreach (var l in legacy)
                info.Disks.Add(new DiskInfo
                {
                    Number = l.Int("Index") ?? -1,
                    Model = l.Str("Model"),
                    SerialNumber = l.Str("SerialNumber"),
                    Firmware = l.Str("FirmwareRevision"),
                    BusType = l.Str("InterfaceType") switch { "USB" => "USB", "IDE" => "ATA", var x => x },
                    SizeBytes = l.Long("Size") ?? 0,
                    PnpDeviceId = l.Str("PNPDeviceID"),
                    IsBoot = l.Int("Index") == 0 && windowsDrive is not null, // best guess without the Storage namespace
                });
        }

        AddHealth(info, ctx);
        return info;
    }

    private static void AddHealth(StorageInfo info, CollectContext ctx)
    {
        var reliability = ctx.IsAdmin ? TryQuery("SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, ReadErrorsTotal, ReadErrorsUncorrected, " +
                                                 "WriteErrorsTotal, WriteErrorsUncorrected, StartStopCycleCount, LoadUnloadCycleCount FROM MSFT_StorageReliabilityCounter", Wmi.Storage) : [];
        var smartData = ctx.IsAdmin ? TryQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", Wmi.RootWmi) : [];
        var smartThresholds = ctx.IsAdmin ? TryQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", Wmi.RootWmi) : [];
        var smartStatus = ctx.IsAdmin ? TryQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", Wmi.RootWmi) : [];

        foreach (var disk in info.Disks)
        {
            if (reliability.FirstOrDefault(r => r.Str("DeviceId") == disk.Number.ToString()) is { } r)
                disk.Reliability = new ReliabilityInfo
                {
                    TemperatureC = r.Int("Temperature"), TemperatureMaxC = r.Int("TemperatureMax"), Wear = r.Int("Wear"),
                    PowerOnHours = r.Long("PowerOnHours"), ReadErrorsTotal = r.Long("ReadErrorsTotal"), ReadErrorsUncorrected = r.Long("ReadErrorsUncorrected"),
                    WriteErrorsTotal = r.Long("WriteErrorsTotal"), WriteErrorsUncorrected = r.Long("WriteErrorsUncorrected"),
                    StartStopCycles = r.Long("StartStopCycleCount"), LoadUnloadCycles = r.Long("LoadUnloadCycleCount"),
                };

            string? nvmeError = null;
            if (disk.BusType == "NVMe" && DeviceIo.NvmeHealthLog(disk.Number, out nvmeError) is { } log)
                disk.Nvme = HealthParsers.ParseNvmeHealth(log);

            if (disk.PnpDeviceId is { } pnp && disk.BusType != "NVMe")
            {
                bool Match(WmiRow x) => x.Str("InstanceName") is { } n && n.StartsWith(pnp, StringComparison.OrdinalIgnoreCase);
                if (smartData.FirstOrDefault(Match)?["VendorSpecific"] is byte[] data)
                    disk.Ata = new AtaSmart
                    {
                        Attributes = HealthParsers.ParseAtaSmart(data, smartThresholds.FirstOrDefault(Match)?["VendorSpecific"] as byte[]),
                        PredictFailure = smartStatus.FirstOrDefault(Match)?.Bool("PredictFailure"),
                    };
            }

            // No SMART from Windows (USB enclosure, some RAID/vendor drivers): ask the drive directly through SCSI ATA pass-through.
            string? satError = null;
            if (disk.Ata is null && disk.BusType != "NVMe" && ctx.IsAdmin && disk.BusType is "USB" or "SATA" or "ATA" or "SCSI" or "RAID" or "SAS"
                && DeviceIo.SatSmartPage(disk.Number, 0xD0, out satError) is { } satData)
            {
                var satThresholds = DeviceIo.SatSmartPage(disk.Number, 0xD1, out _);
                disk.Ata = new AtaSmart { Attributes = HealthParsers.ParseAtaSmart(satData, satThresholds) };
            }

            if (disk.Nvme is null && disk.Ata is null && disk.Reliability is null)
                disk.HealthDataUnavailable = disk.BusType switch
                {
                    "USB" => $"the USB adapter or enclosure hides the drive's health data ({satError ?? (ctx.IsAdmin ? "no answer" : "run as admin to try reading it through the adapter")}); connect it directly to read it",
                    "RAID" => "the drive is behind a RAID controller",
                    "Virtual" or "FileBackedVirtual" or "StorageSpaces" => "virtual disk",
                    _ when !ctx.IsAdmin => "needs admin",
                    "NVMe" => $"the NVMe driver refused the health query ({nvmeError})",
                    _ => "the drive or its driver doesn't report SMART data",
                };
        }
    }

    /// <summary>Health queries are extras: a failure here shouldn't lose the disk list.</summary>
    private static List<WmiRow> TryQuery(string wql, string ns)
    {
        try { return Wmi.Query(wql, ns); }
        catch (CollectorException) { return []; }
    }

    /// <summary>STORAGE_BUS_TYPE values (MSFT_Disk.BusType).</summary>
    private static string? BusName(int? code) => code switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
        11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "FileBackedVirtual", 16 => "StorageSpaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        null => null, _ => $"bus {code}",
    };

    private static string? HealthName(int? code) => code switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", _ => null };

    private static List<string> OpStatus(ushort[]? codes) => codes?.Select(c => c switch
    {
        2 => "OK", 3 => "Degraded", 5 => "Predictive Failure", 6 => "Error", 7 => "Non-Recoverable Error", 10 => "Stopped", 11 => "In Service",
        13 => "Lost Communication", 0xD002 => "Failed Media", 0xD003 => "Split", 0xD008 => "Stale Metadata", 0xD00B => "Transient Error",
        0xD00C => "Removing From Pool", 0xD012 => "Threshold Exceeded", 0xD013 => "IO Error Reported", _ => $"status {c}",
    }).ToList() ?? new List<string>();
}
