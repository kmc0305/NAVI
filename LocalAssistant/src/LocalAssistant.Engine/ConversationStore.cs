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
