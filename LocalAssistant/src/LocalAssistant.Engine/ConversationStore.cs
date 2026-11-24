using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// Holds this chat room's message history in the exact shape the Anthropic
// Messages API expects ({role, content}), so it can be sent back verbatim on
// the next turn. Persisted to a local JSON file so the conversation survives
// an app restart (03-orchestrator.md 2.1).
public class ConversationStore
{
    private readonly string _path;
    private readonly JsonArray _messages;

    public ConversationStore(string path)
    {
        _path = path;
        _messages = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))!.AsArray()
            : new JsonArray();
    }

    public JsonArray Messages => _messages;

    // Trims at the start of a real new user utterance only, never in the
    // middle of a tool_use/tool_result exchange (the API errors if that pair
    // is split across the trim point). Local storage keeps everything; only
    // what's sent to Claude is capped (03-orchestrator.md 7).
    public JsonArray RecentForApi(int maxUserTurns = 20)
    {
        var userTurnStarts = new List<int>();
        for (var i = 0; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg?["role"]?.GetValue<string>() == "user" && msg["content"] is JsonValue)
                userTurnStarts.Add(i);
        }

        if (userTurnStarts.Count <= maxUserTurns) return _messages;

        var startIndex = userTurnStarts[userTurnStarts.Count - maxUserTurns];
        var trimmed = new JsonArray();
        for (var i = startIndex; i < _messages.Count; i++)
            trimmed.Add(_messages[i]!.DeepClone());
        return trimmed;
    }

    public void AddUserText(string text)
    {
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        Save();
    }

    public void AddAssistantBlocks(JsonArray contentBlocks)
    {
        _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = contentBlocks.DeepClone() });
        Save();
    }

    public void AddToolResult(string toolUseId, string resultText)
    {
        var block = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = resultText
            }
        };
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = block });
        Save();
    }

    // see_screen 같은 IImageTool용 - tool_result의 content를 문자열 대신 블록 배열로 만들어
    // 텍스트 설명 다음에 이미지(base64)를 함께 실어 보낸다. Claude가 실제로 화면을 "보고"
    // 판단해야 하는 도구 결과에만 쓰고, 나머지 도구는 기존 AddToolResult(string)를 그대로 쓴다.
    //
    // 중요: 새 이미지를 넣기 전에 과거 이미지는 전부 텍스트로 치환한다 - 안 그러면 스크린샷을
    // 찍을 때마다 그 전에 찍었던 모든 스크린샷까지 매 Claude 호출마다 다시 통째로 전송되어
    // (RecentForApi가 전체 히스토리를 돌려주므로) 대화가 길어질수록 입력 토큰과 응답 시간이
    // 계속 불어난다. Claude는 보통 "지금" 화면만 필요하므로 최신 1장만 유지해도 충분하다.
    public void AddToolResultWithImage(string toolUseId, string resultText, byte[] imageBytes, string mediaType)
    {
        StripOldImages();
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = resultText },
            new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = mediaType,
                    ["data"] = Convert.ToBase64String(imageBytes)
                }
            }
        };
        var block = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = content
            }
        };
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = block });
        Save();
    }

    // 과거에 누적된 스크린샷을 지금 당장 정리하고 싶을 때 쓰는 공개 진입점
    // (예: 이미 비대해진 기존 conversation.json을 한 번 청소할 때).
    public void TrimAllImageHistory()
    {
        StripOldImages();
        Save();
    }

    private void StripOldImages()
    {
        foreach (var msg in _messages)
        {
            if (msg?["content"] is not JsonArray blocks) continue;
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() != "tool_result") continue;
                if (block["content"] is not JsonArray innerContent) continue;
                for (var i = 0; i < innerContent.Count; i++)
                {
                    if (innerContent[i]?["type"]?.GetValue<string>() == "image")
                        innerContent[i] = new JsonObject { ["type"] = "text", ["text"] = "[이전 스크린샷 - 최신 화면만 유지하기 위해 생략됨]" };
                }
            }
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, _messages.ToJsonString());
    }
}
