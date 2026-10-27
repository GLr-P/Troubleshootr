using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Fixes;

/// <summary>Every fix action, by id. Findings point at them through Finding.FixActionId.</summary>
public static class FixRegistry
{
    public static IReadOnlyList<IFixAction> All { get; } =
    [
        new SystemFilesFix(), new TempCleanupFix(), new DnsFlushFix(), new NetworkResetFix(), new SpoolerFix(), new MemoryDiagnosticFix(),
    ];

    public static IFixAction? Find(string? id) => All.FirstOrDefault(f => f.Id == id);
}

/// <summary>Shared plumbing: timing, the record, and turning exceptions into a failed record.</summary>
public abstract class FixAction : IFixAction
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Changes { get; }
    public abstract bool NeedsAdmin { get; }
    public abstract string Duration { get; }

    /// <summary>Does the work; returns (ok, summary). Output lines go to <paramref name="log"/>.</summary>
    protected abstract Task<(bool Ok, string Summary)> Run(Log log, CancellationToken ct);

    public async Task<FixRecord> RunAsync(Action<string> output, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var log = new Log(output);
        bool ok;
        string summary;
        if (NeedsAdmin && !Native.IsElevated()) (ok, summary) = (false, "Needs admin: restart Troubleshootr as admin and try again.");
        else
        {
            try { (ok, summary) = await Run(log, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { (ok, summary) = (false, "Cancelled."); }
            catch (Exception e) { (ok, summary) = (false, $"{e.GetType().Name}: {e.Message}"); }
        }
        log.Line(summary);
        return new FixRecord { Id = Id, Title = Title, StartedUtc = started, DurationMs = Timing.Ms(sw), Ok = ok, Summary = summary, Output = FixRecord.Tail(log.Text) };
    }

    public sealed class Log(Action<string> sink)
    {
        private readonly StringBuilder _all = new();
        public string Text { get { lock (_all) return _all.ToString(); } }
        public void Line(string line)
        {
            lock (_all) _all.AppendLine(line);
            sink(line);
        }
    }

    /// <summary>A Windows tool from System32, even from a 32-bit process on 64-bit Windows (which would get SysWOW64).</summary>
    protected static string Sys(string exe)
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var dir = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? Path.Combine(windir, "Sysnative") : Environment.SystemDirectory;
        return Path.Combine(dir, exe);
    }

    /// <summary>Runs a console tool hidden, streaming its output lines; kills it on cancel or after <paramref name="timeout"/>.</summary>
    protected static async Task<(int ExitCode, string Output)> Exec(string exe, string args, Log log, TimeSpan timeout, CancellationToken ct, Encoding? encoding = null)
    {
        log.Line($"> {Path.GetFileName(exe)} {args}");
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var text = new StringBuilder();
        var exited = new TaskCompletionSource<bool>();
        var lastPercent = -100;
        void OnLine(string? raw)
        {
            var line = raw?.Replace("\0", "").Trim();
            if (string.IsNullOrEmpty(line)) return;
            // DISM ("[==== 10.1% ====]") and SFC ("Verification 12% complete.") redraw their progress hundreds of times
            // (each redraw arrives as a line); pass on every 10% only.
            if (Progress.Match(line!) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var pct))
            {
                lock (text) { if (pct < lastPercent + 10 && pct < 100 || pct == lastPercent) return; lastPercent = pct; }
            }
            lock (text) text.AppendLine(line);
            log.Line(line!);
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => exited.TrySetResult(true);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using (timeoutCts.Token.Register(() => exited.TrySetCanceled()))
        {
            try { await exited.Task.ConfigureAwait(false); }
            catch (TaskCanceledException)
            {
                KillTree(p);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new System.TimeoutException($"{Path.GetFileName(exe)} didn't finish within {timeout.TotalMinutes:0} minutes");
            }
        }
        p.WaitForExit();  // flushes the redirected output
        lock (text) return (p.ExitCode, text.ToString());
    }

    private static readonly System.Text.RegularExpressions.Regex Progress =
        new(@"^(?:\[[=\s]*|Verification\s+)(\d+)(?:\.\d+)?\s*%", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Kills a tool and its children (DISM hands the work to DismHost.exe), so a timed-out repair doesn't run on unseen.</summary>
    private static void KillTree(Process p)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo(Sys("taskkill.exe"), $"/PID {p.Id} /T /F") { UseShellExecute = false, CreateNoWindow = true });
            kill?.WaitForExit(10000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { if (!p.HasExited) p.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>DISM RestoreHealth (repairs the component store from Windows Update), then SFC (repairs system files from it).</summary>
public sealed class SystemFilesFix : FixAction
{
    public override string Id => "system-files";
    public override string Title => "Repair Windows system files (DISM + SFC)";
    public override string Changes => "Runs DISM /RestoreHealth, which downloads clean copies of damaged Windows components from Windows Update, then sfc /scannow, which replaces damaged system files. Nothing personal is touched. Keep the PC plugged in.";
    public override bool NeedsAdmin => true;
    public override string Duration => "15–45 minutes";

    protected override async Task<(bool, string)> Run(Log log, CancellationToken ct)
    {
        var (dismCode, dismOut) = await Exec(Sys("Dism.exe"), "/Online /Cleanup-Image /RestoreHealth", log, TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
        var dism = dismCode == 0 ? "DISM repaired the component store or found it healthy" : $"DISM failed (error {DismError(dismCode, dismOut)})";
        // SFC writes UTF-16 when its output is redirected.
        var (_, sfcOut) = await Exec(Sys("sfc.exe"), "/scannow", log, TimeSpan.FromMinutes(60), ct, Encoding.Unicode).ConfigureAwait(false);
        var (sfcOk, sfc) = ReadSfc(sfcOut);
        return (dismCode == 0 && sfcOk, $"{dism}; {sfc}.");
    }

    private static string DismError(int code, string output) =>
        System.Text.RegularExpressions.Regex.Match(output, @"Error:\s*(0x[0-9a-fA-F]+|\d+)") is { Success: true } m ? m.Groups[1].Value : $"0x{code:X8}";

    /// <summary>What SFC's last lines mean. Public for the tests.</summary>
    public static (bool Ok, string Summary) ReadSfc(string output)
    {
        if (output.IndexOf("did not find any integrity violations", StringComparison.OrdinalIgnoreCase) >= 0) return (true, "SFC found no damaged files");
        if (output.IndexOf("successfully repaired", StringComparison.OrdinalIgnoreCase) >= 0) return (true, "SFC found damaged files and repaired them (restart to finish)");
        if (output.IndexOf("unable to fix", StringComparison.OrdinalIgnoreCase) >= 0) return (false, "SFC found damaged files it couldn't repair (see CBS.log; an in-place upgrade fixes this)");
        if (output.IndexOf("could not perform the requested operation", StringComparison.OrdinalIgnoreCase) >= 0) return (false, "SFC couldn't run (try again in Safe Mode)");
        if (output.IndexOf("repair pending", StringComparison.OrdinalIgnoreCase) >= 0 || output.IndexOf("pending system repair", StringComparison.OrdinalIgnoreCase) >= 0) return (false, "SFC needs a restart first (a repair is pending)");
        return (false, "SFC gave no result");
    }
}

/// <summary>Deletes temp files more than a day old (the user's, and Windows' own as admin). Files in use are skipped.</summary>
public sealed class TempCleanupFix : FixAction
{
    public override string Id => "temp-cleanup";
    public override string Title => "Delete temporary files";
    public override string Changes => "Deletes files older than a day from the user's temp folder (and C:\\Windows\\Temp when running as admin). Files that are in use are left alone.";
    public override bool NeedsAdmin => false;
    public override string Duration => "under a minute";

    protected override Task<(bool, string)> Run(Log log, CancellationToken ct)
    {
        var folders = new List<string> { Path.GetTempPath() };
        if (Native.IsElevated()) folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        long freed = 0;
        int files = 0, skipped = 0;
        foreach (var dir in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = Clean(dir, DateTime.UtcNow.AddDays(-1), ct);
            log.Line($"{dir}: deleted {r.Files} files ({r.Bytes / 1048576.0:0.#} MB), {r.Skipped} in use or protected");
            freed += r.Bytes; files += r.Files; skipped += r.Skipped;
        }
        return Task.FromResult((true, $"Deleted {files:N0} files, freeing {freed / 1048576.0:0.#} MB ({skipped:N0} skipped because they're in use)."));
    }

    /// <summary>Deletes files last written before <paramref name="olderThanUtc"/> under <paramref name="root"/> (not the folder itself). Never follows links.</summary>
    public static (int Files, long Bytes, int Skipped) Clean(string root, DateTime olderThanUtc, CancellationToken ct)
    {
        int files = 0, skipped = 0;
        long bytes = 0;
        void Walk(DirectoryInfo dir)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; return; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;  // a junction or symlink: its target isn't ours
                if (e is DirectoryInfo sub)
                {
                    var wasOld = sub.LastWriteTimeUtc < olderThanUtc;  // read first: deleting its files makes it look new
                    Walk(sub);
                    try { if (wasOld && !sub.EnumerateFileSystemInfos().Any()) sub.Delete(); }
                    catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
                }
                else if (e is FileInfo f && f.LastWriteTimeUtc < olderThanUtc)
                {
                    try
                    {
                        var size = f.Length;
                        if (f.IsReadOnly) f.IsReadOnly = false;
                        f.Delete();
                        files++;
                        bytes += size;
                    }
                    catch (Exception x) when (x is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
        }
        if (Directory.Exists(root)) Walk(new DirectoryInfo(root));
        return (files, bytes, skipped);
    }
}

public sealed class DnsFlushFix : FixAction
{
    public override string Id => "dns-flush";
    public override string Title => "Flush the DNS cache";
    public override string Changes => "Clears Windows' list of remembered website addresses, so they're looked up again. Harmless.";
    public override bool NeedsAdmin => false;
    public override string Duration => "a few seconds";

    protected override async Task<(bool, string)> Run(Log log, CancellationToken ct)
    {
        var (code, _) = await Exec(Sys("ipconfig.exe"), "/flushdns", log, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return code == 0 ? (true, "Flushed the DNS cache.") : (false, $"ipconfig /flushdns failed (exit code {code}).");
    }
}

/// <summary>netsh winsock reset + netsh int ip reset + DNS flush: the standard "internet is broken in weird ways" reset.</summary>
public sealed class NetworkResetFix : FixAction
{
    public override string Id => "network-reset";
    public override string Title => "Reset Windows networking (Winsock + TCP/IP)";
    public override string Changes => "Resets Winsock and TCP/IP to Windows defaults and flushes DNS. Removes broken network hooks, but also any static IP address or manually set DNS on the adapters, and some VPN apps need reinstalling. The PC must restart to finish.";
    public override bool NeedsAdmin => true;
    public override string Duration => "under a minute, then a restart";

    protected override async Task<(bool, string)> Run(Log log, CancellationToken ct)
    {
        var (winsock, _) = await Exec(Sys("netsh.exe"), "winsock reset", log, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var (ip, _) = await Exec(Sys("netsh.exe"), "int ip reset", log, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        await Exec(Sys("ipconfig.exe"), "/flushdns", log, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        // "int ip reset" returns 1 when a few registry keys were already default ("Access is denied" on one key is common); winsock is the one that matters.
        return winsock == 0
            ? (true, $"Reset Winsock{(ip == 0 ? " and TCP/IP" : " (TCP/IP reset partly failed, which is common)")}. Restart the PC to finish.")
            : (false, $"netsh winsock reset failed (exit code {winsock}).");
    }
}

/// <summary>Stops the Print Spooler, deletes the stuck job files, starts it again.</summary>
public sealed class SpoolerFix : FixAction
{
    public override string Id => "spooler-restart";
    public override string Title => "Restart printing and clear stuck jobs";
    public override string Changes => "Stops the Print Spooler service, deletes every print job waiting in the queue (they need printing again), and starts the service.";
    public override bool NeedsAdmin => true;
    public override string Duration => "a few seconds";

    protected override Task<(bool, string)> Run(Log log, CancellationToken ct)
    {
        using var sc = new ServiceController("Spooler");
        if (sc.StartType == ServiceStartMode.Disabled) return Task.FromResult((false, "Print Spooler is disabled; set it to Automatic in Services first."));
        // Services that need the spooler (Fax and some printer vendors' services) stop with it; start them again afterwards.
        var dependents = sc.DependentServices.Where(d => d.Status == ServiceControllerStatus.Running).Select(d => d.ServiceName).ToList();
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            log.Line("Stopping Print Spooler…");
            try
            {
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
            {
                // A hung spooler (the usual reason for this fix) ignores the stop request: end its process.
                log.Line("The spooler didn't stop; ending spoolsv.exe.");
                foreach (var p in Process.GetProcessesByName("spoolsv")) using (p) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception x) when (x is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
                sc.Refresh();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
        var queue = Path.Combine(Environment.SystemDirectory, "spool", "PRINTERS");
        var jobs = 0;
        var spl = 0;
        foreach (var f in Directory.Exists(queue) ? Directory.GetFiles(queue) : [])
        {
            try { File.Delete(f); jobs++; if (f.EndsWith(".SPL", StringComparison.OrdinalIgnoreCase)) spl++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Line($"Couldn't delete {Path.GetFileName(f)}: {e.Message}"); }
        }
        log.Line($"Deleted {jobs} queued job file{(jobs == 1 ? "" : "s")}. Starting Print Spooler…");
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        foreach (var name in dependents)
        {
            try { using var d = new ServiceController(name); if (d.Status == ServiceControllerStatus.Stopped) d.Start(); }
            catch (InvalidOperationException e) { log.Line($"Couldn't restart {name}: {e.Message}"); }
        }
        return Task.FromResult((true, $"Print Spooler restarted; {spl} stuck job{(spl == 1 ? "" : "s")} cleared."));
    }
}

/// <summary>Opens Windows Memory Diagnostic, which offers to restart now or at the next restart and tests the RAM before Windows loads.</summary>
public sealed class MemoryDiagnosticFix : FixAction
{
    public override string Id => "memory-diagnostic";
    public override string Title => "Test the RAM (Windows Memory Diagnostic)";
    public override string Changes => "Opens Windows Memory Diagnostic. Choosing \"Restart now\" restarts the PC (save work first) and tests the RAM for about 10–20 minutes before Windows starts. The result shows after sign-in and in the next Troubleshootr scan.";
    public override bool NeedsAdmin => false;  // mdsched asks for admin itself (one UAC prompt)
    public override string Duration => "seconds to open; the test runs for 10–20 minutes after a restart";

    protected override Task<(bool, string)> Run(Log log, CancellationToken ct)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Sys("MdSched.exe")) { UseShellExecute = true })?.Dispose();
            return Task.FromResult((true, "Opened Windows Memory Diagnostic: pick \"Restart now and check for problems\" or \"next time I start\"."));
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return Task.FromResult((false, "Cancelled at the admin prompt."));
        }
    }
}
