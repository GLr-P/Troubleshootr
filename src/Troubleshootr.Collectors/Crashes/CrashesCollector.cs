using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Crashes;

/// <summary>Minidump files (reading them needs admin; listing them doesn't), crash-dump settings, and WER report folders.</summary>
public sealed class CrashesCollector : Collector<CrashInfo>
{
    private const string CrashControl = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    private const int MaxDumps = 40;
    private const int WerDays = 30;
    private const int MaxReports = 400;

    public override string Section => nameof(Snapshot.Crashes);

    protected override CrashInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new CrashInfo
        {
            CrashDumpEnabled = (int?)Reg.Num(RegistryHive.LocalMachine, CrashControl, "CrashDumpEnabled"),
            AutoReboot = Reg.Num(RegistryHive.LocalMachine, CrashControl, "AutoReboot") is { } a ? a != 0 : null,
        };
        ReadDumps(info, ct);
        ReadWer(info, ct);
        return info;
    }

    private static void ReadDumps(CrashInfo info, CancellationToken ct)
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var dir = Reg.Get(RegistryHive.LocalMachine, CrashControl, "MinidumpDir") is string d && d.Length > 0
            ? Environment.ExpandEnvironmentVariables(d) : Path.Combine(windir, "Minidump");
        FileInfo[] files;
        try { files = new DirectoryInfo(dir).GetFiles("*.dmp"); }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            info.DumpsUnavailable = "the Minidump folder needs admin";
            return;
        }
        var denied = 0;
        foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxDumps))
        {
            ct.ThrowIfCancellationRequested();
            var dump = new CrashDump { File = f.FullName, TimeUtc = f.LastWriteTimeUtc, SizeBytes = f.Length };
            try
            {
                using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                DumpParser.Parse((offset, count) => ReadAt(stream, offset, count), stream.Length, dump);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { denied++; }
            catch (IOException e) { dump.ParseError = e.Message; }
            info.Dumps.Add(dump);
        }
        if (denied > 0) info.DumpsUnavailable = "reading the dump files needs admin";
    }

    /// <summary>Parses one dump file (the CLI "dump" command; also used for each file in the Minidump folder).</summary>
    public static CrashDump ReadDump(string path, out List<DumpParser.Driver>? drivers)
    {
        var f = new FileInfo(path);
        var dump = new CrashDump { File = f.FullName, TimeUtc = f.LastWriteTimeUtc, SizeBytes = f.Length };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        DumpParser.Parse((o, c) => ReadAt(stream, o, c), stream.Length, dump);
        drivers = DumpParser.Drivers((o, c) => ReadAt(stream, o, c), stream.Length);
        return dump;
    }

    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || offset >= stream.Length || count <= 0) return [];
        count = (int)Math.Min(count, stream.Length - offset);
        var buffer = new byte[count];
        stream.Position = offset;
        var read = 0;
        while (read < count && stream.Read(buffer, read, count - read) is var n and > 0) read += n;
        return read == count ? buffer : buffer.Take(read).ToArray();
    }

    /// <summary>Report folders are named Kind_Subject_hash…, e.g. AppCrash_explorer.exe_1a2b…, Kernel_119_…, LiveKernelEvent_141_….</summary>
    private static void ReadWer(CrashInfo info, CancellationToken ct)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER");
        var since = DateTime.UtcNow.AddDays(-WerDays);
        foreach (var (folder, queued) in new[] { ("ReportArchive", false), ("ReportQueue", true) })
        {
            DirectoryInfo[] dirs;
            try { dirs = new DirectoryInfo(Path.Combine(root, folder)).GetDirectories(); }
            catch (Exception e) when (e is DirectoryNotFoundException or UnauthorizedAccessException or IOException or System.Security.SecurityException) { continue; }
            if (queued) info.QueuedReports = dirs.Length;
            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                // Creation time is when the problem happened; Windows touches the folders again whenever it retries sending them.
                if (d.CreationTimeUtc < since || info.Reports.Count >= MaxReports) continue;
                if (ParseReportName(d.Name) is not { } r) continue;
                r.TimeUtc = d.CreationTimeUtc;
                r.Queued = queued;
                info.Reports.Add(r);
            }
        }
        info.Reports = info.Reports.OrderByDescending(r => r.TimeUtc).ToList();
    }

    internal static WerReport? ParseReportName(string name)
    {
        var parts = name.Split('_');
        if (parts.Length < 2) return null;
        // The app name is cut to about 16 characters by Windows (AppCrash_KillerProviderDa_…).
        return new WerReport { Kind = parts[0], Subject = parts[1] };
    }
}
