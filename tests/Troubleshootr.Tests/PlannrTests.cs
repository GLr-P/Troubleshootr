using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Troubleshootr.Checks;
using Troubleshootr.Collectors.Plannr;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>
/// The Plannr inbox. tests/fixtures/plannr holds test keys and a ticket list sealed by Plannr's own code
/// (shared/drop-crypto.ts), so these tests prove the two sides agree byte for byte.
/// </summary>
public class PlannrTests
{
    private static string Dir => Path.Combine(Paths.Repo(), "tests", "fixtures", "plannr");
    private static JsonObject Keys => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "drop-keys.json")))!;
    private static string Link => $"https://plannr-sync.example.workers.dev/#troubleshootr=AbCdEfGh12345678.{Keys["secret"]}.{Keys["publicKey"]}";

    private sealed class FakeSync(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Seen { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync()));
            return answer(request);
        }
    }

    private static Report Demo()
    {
        var s = Json.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(Paths.Repo(), "tests", "fixtures", "demo-many-problems.snap.json")));
        var r = ScanEngine.Evaluate(s, CheckRegistry.All, new ScanOptions { ToolVersion = "1.1.0" });
        r.Snapshot = s;
        return r;
    }

    [Fact]
    public void LinkParsesAndTheTokenMatchesPlannrs()
    {
        var link = PlannrLink.TryParse(Link)!;
        Assert.Equal("https://plannr-sync.example.workers.dev", link.Server);
        Assert.Equal("AbCdEfGh12345678", link.Space);
        Assert.Equal(Keys["token"]!.GetValue<string>(), link.Token);
        Assert.Null(PlannrLink.TryParse("https://plannr-sync.example.workers.dev/#join=abc"));
        Assert.Null(PlannrLink.TryParse("http://insecure.example/#troubleshootr=AbCdEfGh12345678.x.y"));
        Assert.Null(PlannrLink.TryParse(""));
        // A changed last character makes the key not a point on the curve (found by sending with a damaged link).
        var damaged = Link.Substring(0, Link.Length - 1) + (Link.EndsWith("A") ? "B" : "A");
        Assert.Null(PlannrLink.TryParse(damaged));
        Assert.NotNull(PlannrLink.TryParse("http://127.0.0.1:8787/#troubleshootr=AbCdEfGh12345678." + Link.Split(new[] { "AbCdEfGh12345678." }, StringSplitOptions.None)[1]));
    }

    [Fact]
    public void OpensTheTicketListPlannrSealed()
    {
        var plain = DropCrypto.Gunzip(DropCrypto.OpenInfo(DropCrypto.FromB64Url(Keys["secret"]!.GetValue<string>()), File.ReadAllBytes(Path.Combine(Dir, "info.bin"))));
        var tickets = JsonNode.Parse(Encoding.UTF8.GetString(plain))!["tickets"]!.AsArray();
        Assert.Equal(7, tickets[0]!["number"]!.GetValue<int>());
        Assert.Equal("Jane Doe", tickets[0]!["customer"]!.GetValue<string>());
        // A changed byte is caught by the HMAC.
        var tampered = File.ReadAllBytes(Path.Combine(Dir, "info.bin"));
        tampered[20] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => DropCrypto.OpenInfo(DropCrypto.FromB64Url(Keys["secret"]!.GetValue<string>()), tampered));
    }

    [Fact]
    public async Task ListsTicketsAndSendsASealedReport()
    {
        var api = new FakeSync(req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["info"] = B64Url(File.ReadAllBytes(Path.Combine(Dir, "info.bin"))) }.ToJsonString()) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"id":"abc"}""") });
        using var drop = new PlannrDrop(PlannrLink.TryParse(Link)!, api);

        var tickets = await drop.OpenTicketsAsync(CancellationToken.None);
        Assert.Equal([9, 7], tickets.Select(t => t.Number));  // newest first
        Assert.Equal("#7  ·  Jane Doe  ·  Dell XPS 13", tickets[1].Label);  // an older Plannr sends no label

        var payload = PlannrPayload.Build(Demo(), PlannrTarget.Ticket, 7);
        await drop.SendReportAsync(payload, CancellationToken.None);
        var (request, body) = api.Seen[1];
        Assert.Equal("https://plannr-sync.example.workers.dev/api/s/AbCdEfGh12345678/inbox", request.RequestUri!.ToString());
        Assert.Equal("Bearer " + Keys["token"]!.GetValue<string>(), request.Headers.GetValues("Authorization").Single());
        Assert.Equal(1, body[0]);
        Assert.Equal(4, body[1]);

        // Plannr's tests open this file with the private key in drop-keys.json (copy both to Plannr/tests/fixtures/troubleshootr).
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(Path.Combine(Dir, "report.bin")))
            File.WriteAllBytes(Path.Combine(Dir, "report.bin"), DropCrypto.SealReport(DropCrypto.FromB64Url(Keys["publicKey"]!.GetValue<string>()),
                DropCrypto.Gzip(Encoding.UTF8.GetBytes(PlannrPayload.Build(Demo(), PlannrTarget.NewTicket, customer: "Jane Doe").ToJsonString()))));
    }

    [Fact]
    public async Task UsesTheTicketNumberAsPlannrShowsIt()
    {
        var list = new JsonObject { ["v"] = 1, ["tickets"] = new JsonArray(new JsonObject { ["number"] = 12, ["label"] = "NT-0012", ["customer"] = "", ["device"] = "HP Pavilion", ["status"] = "open" }) };
        var info = DropCrypto.SealInfo(DropCrypto.FromB64Url(Keys["secret"]!.GetValue<string>()), DropCrypto.Gzip(Encoding.UTF8.GetBytes(list.ToJsonString())));
        var api = new FakeSync(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["info"] = B64Url(info) }.ToJsonString()) });
        using var drop = new PlannrDrop(PlannrLink.TryParse(Link)!, api);
        var ticket = (await drop.OpenTicketsAsync(CancellationToken.None)).Single();
        Assert.Equal("NT-0012", ticket.Name);
        Assert.Equal("NT-0012  ·  no customer  ·  HP Pavilion", ticket.Label);
    }

    [Fact]
    public async Task ExplainsARevokedLink()
    {
        var api = new FakeSync(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"Not allowed"}""") });
        using var drop = new PlannrDrop(PlannrLink.TryParse(Link)!, api);
        var e = await Assert.ThrowsAsync<PlannrException>(() => drop.SendReportAsync(new JsonObject(), CancellationToken.None));
        Assert.Contains("Copy the link again", e.Message);
    }

    [Fact]
    public void PayloadHasTheTargetDeviceFindingsAndFullReport()
    {
        var p = PlannrPayload.Build(Demo(), PlannrTarget.NewTicket, customer: "  Jane Doe ");
        Assert.Equal(1, p["v"]!.GetValue<int>());
        Assert.Equal("troubleshootr-report", p["kind"]!.GetValue<string>());
        Assert.Equal("new", p["target"]!["mode"]!.GetValue<string>());
        Assert.Equal("Jane Doe", p["target"]!["customer"]!.GetValue<string>());
        Assert.Null(p["target"]!["ticketNumber"]);
        Assert.Contains(p["findings"]!.AsArray(), f => f!["severity"]!.GetValue<string>() == "Critical");
        var html = p["html"]!.GetValue<string>();
        Assert.Contains("All scan data", html);
        Assert.Contains("<summary>Storage", html);
        Assert.DoesNotContain("All scan data", HtmlReport.Write(Demo()));  // the printable report stays short
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
