using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

public sealed class SystemCollector : Collector<SystemInfo>
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public override string Section => nameof(Snapshot.System);

    protected override SystemInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var os = Environment.OSVersion.Version; // correct thanks to the supportedOS entries in app.manifest
        var info = new SystemInfo
        {
            DeviceName = Environment.MachineName,
            UserName = Environment.UserName,
            Domain = Environment.UserDomainName,
            WindowsProductName = Reg.Str(RegistryHive.LocalMachine, CurrentVersion, "ProductName"),
            WindowsDisplayVersion = Reg.Str(RegistryHive.LocalMachine, CurrentVersion, "DisplayVersion")
                ?? Reg.Str(RegistryHive.LocalMachine, CurrentVersion, "ReleaseId")
                ?? Reg.Str(RegistryHive.LocalMachine, CurrentVersion, "CSDVersion"),
            EditionId = Reg.Str(RegistryHive.LocalMachine, CurrentVersion, "EditionID"),
            Build = (int)(Reg.Num(RegistryHive.LocalMachine, CurrentVersion, "CurrentBuildNumber") ?? os.Build),
            Ubr = (int)(Reg.Num(RegistryHive.LocalMachine, CurrentVersion, "UBR") ?? 0),
            MajorVersion = os.Major,
            MinorVersion = os.Minor,
            InstallDateUtc = Reg.Num(RegistryHive.LocalMachine, CurrentVersion, "InstallDate") is { } secs and > 0
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs) : null,
            Is64BitOs = Environment.Is64BitOperatingSystem,
            Architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"),
            UptimeSeconds = (long)(Native.UptimeMs() / 1000),
            FastStartupEnabled = Reg.Num(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled") is { } h ? h == 1 : null,
            PendingRebootReasons = PendingReboot(),
        };

        var cs = Wmi.Query("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem").FirstOrDefault();
        info.Manufacturer = cs?.Str("Manufacturer");
        info.Model = cs?.Str("Model");
        info.PcSystemType = cs?.Int("PCSystemType");
        info.SerialNumber = Wmi.Query("SELECT SerialNumber FROM Win32_BIOS").FirstOrDefault()?.Str("SerialNumber");
        info.HasBattery = Wmi.Query("SELECT DeviceID FROM Win32_Battery").Count > 0;
        return info;
    }

    /// <summary>The usual places Windows records "restart needed".</summary>
    private static List<string> PendingReboot()
    {
        var reasons = new List<string>();
        if (Reg.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("Windows component servicing");
        if (Reg.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update");
        if (Reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations") is string[] { Length: > 0 })
            reasons.Add(SystemInfo.PendingFileRenames);
        var active = Reg.Str(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName", "ComputerName");
        var pending = Reg.Str(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName");
        if (active is not null && pending is not null && !string.Equals(active, pending, StringComparison.OrdinalIgnoreCase))
            reasons.Add("computer rename");
        return reasons;
    }
}
