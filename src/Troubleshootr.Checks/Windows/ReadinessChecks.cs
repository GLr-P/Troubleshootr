using System.Text.RegularExpressions;
using Troubleshootr.Core;

namespace Troubleshootr.Checks.Windows;

/// <summary>Windows 11's hardware rules: a supported CPU, TPM 2.0, UEFI with Secure Boot capability, 4 GB RAM, 64 GB disk.</summary>
internal static class Win11
{
    private static readonly Regex IntelCore = new(@"\bi[3579]-(\d{4,5})[A-Z]*\b", RegexOptions.CultureInvariant);
    private static readonly Regex Ryzen = new(@"Ryzen\s+(?:Threadripper\s+)?\d?\s*(\d)(\d{3})([A-Z]*)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex OldAmd = new(@"\bAMD\s+(FX|A\d{1,2}-|Phenom|Athlon(?!\s+(Gold|Silver)))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>True when the CPU is clearly older than Microsoft's supported list (Intel 7th gen and older, first-gen Ryzen); null when unsure.</summary>
    public static bool? CpuUnsupported(string? name)
    {
        if (name is null) return null;
        if (IntelCore.Match(name) is { Success: true } i)
        {
            var digits = i.Groups[1].Value;
            var gen = digits.Length == 5 ? int.Parse(digits.Substring(0, 2)) : int.Parse(digits.Substring(0, 1));
            return gen <= 7;
        }
        if (Ryzen.Match(name) is { Success: true } r)
        {
            var series = int.Parse(r.Groups[1].Value);
            // Ryzen 1000, and the 2000-series APUs and mobile chips (Zen 1: 2200G, 2400G, 2500U…), aren't supported.
            return series == 1 || (series == 2 && r.Groups[3].Value is "G" or "GE" or "U" or "H");
        }
        if (OldAmd.IsMatch(name) || name.IndexOf("Core(TM)2", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return null;
    }

    /// <summary>"2.0", "1.2", or null when no TPM shows in Device Manager (it may be switched off in the BIOS).</summary>
    public static string? TpmVersion(DevicesInfo d) =>
        d.Devices.Where(x => x.Name.IndexOf("Trusted Platform Module", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(x => Regex.Match(x.Name, @"\d\.\d").Value).FirstOrDefault(v => v.Length > 0);
}

/// <summary>WIN-003: Windows isn't activated.</summary>
public sealed class Win003Activation : Check
{
    public override string Id => "WIN-003";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.Health)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var h = s.Health!.Value!;
        if (h.LicenseStatus is not { } status || status == 1) yield break;
        var what = status switch
        {
            0 => "not activated", 2 => "in the first grace period", 3 => "in a grace period after a hardware change",
            4 => "flagged as not genuine", 5 => "in notification mode (not activated)", 6 => "in an extended grace period", _ => $"license status {status}",
        };
        yield return F(Severity.Warning, "Windows isn't activated", $"{h.LicenseName ?? "Windows"} is {what}.",
            "Settings > System > Activation. After a motherboard change, use the troubleshooter and sign in with the Microsoft account that had the digital license, or enter the product key from the PC's sticker or purchase.");
    }
}

/// <summary>WIN-018: on Windows 10, what stands between this PC and Windows 11 (Windows 10 support ended 2025-10-14).</summary>
public sealed class Win018Windows11Ready : Check, IPhysicalHardwareCheck
{
    public override string Id => "WIN-018";
    public override Category Category => Category.WindowsHealth;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.System), nameof(Snapshot.Cpu), nameof(Snapshot.Devices)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        var sys = s.System!.Value!;
        if (sys.MajorVersion != 10 || WindowsVersion.IsWindows11(sys)) yield break;
        var blockers = new List<string>();
        var cpu = s.Cpu!.Value!.Packages.FirstOrDefault()?.Name?.Trim();
        if (Win11.CpuUnsupported(cpu) == true) blockers.Add($"the processor ({cpu}) isn't on Microsoft's supported list");
        var tpm = Win11.TpmVersion(s.Devices!.Value!);
        if (tpm is null) blockers.Add("no TPM found (it may just be switched off in the BIOS: Intel PTT or AMD fTPM)");
        else if (tpm != "2.0") blockers.Add($"the TPM is version {tpm}; Windows 11 needs 2.0");
        var board = s.Board?.Value;
        if (board?.FirmwareType == "Legacy BIOS") blockers.Add("Windows starts in legacy BIOS mode; it needs UEFI (convert the disk with mbr2gpt, then switch the BIOS to UEFI)");
        if (s.Memory?.Value is { } mem && (mem.InstalledMB ?? mem.UsableMB) < 4096) blockers.Add("less than 4 GB of RAM");
        if (s.Volumes?.Value?.FirstOrDefault(v => v.IsSystem) is { } c && c.TotalBytes < 64L * 1073741824) blockers.Add("the system drive is smaller than 64 GB");

        if (blockers.Count == 0)
            yield return F(Severity.Info, "This PC can run Windows 11",
                $"Supported processor{(Win11.CpuUnsupported(cpu) is null ? " (likely)" : "")}, TPM {tpm}, {board?.FirmwareType ?? "UEFI"}{(board?.SecureBoot == false ? " (turn Secure Boot on in the BIOS before upgrading)" : "")}.",
                "Upgrade through Settings > Windows Update or Microsoft's Installation Assistant; Windows 10 no longer gets security updates.");
        else
            yield return F(Severity.Info, "This PC doesn't meet Windows 11's requirements", string.Join("; ", blockers) + ".",
                "Fix what can be fixed in the BIOS (TPM, UEFI, Secure Boot). If the processor is the blocker, the options are Windows 10 Extended Security Updates, a newer PC, or another operating system.");
    }
}

/// <summary>CPU-009: Windows 11 installed on a processor Microsoft doesn't support (it may not get feature updates).</summary>
public sealed class Cpu009UnsupportedCpu : Check, IPhysicalHardwareCheck
{
    public override string Id => "CPU-009";
    public override Category Category => Category.CpuThermal;
    public override IReadOnlyList<string> Needs => [nameof(Snapshot.System), nameof(Snapshot.Cpu)];

    public override IEnumerable<Finding> Run(Snapshot s)
    {
        if (!WindowsVersion.IsWindows11(s.System!.Value!)) yield break;
        var cpu = s.Cpu!.Value!.Packages.FirstOrDefault()?.Name?.Trim();
        if (Win11.CpuUnsupported(cpu) != true) yield break;
        yield return F(Severity.Info, "Windows 11 is running on an unsupported processor", $"{cpu} isn't on Microsoft's Windows 11 processor list.",
            "It works, but Microsoft doesn't guarantee updates on unsupported hardware, and feature updates may need a manual install each year.");
    }
}
