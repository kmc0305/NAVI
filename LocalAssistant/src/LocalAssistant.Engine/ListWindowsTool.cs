using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// WindowLister(M1에서 재사용)를 그대로 호출하는 얇은 ITool 어댑터 - 읽기 전용.
public class ListWindowsTool : ITool
{
    public string Name => "list_windows";
    public string Description => "현재 떠 있는 최상위 창의 이름과 위치 목록을 반환한다 (읽기 전용).";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject()
    };

    public Task<string> ExecuteAsync(JsonElement input, Action<string> log) =>
        Task.FromResult(WindowLister.ListTopLevelWindows());
}
