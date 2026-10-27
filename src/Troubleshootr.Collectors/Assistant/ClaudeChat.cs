using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Troubleshootr.Core;

namespace Troubleshootr.Collectors.Assistant;

/// <summary>
/// One "Ask Claude" conversation over the Messages API, with the user's own API key. Raw HTTP on purpose: the official
/// C# SDK is a 10 MB DLL plus dependencies, over the app's 15 MB budget. The history is append-only (assistant turns are
/// stored exactly as returned, thinking blocks included), which keeps prompt caching and preserved thinking intact.
/// </summary>
public sealed class ClaudeChat : IDisposable
{
    public const string Model = "claude-opus-5-5";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const int MaxToolRounds = 12;
    private const int MaxRetries = 2;

    private readonly HttpClient _http;
    private readonly AssistantContext _context;
    private readonly JsonArray _messages = new();

    /// <param name="handler">For tests; null uses the network.</param>
    public ClaudeChat(string apiKey, AssistantContext context, HttpMessageHandler? handler = null)
    {
        _context = context;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey.Trim());
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        // Server-side fallback: if a safety classifier declines, the API retries on its recommended model in the same call.
        _http.DefaultRequestHeaders.Add("anthropic-beta", "server-side-fallback-2026-07-01");
    }

    /// <summary>Tokens used so far (input includes cache reads), for the status line.</summary>
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CachedTokens { get; private set; }

    /// <summary>Asks a question; runs Claude's tool calls against the scan until it answers. Throws ClaudeException with a readable message.</summary>
    public async Task<string> AskAsync(string question, Action<string>? status, CancellationToken ct)
    {
        var rollback = _messages.Count;
        // The report goes in the first user message, so it's cached with the system prompt and tools for later turns.
        var content = new JsonArray();
        if (_messages.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = _context.OpeningContext() });
        content.Add(new JsonObject { ["type"] = "text", ["text"] = question });
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        try
        {
            var answer = new StringBuilder();
            for (var round = 0; round <= MaxToolRounds; round++)
            {
                status?.Invoke(round == 0 ? "Claude is thinking…" : "Claude is reading the scan data…");
                var response = await SendAsync(ct).ConfigureAwait(false);
                var reply = response["content"] as JsonArray ?? new JsonArray();
                var stop = response["stop_reason"]?.GetValue<string>();
                if (stop == "refusal")
                {
                    var category = response["stop_details"]?["category"]?.GetValue<string>();
                    throw new ClaudeException($"Claude declined to answer this{(category is null ? "" : $" ({category})")}. Try rephrasing the question.");
                }
                _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.DeepClone() });
                foreach (var block in reply.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "text"))
                    answer.Append(block["text"]?.GetValue<string>());
                if (stop != "tool_use")
                {
                    if (stop == "max_tokens") answer.Append("\n\n(The answer was cut off at the length limit.)");
                    return answer.ToString().Trim();
                }
                // Run every tool call of this turn and send all results back in one user message.
                var results = new JsonArray();
                foreach (var call in reply.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use"))
                {
                    var (text, isError) = _context.RunTool(call["name"]?.GetValue<string>() ?? "", call["input"]);
                    var result = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = call["id"]?.GetValue<string>(), ["content"] = text };
                    if (isError) result["is_error"] = true;
                    results.Add(result);
                }
                _messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                if (answer.Length > 0) answer.Append("\n\n");
            }
            throw new ClaudeException("Claude kept looking things up without answering; try a narrower question.");
        }
        catch
        {
            // Drop the unanswered turn so the next question starts from a valid history.
            while (_messages.Count > rollback) _messages.RemoveAt(_messages.Count - 1);
            throw;
        }
    }

    /// <summary>The request body, built fresh each round from the same stable parts (tools → system → messages).</summary>
    public JsonObject Body() => new()
    {
        ["model"] = Model,
        ["max_tokens"] = 16000,
        ["fallbacks"] = "default",
        ["output_config"] = new JsonObject { ["effort"] = "medium" },
        // Automatic prompt caching: the breakpoint moves to the end of the conversation each turn.
        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        ["system"] = AssistantContext.SystemPrompt,
        ["tools"] = _context.Tools(),
        ["messages"] = _messages.DeepClone(),
    };

    private async Task<JsonObject> SendAsync(CancellationToken ct)
    {
        var body = Body().ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                if (attempt < MaxRetries) { await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false); continue; }
                throw new ClaudeException($"Couldn't reach Claude: {e.InnerException?.Message ?? e.Message}. Check the internet connection.", e);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new ClaudeException("Claude didn't answer within 5 minutes; try again.", e);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var json = JsonNode.Parse(text) as JsonObject ?? throw new ClaudeException("Claude sent an empty answer.");
                    if (json["usage"] is JsonObject u)
                    {
                        var cached = Long(u["cache_read_input_tokens"]);
                        InputTokens += Long(u["input_tokens"]) + Long(u["cache_creation_input_tokens"]) + cached;
                        CachedTokens += cached;
                        OutputTokens += Long(u["output_tokens"]);
                    }
                    return json;
                }
                var retryable = code is 408 or 409 or 429 or >= 500;
                if (retryable && attempt < MaxRetries)
                {
                    await Task.Delay(Backoff(attempt, response.Headers.RetryAfter?.Delta), ct).ConfigureAwait(false);
                    continue;
                }
                throw new ClaudeException(Describe(code, text));
            }
        }
    }

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } r && r <= TimeSpan.FromSeconds(60) ? r : TimeSpan.FromSeconds(2 << attempt);

    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    /// <summary>A plain-English message for an API error; the API's own message is kept for anything unusual.</summary>
    public static string Describe(int status, string body)
    {
        string? message = null;
        try { message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch (JsonException) { }
        return status switch
        {
            401 => "The API key wasn't accepted. Check it on console.anthropic.com and enter it again.",
            403 => $"This API key isn't allowed to do that{(message is null ? "" : $": {message}")}.",
            413 => "The scan is too large to send in one go; ask about one area at a time.",
            429 => "Too many requests or out of API credit for now. Wait a minute, or check billing on console.anthropic.com.",
            529 or 503 => "Claude is overloaded right now; try again in a minute.",
            >= 500 => "Claude had a server error; try again in a minute.",
            _ => $"Claude returned an error ({status}){(message is null ? "" : $": {message}")}.",
        };
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ClaudeException(string message, Exception? inner = null) : Exception(message, inner);
