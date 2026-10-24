using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Troubleshootr.Collectors.Support;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors;

/// <summary>Windows Update history (the update agent's COM API, no admin needed), update policies and upgrade blocks.</summary>
public sealed class UpdatesCollector : Collector<UpdatesInfo>
{
    private const int MaxHistory = 200;
    private const string Policies = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    public override string Section => nameof(Snapshot.Updates);
    public override TimeSpan Timeout => TimeSpan.FromSeconds(8);

    protected override UpdatesInfo Collect(CollectContext ctx, CancellationToken ct)
    {
        var info = new UpdatesInfo { Policy = Policy(), UpgradeBlocks = UpgradeBlocks() };
        try { info.History = History(ct); }
        catch (Exception e) when (e is COMException or TargetInvocationException or InvalidCastException)
        {
            info.HistoryUnavailable = $"Windows Update history couldn't be read ({(e.InnerException ?? e).Message.Trim()})";
        }
        return info;
    }

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    /// <summary>IUpdateSearcher.QueryHistory, newest first. Late-bound so it works from netstandard2.0 without interop assemblies.</summary>
    private static List<UpdateEntry> History(CancellationToken ct)
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new COMException("Windows Update agent not registered");
        var session = Activator.CreateInstance(type)!;
        var list = new List<UpdateEntry>();
        try
        {
            var searcher = Call(session, "CreateUpdateSearcher")!;
            var total = Convert.ToInt32(Call(searcher, "GetTotalHistoryCount"), CultureInfo.InvariantCulture);
            if (total == 0) return list;
            var history = Call(searcher, "QueryHistory", 0, Math.Min(total, MaxHistory))!;
            var count = Convert.ToInt32(Get(history, "Count"), CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var e = history.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, history, [i], CultureInfo.InvariantCulture)!;
                list.Add(new UpdateEntry
                {
                    DateUtc = DateTime.SpecifyKind((DateTime)Get(e, "Date")!, DateTimeKind.Utc), // the agent reports UTC
                    Title = (Get(e, "Title") as string ?? "").Trim(),
                    Result = Convert.ToInt32(Get(e, "ResultCode"), CultureInfo.InvariantCulture),
                    HResult = Convert.ToInt32(Get(e, "HResult"), CultureInfo.InvariantCulture),
                    Operation = Convert.ToInt32(Get(e, "Operation"), CultureInfo.InvariantCulture),
                });
                Marshal.ReleaseComObject(e);
            }
            Marshal.ReleaseComObject(history);
            Marshal.ReleaseComObject(searcher);
        }
        finally { Marshal.ReleaseComObject(session); }
        return list.Where(e => e.Title.Length > 0).OrderByDescending(e => e.DateUtc).ToList();
    }

    private static UpdatePolicy Policy()
    {
        var p = new UpdatePolicy
        {
            WsusServer = Reg.Str(RegistryHive.LocalMachine, Policies, "WUServer"),
            UsesWsus = Reg.Num(RegistryHive.LocalMachine, $@"{Policies}\AU", "UseWUServer") == 1,
            AutoUpdateOff = Reg.Num(RegistryHive.LocalMachine, $@"{Policies}\AU", "NoAutoUpdate") == 1,
            WindowsUpdateBlocked = Reg.Num(RegistryHive.LocalMachine, Policies, "DoNotConnectToWindowsUpdateInternetLocations") == 1
                                   || Reg.Num(RegistryHive.LocalMachine, Policies, "DisableWindowsUpdateAccess") == 1,
            TargetVersion = Reg.Num(RegistryHive.LocalMachine, Policies, "TargetReleaseVersion") == 1
                ? Reg.Str(RegistryHive.LocalMachine, Policies, "TargetReleaseVersionInfo") : null,
        };
        // Settings > Windows Update > Pause stores its end time here (ISO 8601, UTC).
        var pause = Reg.Str(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime");
        if (pause is not null && DateTime.TryParse(pause, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until))
            p.PausedUntilUtc = until;
        return p;
    }

    private static List<UpgradeBlock> UpgradeBlocks()
    {
        const string root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators";
        return Reg.SubKeys(RegistryHive.LocalMachine, root)
            .Select(k => new UpgradeBlock
            {
                Target = k,
                RedReason = Reg.Str(RegistryHive.LocalMachine, $@"{root}\{k}", "RedReason"),
                GatedBlockId = Reg.Str(RegistryHive.LocalMachine, $@"{root}\{k}", "GatedBlockId"),
            })
            .Where(b => b.RedReason is not null || b.GatedBlockId is not null)
            .ToList();
    }
}
