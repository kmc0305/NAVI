using System.Text.Json;

namespace LocalAssistant.Engine;

// Claude에게 이미지를 직접 보여줘야 하는 도구(예: see_screen)를 위한 확장 계약.
// 기존 ITool.ExecuteAsync(문자열만 반환)는 그대로 두고, 이 인터페이스를 추가로
// 구현한 도구만 Orchestrator가 ExecuteImageAsync로 호출해 tool_result에 이미지
// 블록을 함께 실어 보낸다 (다른 8개 도구는 전혀 손대지 않음).
public interface IImageTool : ITool
{
    Task<ToolImageResult> ExecuteImageAsync(JsonElement input, Action<string> log);
}

public record ToolImageResult(string Text, byte[]? ImageBytes, string MediaType = "image/png");
