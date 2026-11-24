using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

public record ClaudeResult(
    string StopReason, JsonArray ContentBlocks, int InputTokens, int OutputTokens,
    int CacheCreationInputTokens = 0, int CacheReadInputTokens = 0)
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

    public ClaudeClient(HttpClient http, string apiKey, string baseUrl = "https://api.anthropic.com/v1/")
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl);
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // 게이트웨이(corebrain-gateway)가 Anthropic으로 중계하다가 연결이 끊기는 등
    // 일시적인 네트워크 끊김(예: "unexpected EOF")이 실측으로 확인됨 - 코드 버그가
    // 아니라 순간적인 블립이라, 바로 사용자에게 에러를 보여주는 대신 몇 번 재시도한다.
    // 4xx(요청 자체가 잘못됨)는 재시도해도 똑같이 실패하므로 재시도하지 않는다.
    private const int MaxAttempts = 3;

    public async Task<ClaudeResult> SendAsync(
        JsonArray messages, JsonArray tools, string systemPrompt, CancellationToken ct, Action<string>? onStatus = null)
    {
        // Cache breakpoints (03-orchestrator.md 7): system prompt + memory.md and the
        // tool list are both near-fixed across calls, so marking each with
        // cache_control gets a ~90% input-token discount on every call after the
        // first. A plain string "system" or an un-marked tools array is never cached
        // - Anthropic's caching is opt-in per block, not automatic.
        //
        // 실측으로 확인된 중요한 사실: corebrain-gateway의 llmproxy(구독 재사용,
        // CLIProxyAPI가 Claude Code 자체 세션 프로토콜을 흉내냄)를 거치면 이 "system"
        // 필드가 조용히 무시된다 - 극단적인 지시("바나나 한 단어로만 답해라")를 system에
        // 넣었을 때 전혀 안 먹혔고, 같은 지시를 messages 맨 앞에 user/assistant 쌍으로
        // 끼워 넣었을 때는 정확히 먹혔다. 그래서 system 필드는 그대로 두고(실제
        // api.anthropic.com로 직접 붙였을 때는 정상 동작하도록), messages 맨 앞에도 같은
        // 지시를 끼워 넣어 - 게이트웨이를 거치든 직접 호출이든 항상 작동하게 한다.
        //
        // 이 fallback 쌍은 호출마다 내용이 똑같으니 cache_control을 달아서 캐시 대상으로
        // 만든다(ack 블록 끝에 체크포인트).
        var messagesWithSystemFallback = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = systemPrompt },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "네, 알겠습니다. 그 지침에 따라 답하겠습니다.",
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                    },
                },
            },
        };
        foreach (var m in messages) messagesWithSystemFallback.Add(m!.DeepClone());

        // 롤링 캐시 breakpoint (03-orchestrator.md 7): 멀티라운드 도구 호출 루프에서는
        // history가 라운드마다 계속 커지는데, 지금까지는 system/tools만 캐시되고 실제
        // 대화 내용(messages)은 매번 통째로 다시 과금됐다(실측: 라운드가 늘어날수록
        // 캐시적중은 고정, 입력 토큰만 계속 증가). 매 호출의 "현재까지 보낸 마지막 블록"에
        // 체크포인트를 찍어두면, 다음 라운드에서 그 지점까지는 캐시로 읽고 그 뒤에 새로
        // 추가된 것만 새로 과금된다. system(1) + tools(1) + fallback ack(1) + 이 tail(1) =
        // 요청당 최대 4개인 Anthropic 한도에 정확히 맞는다.
        AddRollingCacheBreakpoint(messagesWithSystemFallback);

        var body = new JsonObject
        {
            ["model"] = Model,
            // 2048은 확장 사고(thinking)가 끼면 응답 하나가 그 안에서 다 못 끝나고 잘릴 만큼
            // 작았다(실측: thinking 블록만 채우고 tool_use/text 없이 멈춤). 구독 재사용
            // 게이트웨이라 토큰당 과금은 아니므로 여유 있게 늘린다.
            ["max_tokens"] = 8192,
            ["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            },
            ["messages"] = messagesWithSystemFallback,
        };
        if (tools.Count > 0)
        {
            var cachedTools = tools.DeepClone().AsArray();
            (cachedTools[^1] as JsonObject)!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            body["tools"] = cachedTools;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync("messages", body, ct);
                var responseText = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    if (status >= 500 && attempt < MaxAttempts)
                    {
                        onStatus?.Invoke($"일시적 오류, 재시도 중 ({attempt}/{MaxAttempts})...");
                        await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), ct);
                        continue;
                    }
                    throw new InvalidOperationException($"Claude API 오류 {status}: {responseText}");
                }

                var json = JsonNode.Parse(responseText)!.AsObject();
                var stopReason = json["stop_reason"]?.GetValue<string>() ?? "end_turn";
                var content = json["content"]!.AsArray();
                var usage = json["usage"];
                var inputTokens = usage?["input_tokens"]?.GetValue<int>() ?? 0;
                var outputTokens = usage?["output_tokens"]?.GetValue<int>() ?? 0;
                var cacheCreationTokens = usage?["cache_creation_input_tokens"]?.GetValue<int>() ?? 0;
                var cacheReadTokens = usage?["cache_read_input_tokens"]?.GetValue<int>() ?? 0;
                return new ClaudeResult(stopReason, content, inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                lastError = ex;
                onStatus?.Invoke($"연결 오류, 재시도 중 ({attempt}/{MaxAttempts})...");
                await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), ct);
            }
        }

        throw lastError ?? new InvalidOperationException("Claude 호출 실패: 재시도 초과");
    }

    // messages는 호출자(Orchestrator)가 들고 있는 ConversationStore의 원본이거나
    // 그 참조일 수 있다(RecentForApi가 트림이 필요 없을 때 원본을 그대로 돌려줌) - 이
    // 메서드는 항상 SendAsync 안에서 이미 DeepClone된 messagesWithSystemFallback에만
    // 호출되므로, 여기서 마지막 블록을 직접 고쳐도 로컬 저장소 원본은 건드리지 않는다.
    //
    // 실측으로 확인된 함정: 방금 추가된 스크린샷(이미지)이 들어있는 메시지에 체크포인트를
    // 찍으면, 바로 다음 라운드에서 또 see_screen을 부를 때 ConversationStore.StripOldImages가
    // 그 이미지를 텍스트로 치환해버려서 캐시가 즉시 무효화된다(cache_creation 비용만 내고
    // 한 번도 못 읽는 상황). 그래서 뒤에서부터 훑어 이미지가 없는 가장 최근 메시지를 찾아
    // 거기에 체크포인트를 찍는다 - 다음 라운드에서 이미지가 또 치환돼도 그 지점 이전은
    // 안전하게 캐시로 재사용된다.
    private static void AddRollingCacheBreakpoint(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject candidate) continue;
            if (ContainsImage(candidate)) continue;
            if (MarkLastBlockCacheable(candidate)) return;
        }
    }

    private static bool ContainsImage(JsonObject message)
    {
        if (message["content"] is not JsonArray blocks) return false;
        foreach (var block in blocks)
        {
            if (block?["type"]?.GetValue<string>() == "image") return true;
            if (block?["content"] is JsonArray inner &&
                inner.Any(b => b?["type"]?.GetValue<string>() == "image"))
                return true;
        }
        return false;
    }

    private static bool MarkLastBlockCacheable(JsonObject message)
    {
        if (message["content"] is JsonValue stringContent && stringContent.TryGetValue<string>(out var text))
        {
            message["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            };
            return true;
        }

        if (message["content"] is JsonArray blocks && blocks.Count > 0 && blocks[^1] is JsonObject lastBlock)
        {
            lastBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            return true;
        }

        return false;
    }

    // 연결 상태 UI가 주기적으로 부른다 - 실제 메시지를 보내면 토큰이 들고 느리니, 베이스
    // URL에 가벼운 GET만 날려 응답이 오는지만 본다. 상태 코드는 무엇이든(404/405 등) 응답
    // 자체가 왔다는 게 네트워크/게이트웨이가 살아있다는 뜻이라 신경 쓰지 않는다.
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync(string.Empty, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
