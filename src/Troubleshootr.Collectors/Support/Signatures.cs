using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Troubleshootr.Collectors.Support;

/// <summary>
/// Authenticode checks (WinVerifyTrust, cached revocation data only, so it never waits on the network) and the
/// command-line parsing that turns a startup command into the file it runs.
/// </summary>
public static class Signatures
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2, RevokeNone = 0, RevokeWholeChain = 1, ChoiceFile = 1, CacheOnlyUrlRetrieval = 0x1000;

    /// <summary>Results that mean "no valid signature". Anything else (no access, locked, unknown format) means "couldn't check".</summary>
    private static readonly HashSet<uint> NotTrusted =
    [
        0x800B0100, // TRUST_E_NOSIGNATURE
        0x80096010, // TRUST_E_BAD_DIGEST (file changed after signing)
        0x800B0111, // TRUST_E_EXPLICIT_DISTRUST
        0x800B0109, // CERT_E_UNTRUSTEDROOT
        0x800B010A, // CERT_E_CHAINING
        0x800B0004, // TRUST_E_SUBJECT_NOT_TRUSTED
        0x80096019, // TRUST_E_BASIC_CONSTRAINTS
        0x800B010C, // CERT_E_REVOKED
    ];

    /// <summary>Only program files carry Authenticode signatures; scripts and shortcuts can't, so they aren't "unsigned".</summary>
    private static readonly HashSet<string> Signable = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".scr", ".com", ".sys", ".ocx", ".cpl", ".msi" };

    /// <summary>True = valid signature, false = unsigned or invalid, null = the file couldn't be checked (missing, no access, not a program file).</summary>
    public static (bool? Signed, string? Publisher) Check(string file) => Check(file, checkRevocation: false);

    /// <summary>With <paramref name="checkRevocation"/>, also asks the certificate authority whether the certificate was revoked (online; for files about to run as admin).</summary>
    public static (bool? Signed, string? Publisher) Check(string file, bool checkRevocation)
    {
        if (!Signable.Contains(Path.GetExtension(file)) || !File.Exists(file)) return (null, null);
        var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = UiNone, fdwRevocationChecks = checkRevocation ? RevokeWholeChain : RevokeNone,
                dwUnionChoice = ChoiceFile, pFile = infoPtr, dwProvFlags = checkRevocation ? 0 : CacheOnlyUrlRetrieval,
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            if (result == 0) return (true, Publisher(file));
            return NotTrusted.Contains(unchecked((uint)result)) ? (false, null) : (null, null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return (null, null); }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(infoPtr);
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    private static string? Publisher(string file)
    {
        try
        {
            var subject = X509Certificate.CreateFromSignedFile(file).Subject;
            var cn = Regex.Match(subject, @"CN=(""[^""]+""|[^,]+)");
            return cn.Success ? cn.Groups[1].Value.Trim('"').Trim() : subject;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private static readonly Regex Quoted = new(@"^\s*""([^""]+)""");

    /// <summary>
    /// The executable a command line runs: a quoted path, or the shortest unquoted prefix that ends in .exe/.com/.bat/.cmd
    /// and exists (C:\Program Files\App\app.exe --flag). Environment variables are expanded; bare names are looked up in System32.
    /// </summary>
    public static string? FileOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command!.Trim());
        var q = Quoted.Match(text);
        if (q.Success) return Resolve(q.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"\.(exe|com|bat|cmd|lnk|vbs|js|ps1)\b", RegexOptions.IgnoreCase))
        {
            var candidate = text.Substring(0, m.Index + m.Length);
            if (Resolve(candidate) is { } found) return found;
        }
        var first = text.Split(' ')[0];
        return Resolve(first) ?? (first.IndexOf('\\') >= 0 ? first : null);
    }

    private static string? Resolve(string path)
    {
        try
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (path.IndexOf('\\') < 0)
            {
                var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), path);
                if (File.Exists(sys)) return sys;
                if (File.Exists(sys + ".exe")) return sys + ".exe";
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException) { }
        return null;
    }

    /// <summary>A shortcut's target (Startup folder entries are mostly .lnk files).</summary>
    public static string? ShortcutTarget(string lnk)
    {
        try
        {
            var shell = Com.Create("WScript.Shell");
            if (shell is null) return null;
            try
            {
                var shortcut = Com.Call(shell, "CreateShortcut", lnk)!;
                var target = Com.Get(shortcut, "TargetPath") as string;
                var args = Com.Get(shortcut, "Arguments") as string;
                Marshal.ReleaseComObject(shortcut);
                return string.IsNullOrWhiteSpace(target) ? null : $"\"{target}\" {args}".Trim();
            }
            finally { Marshal.ReleaseComObject(shell); }
        }
        catch (Exception e) when (e is COMException or System.Reflection.TargetInvocationException) { return null; }
    }
}
