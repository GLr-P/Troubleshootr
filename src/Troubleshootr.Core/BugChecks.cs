using System.Globalization;
using System.Text.RegularExpressions;

namespace Troubleshootr.Core;

/// <summary>What usually causes a bugcheck code, so patterns across crashes can point somewhere.</summary>
public enum CrashHint { Driver, Memory, Graphics, Storage, Hardware, Windows, Other }

/// <summary>CRA-007: the common bugcheck (blue screen) codes in plain English. Names are Microsoft's (learn.microsoft.com "Bug check code reference").</summary>
public static class BugChecks
{
    public sealed record Info(string Name, string Meaning, CrashHint Hint);

    private static readonly Dictionary<uint, Info> Codes = new()
    {
        [0x0A] = new("IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory it wasn't allowed to; usually a bad driver, sometimes bad RAM.", CrashHint.Driver),
        [0x19] = new("BAD_POOL_HEADER", "Kernel memory was corrupted, usually by a driver.", CrashHint.Driver),
        [0x1A] = new("MEMORY_MANAGEMENT", "Windows found its memory bookkeeping corrupted; often failing RAM or unstable memory overclock (XMP/EXPO).", CrashHint.Memory),
        [0x1E] = new("KMODE_EXCEPTION_NOT_HANDLED", "A kernel driver crashed.", CrashHint.Driver),
        [0x24] = new("NTFS_FILE_SYSTEM", "The NTFS file system hit an error; check the drive and run chkdsk.", CrashHint.Storage),
        [0x2E] = new("DATA_BUS_ERROR", "A parity error in memory; usually failing RAM.", CrashHint.Memory),
        [0x3B] = new("SYSTEM_SERVICE_EXCEPTION", "A driver crashed while handling a system call; graphics and antivirus drivers are common causes.", CrashHint.Driver),
        [0x3D] = new("INTERRUPT_EXCEPTION_NOT_HANDLED", "A driver crashed while handling an interrupt.", CrashHint.Driver),
        [0x3F] = new("NO_MORE_SYSTEM_PTES", "The kernel ran out of page table entries, usually a driver leak.", CrashHint.Driver),
        [0x44] = new("MULTIPLE_IRP_COMPLETE_REQUESTS", "A driver completed the same request twice.", CrashHint.Driver),
        [0x4A] = new("IRQL_GT_ZERO_AT_SYSTEM_SERVICE", "A driver returned to user mode in the wrong state.", CrashHint.Driver),
        [0x4E] = new("PFN_LIST_CORRUPT", "The memory page list was corrupted; often failing RAM, sometimes a driver.", CrashHint.Memory),
        [0x50] = new("PAGE_FAULT_IN_NONPAGED_AREA", "Invalid memory was referenced; bad RAM, a bad driver or antivirus.", CrashHint.Memory),
        [0x5C] = new("HAL_INITIALIZATION_FAILED", "Windows couldn't start the hardware layer; firmware or hardware problem.", CrashHint.Hardware),
        [0x76] = new("PROCESS_HAS_LOCKED_PAGES", "A driver didn't release memory it locked.", CrashHint.Driver),
        [0x77] = new("KERNEL_STACK_INPAGE_ERROR", "Kernel data couldn't be read back from the page file; failing drive or cable.", CrashHint.Storage),
        [0x7A] = new("KERNEL_DATA_INPAGE_ERROR", "Data couldn't be read from the page file; failing drive, loose cable or bad RAM.", CrashHint.Storage),
        [0x7B] = new("INACCESSIBLE_BOOT_DEVICE", "Windows lost access to the system drive while starting (storage driver, SATA mode change or failing drive).", CrashHint.Storage),
        [0x7E] = new("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A driver's system thread crashed.", CrashHint.Driver),
        [0x7F] = new("UNEXPECTED_KERNEL_MODE_TRAP", "The CPU hit a trap the kernel couldn't handle; hardware fault or overclock, sometimes a driver.", CrashHint.Hardware),
        [0x8E] = new("KERNEL_MODE_EXCEPTION_NOT_HANDLED", "A kernel driver crashed; often bad RAM too.", CrashHint.Driver),
        [0x9C] = new("MACHINE_CHECK_EXCEPTION", "The CPU reported a fatal hardware error.", CrashHint.Hardware),
        [0x9F] = new("DRIVER_POWER_STATE_FAILURE", "A driver didn't respond while the PC was going to or coming out of sleep.", CrashHint.Driver),
        [0xA0] = new("INTERNAL_POWER_ERROR", "A fatal power-management error, often during sleep or hibernation.", CrashHint.Driver),
        [0xA5] = new("ACPI_BIOS_ERROR", "The BIOS's power tables are broken; update the BIOS.", CrashHint.Hardware),
        [0xBE] = new("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "A driver tried to write to read-only memory.", CrashHint.Driver),
        [0xC1] = new("SPECIAL_POOL_DETECTED_MEMORY_CORRUPTION", "Driver Verifier caught a driver corrupting memory.", CrashHint.Driver),
        [0xC2] = new("BAD_POOL_CALLER", "A driver misused kernel memory.", CrashHint.Driver),
        [0xC4] = new("DRIVER_VERIFIER_DETECTED_VIOLATION", "Driver Verifier caught a driver breaking the rules (Verifier is on; turn it off after testing).", CrashHint.Driver),
        [0xC5] = new("DRIVER_CORRUPTED_EXPOOL", "A driver corrupted kernel memory.", CrashHint.Driver),
        [0xC7] = new("TIMER_OR_DPC_INVALID", "A driver left a timer behind in freed memory.", CrashHint.Driver),
        [0xCE] = new("DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS", "A driver unloaded without cleaning up.", CrashHint.Driver),
        [0xD1] = new("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory it wasn't allowed to; network, Wi-Fi and antivirus drivers are common causes.", CrashHint.Driver),
        [0xD5] = new("DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "A driver used memory after freeing it.", CrashHint.Driver),
        [0xE2] = new("MANUALLY_INITIATED_CRASH", "Someone forced the crash on purpose (keyboard shortcut or debugger).", CrashHint.Other),
        [0xEA] = new("THREAD_STUCK_IN_DEVICE_DRIVER", "The graphics driver got stuck waiting for the GPU.", CrashHint.Graphics),
        [0xED] = new("UNMOUNTABLE_BOOT_VOLUME", "Windows couldn't mount the system drive; file system damage or a failing drive.", CrashHint.Storage),
        [0xEF] = new("CRITICAL_PROCESS_DIED", "A process Windows can't live without stopped; system file damage, a failing drive or malware.", CrashHint.Windows),
        [0xF4] = new("CRITICAL_OBJECT_TERMINATION", "A critical process ended; often the system drive stopped responding.", CrashHint.Storage),
        [0xF7] = new("DRIVER_OVERRAN_STACK_BUFFER", "A driver overflowed a buffer.", CrashHint.Driver),
        [0xFC] = new("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "A driver tried to run code from data memory.", CrashHint.Driver),
        [0xFE] = new("BUGCODE_USB_DRIVER", "A USB driver crashed.", CrashHint.Driver),
        [0x101] = new("CLOCK_WATCHDOG_TIMEOUT", "A CPU core stopped responding; unstable overclock/undervolt, failing CPU or BIOS bug.", CrashHint.Hardware),
        [0x109] = new("CRITICAL_STRUCTURE_CORRUPTION", "Kernel code or data was changed; a bad driver, bad RAM or tampering.", CrashHint.Memory),
        [0x10E] = new("VIDEO_MEMORY_MANAGEMENT_INTERNAL", "The graphics memory manager failed.", CrashHint.Graphics),
        [0x113] = new("VIDEO_DXGKRNL_FATAL_ERROR", "The DirectX graphics kernel failed.", CrashHint.Graphics),
        [0x116] = new("VIDEO_TDR_FAILURE", "The graphics driver stopped responding and couldn't be reset.", CrashHint.Graphics),
        [0x117] = new("VIDEO_TDR_TIMEOUT_DETECTED", "The graphics driver stopped responding.", CrashHint.Graphics),
        [0x119] = new("VIDEO_SCHEDULER_INTERNAL_ERROR", "The GPU scheduler hit a fatal error; usually the graphics driver.", CrashHint.Graphics),
        [0x124] = new("WHEA_UNCORRECTABLE_ERROR", "The hardware reported a fatal error (CPU, RAM, PCIe); unstable overclock, overheating, failing CPU or power supply.", CrashHint.Hardware),
        [0x127] = new("PAGE_NOT_ZERO", "A memory page that should be empty wasn't; usually failing RAM.", CrashHint.Memory),
        [0x12B] = new("FAULTY_HARDWARE_CORRUPTED_PAGE", "Windows found a single-bit error in memory; failing RAM.", CrashHint.Memory),
        [0x133] = new("DPC_WATCHDOG_VIOLATION", "A driver took too long; often storage (SSD firmware, SATA driver) or network drivers.", CrashHint.Driver),
        [0x139] = new("KERNEL_SECURITY_CHECK_FAILURE", "The kernel caught corrupted data; a bad driver or RAM.", CrashHint.Driver),
        [0x13A] = new("KERNEL_MODE_HEAP_CORRUPTION", "A driver corrupted the kernel heap.", CrashHint.Driver),
        [0x141] = new("VIDEO_ENGINE_TIMEOUT_DETECTED", "A GPU engine stopped responding (recovered, logged as a live dump).", CrashHint.Graphics),
        [0x144] = new("BUGCODE_USB3_DRIVER", "A USB 3 driver crashed.", CrashHint.Driver),
        [0x14C] = new("FATAL_ABNORMAL_RESET_ERROR", "The hardware reset unexpectedly.", CrashHint.Hardware),
        [0x154] = new("UNEXPECTED_STORE_EXCEPTION", "The memory compression store hit an error; often the drive or its driver.", CrashHint.Storage),
        [0x15F] = new("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", "A device didn't respond in modern standby.", CrashHint.Driver),
        [0x161] = new("LIVE_SYSTEM_DUMP", "A diagnostic snapshot, not a crash.", CrashHint.Other),
        [0x18B] = new("SECURE_KERNEL_ERROR", "The secure kernel (virtualization-based security) failed.", CrashHint.Windows),
        [0x1CA] = new("SYNTHETIC_WATCHDOG_TIMEOUT", "The system stopped responding (watchdog).", CrashHint.Hardware),
    };

    /// <summary>Strips the 0x10000000 flag that some codes carry (0x1000007E is 0x7E).</summary>
    public static uint Normalize(uint code) => code & 0x0FFFFFFF;

    public static Info? Lookup(uint code) => Codes.TryGetValue(Normalize(code), out var i) ? i : null;

    public static string Name(uint code) => Lookup(code)?.Name ?? "unknown code";

    public static string Hex(uint code) => $"0x{Normalize(code):X}";

    /// <summary>
    /// Which bugcheck parameter (0-based) holds the address of the code that crashed, so it can be matched to a driver.
    /// From the parameter tables on learn.microsoft.com for each code.
    /// </summary>
    public static int? AddressParameter(uint code) => Normalize(code) switch
    {
        0x0A or 0xD1 => 3,
        0x1E or 0x7E or 0x8E or 0x3B => 1,
        0x50 => 2,
        _ => null,
    };

    private static readonly Regex Param1 = new(@"0x([0-9a-fA-F]{1,16})\s*\(([^)]*)\)", RegexOptions.CultureInvariant);

    /// <summary>Parses "0x00000119 (0x1, 0x5bfcb9, 0x5bfd6d, 0xffff…)" from WER-SystemErrorReporting event 1001.</summary>
    public static (uint Code, List<ulong> Parameters)? ParseEventText(string? text)
    {
        if (text is null || Param1.Match(text) is not { Success: true } m) return null;
        if (!ulong.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) || code == 0 || code > uint.MaxValue) return null;
        var ps = m.Groups[2].Value.Split(',')
            .Select(p => p.Trim())
            .Select(p => p.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p.Substring(2) : p)
            .Select(p => ulong.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0UL)
            .ToList();
        return ((uint)code, ps);
    }
}

/// <summary>One blue screen, merged from its dump file, WER event 1001 and Kernel-Power 41.</summary>
public sealed record BlueScreen(DateTime TimeUtc, uint Code, IReadOnlyList<ulong> Parameters, string? Driver, string? DumpFile, string Source)
{
    public BugChecks.Info? Info => BugChecks.Lookup(Code);
}

public static class BlueScreens
{
    public const string WerProvider = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string KernelPower = "Microsoft-Windows-Kernel-Power";

    /// <summary>Every blue screen the snapshot knows about, newest first, each listed once.</summary>
    public static List<BlueScreen> From(Snapshot s)
    {
        var list = new List<BlueScreen>();
        foreach (var d in s.Crashes?.Value?.Dumps ?? [])
            if (d.BugCheck is { } code and not 0)
                list.Add(new BlueScreen(d.TimeUtc, BugChecks.Normalize(code), d.Parameters, d.Driver, d.File, "dump file"));

        var events = s.Events?.Value;
        foreach (var e in events?.Find(WerProvider, 1001) ?? [])
        {
            if (BugChecks.ParseEventText(e.Get("param1") ?? e.Message) is not { } parsed) continue;
            var dump = e.Get("param2") is { Length: > 0 } p ? FileName(p) : null;
            // Already listed from the dump file itself (same file name, or same code within a few hours: 1001 is logged after the restart).
            if (list.Any(b => (dump is not null && b.DumpFile is not null && string.Equals(FileName(b.DumpFile), dump, StringComparison.OrdinalIgnoreCase))
                              || Same(b, parsed.Code, e.TimeUtc))) continue;
            list.Add(new BlueScreen(e.TimeUtc, BugChecks.Normalize(parsed.Code), parsed.Parameters, null, dump, "event log"));
        }
        foreach (var e in events?.Find(KernelPower, 41) ?? [])
        {
            if (!uint.TryParse(e.Get("BugcheckCode"), out var code) || code == 0) continue;
            if (list.Any(b => Same(b, code, e.TimeUtc))) continue;
            var ps = new[] { "BugcheckParameter1", "BugcheckParameter2", "BugcheckParameter3", "BugcheckParameter4" }
                .Select(n => ParseUlong(e.Get(n))).ToList();
            list.Add(new BlueScreen(e.TimeUtc, BugChecks.Normalize(code), ps, null, null, "event log"));
        }
        return list.OrderByDescending(b => b.TimeUtc).ToList();
    }

    private static bool Same(BlueScreen b, uint code, DateTime t) =>
        b.Code == BugChecks.Normalize(code) && Math.Abs((b.TimeUtc - t).TotalHours) < 6;

    private static string FileName(string path) => path.Substring(path.LastIndexOfAny(['\\', '/']) + 1);

    private static ulong ParseUlong(string? v) =>
        v is null ? 0
        : v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h
        : ulong.TryParse(v, out var d) ? d : 0;

    /// <summary>Drivers to blame: named in the dumps, minus the kernel itself (ntoskrnl is where most crashes surface, not their cause).</summary>
    public static bool IsKernel(string driver) =>
        new[] { "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "hal.dll" }.Contains(driver.ToLowerInvariant());

    public static readonly string[] GpuDrivers = ["nvlddmkm.sys", "amdkmdag.sys", "atikmdag.sys", "atikmpag.sys", "igdkmd64.sys", "igdkmdn64.sys", "igdkmdnd64.sys", "dxgkrnl.sys", "dxgmms2.sys"];
}
