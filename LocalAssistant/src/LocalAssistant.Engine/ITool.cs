using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonObject InputSchema { get; }
    Task<string> ExecuteAsync(JsonElement input, Action<string> log);
}
