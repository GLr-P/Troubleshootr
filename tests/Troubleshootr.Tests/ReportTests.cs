using Troubleshootr.Checks;
using Troubleshootr.Collectors.Fixes;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

public class ReportTests
{
    private static Report Demo()
    {
        var path = Path.Combine(Paths.Repo(), "tests", "fixtures", "demo-many-problems.snap.json");
        var r = ScanEngine.Evaluate(Json.Deserialize<Snapshot>(File.ReadAllText(path)), CheckRegistry.All, new ScanOptions { ToolVersion = "1.0.0" });
        r.Snapshot ??= Json.Deserialize<Snapshot>(File.ReadAllText(path));
        return r;
    }

    [Fact]
    public void HtmlHasEveryFindingAndEscapesText()
    {
        var r = Demo();
        r.Findings.Insert(0, new Finding("TST-001", Category.Storage, Severity.Critical, "<script>alert(1)</script>", "a & b", "do \"this\""));
        r.FixesRun.Add(new FixRecord { Id = "dns-flush", Title = "Flush the DNS cache", Ok = true, Summary = "Flushed the DNS cache.", Output = "> ipconfig /flushdns" });
        var html = HtmlReport.Write(r);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a &amp; b", html);
        Assert.Contains("Fixes run", html);
        foreach (var f in r.Findings) Assert.Contains(System.Net.WebUtility.HtmlEncode(f.Title), html);
        Assert.StartsWith("<!doctype html>", html);
    }

    [Fact]
    public void AnonymizedReportScrubsNamesFromFindingsAndLeavesOriginal()
    {
        var r = Demo();
        var s = r.Snapshot!;
        s.System!.Value!.DeviceName = "KITCHEN-PC";
        s.System.Value.SerialNumber = "SN12345678";
        r.Device.DeviceName = "KITCHEN-PC";
        (s.Network ??= Section<NetworkInfo>.Of(new NetworkInfo())).Value!.Hosts.Add(new HostsEntry { Address = "192.168.1.9", Host = "printer.home.lan" });
        r.Findings.Add(new Finding("TST-002", Category.Network, Severity.Warning, "KITCHEN-PC can't reach the printer", "Serial SN12345678 on C:\\Users\\sam\\x, printer.home.lan", "Restart KITCHEN-PC."));

        var a = Anonymizer.Apply(r);

        var text = string.Join("\n", a.Findings.Select(f => f.Title + f.Evidence + f.Advice));
        Assert.DoesNotContain("KITCHEN-PC", text);
        Assert.DoesNotContain("SN12345678", text);
        Assert.DoesNotContain("printer.home.lan", text);
        Assert.DoesNotContain("\\sam\\", text);
        Assert.Equal(Anonymizer.Redacted, a.Device.DeviceName);
        Assert.True(a.Snapshot!.Meta.Anonymized);
        Assert.Equal("KITCHEN-PC", r.Device.DeviceName);  // the original is untouched
        Assert.Contains(r.Findings, f => f.Title.StartsWith("KITCHEN-PC"));
    }

    [Fact]
    public void AnonymizingKeepsOrdinaryWords()
    {
        var r = Demo();
        r.Snapshot!.System!.Value!.UserName = "Sam";
        r.Snapshot.System.Value.SerialNumber = "Default string";
        r.Snapshot.Board!.Value!.BoardSerial = "User";
        r.Findings.Add(new Finding("TST-003", Category.Storage, Severity.Info, "Samsung SSD 870 is fine", "Sam's files; default string tables", "Clear the user's temp folder."));

        var f = Anonymizer.Apply(r).Findings.Last();

        Assert.Equal("Samsung SSD 870 is fine", f.Title);
        Assert.Equal("[redacted]'s files; default string tables", f.Evidence);
        Assert.Equal("Clear the user's temp folder.", f.Advice);
    }

    [Fact]
    public void TicketAndTextMentionFixesRun()
    {
        var r = Demo();
        r.FixesRun.Add(new FixRecord { Id = "temp-cleanup", Title = "Delete temporary files", Ok = true, Summary = "Deleted 12 files, freeing 3.4 MB." });
        Assert.Contains("Done: Delete temporary files (Deleted 12 files, freeing 3.4 MB)", TextReport.Ticket(r));
        Assert.Contains("Fixes run:", TextReport.Write(r));
    }

    [Fact]
    public void EveryFixIdInFindingsExists()
    {
        Assert.Equal(FixRegistry.All.Count, FixRegistry.All.Select(f => f.Id).Distinct().Count());
        var dir = Path.Combine(Paths.Repo(), "tests", "fixtures");
        var used = Directory.GetFiles(dir, "*.snap.json")
            .SelectMany(f => ScanEngine.Evaluate(Json.Deserialize<Snapshot>(File.ReadAllText(f)), CheckRegistry.All).Findings)
            .Where(f => f.FixActionId is not null).ToList();
        Assert.NotEmpty(used);
        foreach (var f in used) Assert.True(FixRegistry.Find(f.FixActionId) is not null, $"{f.CheckId} points at unknown fix {f.FixActionId}");
        // Every linked id in the source exists too (findings not triggered by any fixture).
        var src = Path.Combine(Paths.Repo(), "src", "Troubleshootr.Checks");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     string.Concat(Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText)), "fix: \"([a-z-]+)\""))
            Assert.True(FixRegistry.Find(m.Groups[1].Value) is not null, $"unknown fix id {m.Groups[1].Value}");
    }

    [Theory]
    [InlineData("Windows Resource Protection did not find any integrity violations.", true)]
    [InlineData("Windows Resource Protection found corrupt files and successfully repaired them.", true)]
    [InlineData("Windows Resource Protection found corrupt files but was unable to fix some of them.", false)]
    [InlineData("Windows Resource Protection could not perform the requested operation.", false)]
    [InlineData("There is a system repair pending which requires reboot to complete.  Restart Windows and run sfc again.", false)]
    [InlineData("", false)]
    public void SfcOutputIsRead(string output, bool ok) => Assert.Equal(ok, SystemFilesFix.ReadSfc(output).Ok);

    [Fact]
    public void TempCleanupDeletesOnlyOldFilesAndKeepsTheFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "troubleshootr-test-" + Guid.NewGuid().ToString("N"));
        var sub = Directory.CreateDirectory(Path.Combine(root, "old-dir")).FullName;
        try
        {
            var old = Path.Combine(sub, "old.tmp");
            var fresh = Path.Combine(root, "new.tmp");
            File.WriteAllText(old, new string('x', 1000));
            File.WriteAllText(fresh, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
            Directory.SetLastWriteTimeUtc(sub, DateTime.UtcNow.AddDays(-3));

            var (files, bytes, skipped) = TempCleanupFix.Clean(root, DateTime.UtcNow.AddDays(-1), CancellationToken.None);

            Assert.Equal((1, 1000L, 0), (files, bytes, skipped));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.False(Directory.Exists(sub));
            Assert.True(Directory.Exists(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
