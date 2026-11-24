using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// WindowCapture(M1에서 재사용)를 그대로 호출하는 얇은 ITool 어댑터 - 읽기 전용.
// 주의: 지금은 PNG를 디스크에 저장하고 경로만 텍스트로 돌려준다 - Claude에게 이미지를
// vision 블록으로 직접 보여주는 건 ClaudeClient가 아직 지원하지 않아서 이번 범위 밖
// (04-android-windows-bridge.md 플랜 참고).
public class ScreenshotWindowTool : ITool
{
    public string Name => "screenshot_window";
    public string Description =>
        "제목에 특정 문자열이 포함된 창을 PNG로 캡쳐해서 임시 폴더에 저장하고 그 경로를 반환한다 (읽기 전용).";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["titleContains"] = new JsonObject { ["type"] = "string", ["description"] = "찾을 창 제목에 포함된 부분 문자열" }
        },
        ["required"] = new JsonArray { "titleContains" }
    };

    public Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var titleContains = input.GetProperty("titleContains").GetString() ?? string.Empty;
        var outputPath = Path.Combine(Path.GetTempPath(), $"LocalAssistant-shot-{Guid.NewGuid():N}.png");

        var ok = WindowCapture.CaptureWindowByTitle(titleContains, outputPath);
        var result = ok
            ? $"저장됨: {outputPath}"
            : $"창을 찾지 못했거나 캡쳐 실패: \"{titleContains}\"";
        return Task.FromResult(result);
    }
}
