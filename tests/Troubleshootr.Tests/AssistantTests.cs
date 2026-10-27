using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Troubleshootr.Checks;
using Troubleshootr.Collectors.Assistant;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

public class AssistantTests
{
    /// <summary>Plays back canned API responses and keeps every request body.</summary>
    private sealed class FakeApi(params (HttpStatusCode Code, string Body)[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<JsonObject> Requests { get; } = new();
        public List<HttpRequestMessage> Raw { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Raw.Add(request);
            Requests.Add((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync())!);
            var (code, body) = replies[_next++];
            var response = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (code == (HttpStatusCode)429) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }
    }

    private static Report Demo()
    {
        var path = Path.Combine(Paths.Repo(), "tests", "fixtures", "demo-many-problems.snap.json");
        var s = Json.Deserialize<Snapshot>(File.ReadAllText(path));
        s.System!.Value!.DeviceName = "KITCHEN-PC";
        var r = ScanEngine.Evaluate(s, CheckRegistry.All, new ScanOptions { ToolVersion = "1.1.0" });
        r.Snapshot = s;
        r.Device.DeviceName = "KITCHEN-PC";
        return r;
    }

    private const string ToolUse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"tool_use",
         "content":[{"type":"thinking","thinking":"","signature":"sig123"},
                    {"type":"tool_use","id":"toolu_1","name":"get_section","input":{"section":"Storage"}},
                    {"type":"tool_use","id":"toolu_2","name":"search_scan","input":{"text":"WDC WD10EZEX"}}],
         "usage":{"input_tokens":10,"output_tokens":20,"cache_creation_input_tokens":5000,"cache_read_input_tokens":0}}
        """;

    private const string Answer = """
        {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"end_turn",
         "content":[{"type":"text","text":"Disk 1 is failing: 312 reallocated sectors."}],
         "usage":{"input_tokens":12,"output_tokens":30,"cache_creation_input_tokens":0,"cache_read_input_tokens":5000}}
        """;

    [Fact]
    public async Task ToolLoopRunsToolsAndEchoesTheAssistantTurnUnchanged()
    {
        var api = new FakeApi((HttpStatusCode.OK, ToolUse), (HttpStatusCode.OK, Answer));
        using var chat = new ClaudeChat("sk-ant-test", new AssistantContext(Demo()), api);

        var answer = await chat.AskAsync("Is the hard drive dying?", null, CancellationToken.None);

        Assert.Equal("Disk 1 is failing: 312 reallocated sectors.", answer);
        var first = api.Requests[0];
        Assert.Equal("claude-opus-5-5", first["model"]!.GetValue<string>());
        Assert.Equal("default", first["fallbacks"]!.GetValue<string>());
        Assert.Equal("ephemeral", first["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("medium", first["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(first["thinking"]);  // Opus 5.5 always thinks; disabling it is a 400
        Assert.Null(first["tool_choice"]);  // forced tool choice is a 400 on Opus 5.5
        Assert.All(first["tools"]!.AsArray(), t => Assert.True(t!["strict"]!.GetValue<bool>()));
        Assert.Equal("sk-ant-test", api.Raw[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", api.Raw[0].Headers.GetValues("anthropic-version").Single());
        Assert.Equal("server-side-fallback-2026-07-01", api.Raw[0].Headers.GetValues("anthropic-beta").Single());

        var second = api.Requests[1]["messages"]!.AsArray();
        Assert.Equal(3, second.Count);
        // The assistant turn goes back byte-for-byte (thinking block and signature included).
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ToolUse)!["content"], second[1]!["content"]));
        var results = second[2]!["content"]!.AsArray();
        Assert.Equal(2, results.Count);  // both tool results in one user message
        Assert.Equal("toolu_1", results[0]!["tool_use_id"]!.GetValue<string>());
        Assert.Contains("WDC WD10EZEX", results[0]!["content"]!.GetValue<string>());
        Assert.Contains("Storage:", results[1]!["content"]!.GetValue<string>());
        Assert.Equal(10022L, chat.InputTokens);  // 10 + 5000 written, then 12 + 5000 read
        Assert.Equal(5000L, chat.CachedTokens);
    }

    [Fact]
    public async Task OpeningContextIsAnonymizedAndOnlyInTheFirstTurn()
    {
        var api = new FakeApi((HttpStatusCode.OK, Answer), (HttpStatusCode.OK, Answer));
        using var chat = new ClaudeChat("sk-ant-test", new AssistantContext(Demo()), api);
        await chat.AskAsync("First?", null, CancellationToken.None);
        await chat.AskAsync("Second?", null, CancellationToken.None);

        var all = api.Requests[1].ToJsonString();
        Assert.DoesNotContain("KITCHEN-PC", all);
        var messages = api.Requests[1]["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Contains("Scan report:", messages[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Contains("- Storage:", messages[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Single(messages[2]!["content"]!.AsArray());  // later questions don't repeat the report
    }

    [Fact]
    public async Task RefusalThrowsAndLeavesHistoryValid()
    {
        const string refusal = """{"id":"m","type":"message","role":"assistant","stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"},"content":[]}""";
        var api = new FakeApi((HttpStatusCode.OK, refusal), (HttpStatusCode.OK, Answer));
        using var chat = new ClaudeChat("sk-ant-test", new AssistantContext(Demo()), api);

        var e = await Assert.ThrowsAsync<ClaudeException>(() => chat.AskAsync("bad", null, CancellationToken.None));
        Assert.Contains("cyber", e.Message);
        await chat.AskAsync("good", null, CancellationToken.None);
        Assert.Single(api.Requests[1]["messages"]!.AsArray());  // the refused turn was dropped
    }

    [Fact]
    public async Task RetriesRateLimitsAndExplainsBadKeys()
    {
        var api = new FakeApi(((HttpStatusCode)429, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}"""), (HttpStatusCode.OK, Answer));
        using (var chat = new ClaudeChat("sk-ant-test", new AssistantContext(Demo()), api))
            Assert.StartsWith("Disk 1", await chat.AskAsync("q", null, CancellationToken.None));
        Assert.Equal(2, api.Requests.Count);

        var bad = new FakeApi((HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
        using var chat2 = new ClaudeChat("sk-ant-wrong", new AssistantContext(Demo()), bad);
        var e = await Assert.ThrowsAsync<ClaudeException>(() => chat2.AskAsync("q", null, CancellationToken.None));
        Assert.Contains("API key wasn't accepted", e.Message);
    }

    [Fact]
    public void ToolsRejectBadInputWithoutThrowing()
    {
        var ctx = new AssistantContext(Demo());
        Assert.True(ctx.RunTool("get_section", JsonNode.Parse("""{"section":"Nope"}""")).IsError);
        Assert.True(ctx.RunTool("search_scan", JsonNode.Parse("""{"text":"x"}""")).IsError);
        Assert.True(ctx.RunTool("format_disk", null).IsError);
        Assert.False(ctx.RunTool("get_section", JsonNode.Parse("""{"section":"memory"}""")).IsError);
        var big = ctx.RunTool("search_scan", JsonNode.Parse("""{"text":"\""}"""));
        Assert.True(big.Text.Length <= AssistantContext.MaxToolResult + 200);
    }
}
