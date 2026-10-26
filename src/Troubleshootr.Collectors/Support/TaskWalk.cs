using System.Reflection;
using System.Runtime.InteropServices;

namespace Troubleshootr.Collectors.Support;

/// <summary>Walks every scheduled task outside Windows' own \Microsoft folder (hidden ones included), through the late-bound Task Scheduler COM API.</summary>
public static class TaskWalk
{
    /// <summary>Calls <paramref name="visit"/> with each registered task. Returns quietly if the Task Scheduler service is off.</summary>
    public static void Each(Action<object> visit, CancellationToken ct)
    {
        try
        {
            var service = Com.Create("Schedule.Service");
            if (service is null) return;
            try
            {
                Com.Call(service, "Connect");
                var stack = new Stack<object>();
                stack.Push(Com.Call(service, "GetFolder", @"\")!);
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var folder = stack.Pop();
                    foreach (var task in Com.Items(Com.Call(folder, "GetTasks", 1)!)) visit(task);
                    foreach (var sub in Com.Items(Com.Call(folder, "GetFolders", 0)!))
                        if (!IsWindowsFolder(Com.Get(sub, "Path") as string ?? "")) stack.Push(sub);
                }
            }
            finally { Marshal.ReleaseComObject(service); }
        }
        catch (Exception e) when (e is COMException or TargetInvocationException or InvalidCastException) { }
    }

    /// <summary>\Microsoft and everything under it, but not look-alikes such as \MicrosoftUpdate (a favourite malware hiding place).</summary>
    public static bool IsWindowsFolder(string path) =>
        path.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The executable actions (type 0) of a task as "path arguments".</summary>
    public static IEnumerable<string> Commands(object definition) =>
        Com.Items(Com.Get(definition, "Actions")!).Where(a => Com.Int(Com.Get(a, "Type")) == 0)
            .Select(a => $"{Com.Get(a, "Path")} {Com.Get(a, "Arguments")}".Trim());
}
