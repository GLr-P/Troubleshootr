using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Every device (present and hidden) with its problem code and driver, plus kernel driver services and signing mode.</summary>
public sealed class DevicesCollector : Collector<DevicesInfo>
{
    private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class";
    private static readonly string[] KeepHiddenClasses = ["Display", "Net", "DiskDrive", "SCSIAdapter", "HDC"];

    public override string Section => nameof(Snapshot.Devices);

    protected override DevicesInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new DevicesInfo();
        // One open of the Class key for all devices: opening the registry root per value made this take seconds.
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Reg.NativeView);
        using var classes = hklm.OpenSubKey(ClassRoot);
        // Without DIGCF_PRESENT the list includes devices that were connected once; CM_Get_DevNode_Status tells them apart.
        var set = SetupApi.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, SetupApi.DigcfAllClasses);
        if (set == SetupApi.Invalid) throw new CollectorException($"SetupAPI: error {Marshal.GetLastWin32Error()}");
        try
        {
            var data = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupApi.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                ct.ThrowIfCancellationRequested();
                var present = SetupApi.CM_Get_DevNode_Status(out var status, out var problem, data.DevInst, 0) == SetupApi.CrSuccess;
                var cls = SetupApi.Property(set, ref data, SetupApi.SpdrpClass);
                if (present && cls is null && SetupApi.InstanceId(set, ref data) is @"HTREE\ROOT\0") continue; // the device tree's root node
                if (!present)
                {
                    info.HiddenCount++;
                    if (cls is null || !KeepHiddenClasses.Contains(cls, StringComparer.OrdinalIgnoreCase)) continue;
                }
                var d = new DeviceEntry
                {
                    Name = SetupApi.Property(set, ref data, SetupApi.SpdrpFriendlyName) ?? SetupApi.Property(set, ref data, SetupApi.SpdrpDeviceDesc) ?? "Unknown device",
                    Class = cls,
                    InstanceId = SetupApi.InstanceId(set, ref data),
                    HardwareId = SetupApi.Property(set, ref data, SetupApi.SpdrpHardwareId),
                    Manufacturer = SetupApi.Property(set, ref data, SetupApi.SpdrpMfg),
                    Service = SetupApi.Property(set, ref data, SetupApi.SpdrpService),
                    ProblemCode = present ? ((status & SetupApi.DnHasProblem) != 0 ? (int)problem : 0) : DeviceProblems.NotPresent,
                };
                if (classes is not null && SetupApi.Property(set, ref data, SetupApi.SpdrpDriver) is { } driverKey) Driver(classes, d, driverKey);
                (present ? info.Devices : info.Hidden).Add(d);
            }
        }
        finally { SetupApi.SetupDiDestroyDeviceInfoList(set); }

        info.Devices = info.Devices.OrderBy(d => d.Class ?? "~").ThenBy(d => d.Name).ToList();
        info.KernelDrivers = KernelDrivers(hklm);
        var options = Reg.Str(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "SystemStartOptions") ?? "";
        info.TestSigning = options.IndexOf("TESTSIGNING", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IntegrityChecksOff = options.IndexOf("DISABLE_INTEGRITY_CHECKS", StringComparison.OrdinalIgnoreCase) >= 0;
        return info;
    }

    /// <summary>Version, date, provider and INF from the device's driver key (Control\Class\{guid}\0000).</summary>
    private static void Driver(RegistryKey classes, DeviceEntry d, string key)
    {
        try
        {
            using var k = classes.OpenSubKey(key);
            if (k is null) return;
            d.DriverProvider = (k.GetValue("ProviderName") as string)?.Trim();
            d.DriverVersion = (k.GetValue("DriverVersion") as string)?.Trim();
            d.Inf = (k.GetValue("InfPath") as string)?.Trim();
            d.DriverDate = ParseDriverDate(k.GetValue("DriverDate") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>DriverDate is "M-D-YYYY" (e.g. "6-21-2006").</summary>
    internal static DateTime? ParseDriverDate(string? s) =>
        s is not null && DateTime.TryParseExact(s, ["M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;

    /// <summary>Kernel and file-system driver services (Type 1 or 2) and their .sys files.</summary>
    private static List<KernelDriver> KernelDrivers(RegistryKey hklm)
    {
        var list = new List<KernelDriver>();
        using var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return list;
        foreach (var name in services.GetSubKeyNames())
        {
            try
            {
                using var k = services.OpenSubKey(name);
                if (k?.GetValue("Type") is not (1 or 2)) continue;
                var image = k.GetValue("ImagePath") as string;
                list.Add(new KernelDriver
                {
                    Name = name,
                    File = image is { Length: > 0 } ? Path.GetFileName(image.Trim('"')) : null,
                    Start = k.GetValue("Start") as int?,
                });
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list;
    }
}
