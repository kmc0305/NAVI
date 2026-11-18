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

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, _messages.ToJsonString());
    }
}
