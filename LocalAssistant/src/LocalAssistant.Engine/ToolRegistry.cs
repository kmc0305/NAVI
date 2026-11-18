using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public ITool? Find(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;

    public JsonArray BuildToolDefinitions()
    {
        var array = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.InputSchema.DeepClone()
            });
        }
        return array;
    }
}
