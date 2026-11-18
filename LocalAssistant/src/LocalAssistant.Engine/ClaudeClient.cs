using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

public record ClaudeResult(string StopReason, JsonArray ContentBlocks)
{
    public string? Text => ContentBlocks
        .FirstOrDefault(b => b?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();

    public IEnumerable<(string Id, string Name, JsonElement Input)> ToolUses() =>
        ContentBlocks
            .Where(b => b?["type"]?.GetValue<string>() == "tool_use")
            .Select(b => (
                b!["id"]!.GetValue<string>(),
                b["name"]!.GetValue<string>(),
                JsonDocument.Parse(b["input"]!.ToJsonString()).RootElement));
}

// Thin wrapper over the Anthropic Messages API (tool use). Deliberately not a
// full SDK - just what the Orchestrator loop needs.
public class ClaudeClient
{
    private const string Model = "claude-sonnet-5-5";
    private const string ApiVersion = "2023-06-01";
    private readonly HttpClient _http;

    public ClaudeClient(HttpClient http, string apiKey)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://api.anthropic.com/v1/");
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ClaudeResult> SendAsync(JsonArray messages, JsonArray tools, string systemPrompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 2048,
            ["system"] = systemPrompt,
            ["messages"] = messages.DeepClone(),
        };
        if (tools.Count > 0)
            body["tools"] = tools.DeepClone();

        using var response = await _http.PostAsJsonAsync("messages", body, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claude API 오류 {(int)response.StatusCode}: {responseText}");

        var json = JsonNode.Parse(responseText)!.AsObject();
        var stopReason = json["stop_reason"]?.GetValue<string>() ?? "end_turn";
        var content = json["content"]!.AsArray();
        return new ClaudeResult(stopReason, content);
    }
}
