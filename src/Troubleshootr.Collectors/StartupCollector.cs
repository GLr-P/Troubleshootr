using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>What starts at sign-in: Run keys (both registry views), Startup folders, and non-Microsoft logon/boot scheduled tasks.</summary>
public sealed class StartupCollector : Collector<StartupInfo>
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public override string Section => nameof(Snapshot.Startup);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(8);

    protected override StartupInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new StartupInfo();
        RunKey(info, RegistryHive.CurrentUser, RegistryView.Default, "HKCU Run", "Run");
        RunKey(info, RegistryHive.LocalMachine, Reg.NativeView, "HKLM Run", "Run");
        if (Environment.Is64BitOperatingSystem) RunKey(info, RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM Run (32-bit)", "Run32");
        Folder(info, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Startup folder");
        Folder(info, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "All users Startup folder");
        ct.ThrowIfCancellationRequested();
        Tasks(info, ct);
        ct.ThrowIfCancellationRequested();
        CheckFiles(info, ct);
        return info;
    }

    /// <summary>
    /// Resolves each entry's file and checks its signature, in parallel. Windows' own files (System32 and friends) are
    /// skipped: Windows signs most of them through catalogs, which a file check reports as unsigned.
    /// </summary>
    private static void CheckFiles(StartupInfo info, CancellationToken ct)
    {
        foreach (var e in info.Entries)
        {
            var command = e.Command is { } c && c.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Signatures.ShortcutTarget(c) ?? c : e.Command;
            e.File = Signatures.FileOf(command);
        }
        // Checking hashes the whole file (big launchers are 100+ MB), so it gets a budget; files not reached stay unchecked.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(info.Entries.Where(e => e.File is not null && !StartupEntry.IsWindowsFile(e.File)),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            e => { if (watch.ElapsedMilliseconds < SignatureBudgetMs) (e.Signed, e.Publisher) = Signatures.Check(e.File!); });
    }

    private const int SignatureBudgetMs = 3000;

    private static void RunKey(StartupInfo info, RegistryHive hive, RegistryView view, string location, string approvedName)
    {
        var approved = ApprovedStates(hive, approvedName);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(Run);
            if (key is null) return;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
                info.Entries.Add(new StartupEntry
                {
                    Name = name,
                    Location = location,
                    Command = key.GetValue(name) as string,
                    Enabled = !approved.TryGetValue(name, out var on) || on,
                });
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static void Folder(StartupInfo info, string path, RegistryHive hive, string location)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        var approved = ApprovedStates(hive, "StartupFolder");
        try
        {
            foreach (var file in Directory.GetFiles(path).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file);
                info.Entries.Add(new StartupEntry { Name = Path.GetFileNameWithoutExtension(file), Location = location, Command = file, Enabled = !approved.TryGetValue(name, out var on) || on });
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
    }

    /// <summary>Task Manager's on/off switch: StartupApproved\Run|Run32|StartupFolder, first byte even = on, odd = off.</summary>
    private static Dictionary<string, bool> ApprovedStates(RegistryHive hive, string sub)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? Reg.NativeView : RegistryView.Default);
            using var key = root.OpenSubKey($@"{Approved}\{sub}");
            if (key is null) return result;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is byte[] { Length: > 0 } b) result[name] = (b[0] & 1) == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return result;
    }

    /// <summary>Scheduled tasks with a logon (9) or boot (8) trigger, outside \Microsoft (those are Windows' own).</summary>
    private static void Tasks(StartupInfo info, CancellationToken ct) => TaskWalk.Each(task =>
    {
        var definition = Com.Get(task, "Definition")!;
        if (!Com.Items(Com.Get(definition, "Triggers")!).Any(t => Com.Int(Com.Get(t, "Type")) is 8 or 9)) return;
        info.Entries.Add(new StartupEntry
        {
            Name = Com.Get(task, "Name") as string ?? "",
            Location = "Scheduled task",
            Command = TaskWalk.Commands(definition).FirstOrDefault(),
            Enabled = Com.Get(task, "Enabled") is true,
        });
    }, ct);
}