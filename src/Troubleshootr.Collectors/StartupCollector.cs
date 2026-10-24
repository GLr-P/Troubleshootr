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

    protected override StartupInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new StartupInfo();
        RunKey(info, RegistryHive.CurrentUser, RegistryView.Default, "HKCU Run", "Run");
        RunKey(info, RegistryHive.LocalMachine, Reg.NativeView, "HKLM Run", "Run");
        if (Environment.Is64BitOperatingSystem) RunKey(info, RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM Run (32-bit)", "Run32");
        Folder(info, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Startup folder");
        Folder(info, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "All users Startup folder");
        ct.ThrowIfCancellationRequested();
        try { Tasks(info, ct); }
        catch (Exception e) when (e is COMException or TargetInvocationException or InvalidCastException) { } // Task Scheduler service off
        return info;
    }

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

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? Get(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args.Length == 0 ? null : args, CultureInfo.InvariantCulture);

    private static IEnumerable<object> Items(object collection)
    {
        var count = Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
        for (var i = 1; i <= count; i++) yield return Get(collection, "Item", i)!;
    }

    /// <summary>Scheduled tasks with a logon (9) or boot (8) trigger, outside \Microsoft (those are Windows' own).</summary>
    private static void Tasks(StartupInfo info, CancellationToken ct)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return;
        var service = Activator.CreateInstance(type)!;
        try
        {
            Call(service, "Connect");
            var stack = new Stack<object>();
            stack.Push(Call(service, "GetFolder", @"\")!);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var folder = stack.Pop();
                foreach (var task in Items(Call(folder, "GetTasks", 1)!))
                {
                    var definition = Get(task, "Definition")!;
                    var triggers = Items(Get(definition, "Triggers")!).Select(t => Convert.ToInt32(Get(t, "Type"), CultureInfo.InvariantCulture)).ToList();
                    if (!triggers.Any(t => t is 8 or 9)) continue;
                    var action = Items(Get(definition, "Actions")!).FirstOrDefault(a => Convert.ToInt32(Get(a, "Type"), CultureInfo.InvariantCulture) == 0);
                    info.Entries.Add(new StartupEntry
                    {
                        Name = Get(task, "Name") as string ?? "",
                        Location = "Scheduled task",
                        Command = action is null ? null : $"{Get(action, "Path")} {Get(action, "Arguments")}".Trim(),
                        Enabled = Get(task, "Enabled") is true,
                    });
                }
                foreach (var sub in Items(Call(folder, "GetFolders", 0)!))
                    if (!(Get(sub, "Path") as string ?? "").StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) stack.Push(sub);
            }
        }
        finally { Marshal.ReleaseComObject(service); }
    }
}
