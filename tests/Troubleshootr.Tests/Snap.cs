using Troubleshootr.Core;

namespace Troubleshootr.Tests;

/// <summary>Builders for small hand-made snapshots. Start from Snap.Healthy() and change only what the test is about.</summary>
public static class Snap
{
    public static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    public static Snapshot Healthy() => new()
    {
        Meta = new SnapshotMeta { CollectedUtc = Now, ToolVersion = "test", IsAdmin = true },
        System = Section<SystemInfo>.Of(new SystemInfo
        {
            DeviceName = "TEST-PC", Manufacturer = "Contoso", Model = "Desk 1", PcSystemType = 1, HasBattery = false,
            WindowsProductName = "Windows 10 Pro", WindowsDisplayVersion = "25H2", EditionId = "Professional",
            MajorVersion = 10, Build = 26200, Ubr = 6000, Is64BitOs = true, UptimeSeconds = 3600,
        }),
        Memory = Section<MemoryInfo>.Of(new MemoryInfo
        {
            InstalledMB = 16384, UsableMB = 16200, AvailableMB = 9000, CommitUsedMB = 9000, CommitLimitMB = 20000, SlotCount = 2,
            Modules =
            [
                new() { CapacityMB = 8192, SpeedMts = 3200, ConfiguredSpeedMts = 3200, PartNumber = "F4-3200C16-8GVKB", Locator = "DIMM1", Type = "DDR4" },
                new() { CapacityMB = 8192, SpeedMts = 3200, ConfiguredSpeedMts = 3200, PartNumber = "F4-3200C16-8GVKB", Locator = "DIMM2", Type = "DDR4" },
            ],
        }),
        Board = Section<BoardInfo>.Of(new BoardInfo { BiosVendor = "AMI", BiosVersion = "1.0", BiosDate = Now.AddYears(-1) }),
        Volumes = Section<List<VolumeInfo>>.Of(
        [
            new() { Root = @"C:\", DriveType = "Fixed", TotalBytes = 500L * GB, FreeBytes = 200L * GB, IsSystem = true },
        ]),
        Cpu = Section<CpuInfo>.Of(new CpuInfo { Packages = [new() { Name = "Test CPU", Cores = 8, Threads = 16 }] }),
        Gpus = Section<List<GpuInfo>>.Of([new() { Name = "Test GPU" }]),
    };

    public const long GB = 1073741824L;

    public static Snapshot With(this Snapshot s, Action<Snapshot> change) { change(s); return s; }
    public static SystemInfo Sys(this Snapshot s) => s.System!.Value!;
    public static MemoryInfo Mem(this Snapshot s) => s.Memory!.Value!;
}
