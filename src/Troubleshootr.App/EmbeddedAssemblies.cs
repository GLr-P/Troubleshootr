using System.IO;
using System.Reflection;

namespace Troubleshootr.App;

/// <summary>
/// Makes Troubleshootr.exe work on its own: every DLL it needs is embedded in the exe ("embedded/Name.dll"). When one isn't
/// next to the exe, it's written to %TEMP%\Troubleshootr\&lt;version&gt; and loaded from there (a real file, so libraries that
/// look at their own location still work). Must be registered before any other assembly is touched (App's static constructor).
/// Also stands in for the binding redirects in Troubleshootr.exe.config, which a lone exe doesn't have.
/// </summary>
public static class EmbeddedAssemblies
{
    private static readonly object Gate = new();

    [ThreadStatic] private static bool _resolving;

    public static void Register() => AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
    {
        // Anything the resolver itself needs must not come back here (that recursed into a stack overflow).
        if (_resolving) return null;
        _resolving = true;
        try
        {
            if (Environment.GetEnvironmentVariable("TROUBLESHOOTR_TRACE_RESOLVE") is { Length: > 0 } trace) File.AppendAllText(trace, e.Name + Environment.NewLine);
            var name = new AssemblyName(e.Name).Name;
            if (name is null || name == "LibreHardwareMonitorLib") return null;  // per-architecture; LhmLoader handles it
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return Extract($"embedded/{name}.dll") is { } path ? Assembly.LoadFrom(path) : null;
        }
        finally { _resolving = false; }
    };

    /// <summary>
    /// A plain loop on purpose: LINQ's SequenceEqual on arrays binds to System.Memory's span version under C# 14, and
    /// System.Memory is one of the embedded DLLs this class is busy unpacking.
    /// </summary>
    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Writes an embedded file to the temp folder (once per version) and returns its path, or null if it isn't embedded.</summary>
    public static string? Extract(string resource)
    {
        var self = typeof(EmbeddedAssemblies).Assembly;
        using var stream = self.GetManifestResourceStream(resource);
        if (stream is null) return null;
        var dir = Path.Combine(Path.GetTempPath(), "Troubleshootr", self.GetName().Version?.ToString() ?? "0");
        var path = Path.Combine(dir, resource.Replace('/', Path.DirectorySeparatorChar));
        var bytes = new byte[stream.Length];
        for (var read = 0; read < bytes.Length;) read += stream.Read(bytes, read, bytes.Length - read);
        lock (Gate)
        {
            // Compare the content, not the size: a rebuild with the same version number usually has the same size.
            try { if (File.Exists(path) && Same(File.ReadAllBytes(path), bytes)) return path; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Another Troubleshootr window has a different build of this file loaded: use a private copy.
                var own = Path.Combine(dir, Guid.NewGuid().ToString("N"), resource.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(own)!);
                File.WriteAllBytes(own, bytes);
                return own;
            }
        }
    }
}
