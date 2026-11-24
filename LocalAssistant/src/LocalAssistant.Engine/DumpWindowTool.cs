using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// UiaTreeDumper(M1에서 재사용)를 그대로 호출하는 얇은 ITool 어댑터 - 읽기 전용.
public class DumpWindowTool : ITool
{
    public string Name => "dump_window";
    public string Description => "제목에 특정 문자열이 포함된 창의 UI 구조(버튼/텍스트 등)를 텍스트로 반환한다 (읽기 전용).";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["titleContains"] = new JsonObject { ["type"] = "string", ["description"] = "찾을 창 제목에 포함된 부분 문자열" },
            ["maxDepth"] = new JsonObject { ["type"] = "integer", ["description"] = "탐색할 최대 깊이 (기본 6)" }
        },
        ["required"] = new JsonArray { "titleContains" }
    };

    public Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var titleContains = input.GetProperty("titleContains").GetString() ?? string.Empty;
        var maxDepth = input.TryGetProperty("maxDepth", out var d) ? d.GetInt32() : 6;

        var tree = UiaTreeDumper.DumpWindowByTitle(titleContains, maxDepth);
        return Task.FromResult(tree ?? $"창을 찾지 못했습니다: 제목에 \"{titleContains}\" 포함된 창 없음");
    }
}
