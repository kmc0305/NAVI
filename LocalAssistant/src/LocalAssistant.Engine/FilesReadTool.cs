using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// corebrain-gateway의 files_read 설계(cat -n 스타일 줄번호)를 참고했지만, 실제로는
// 게이트웨이 컨테이너의 격리된 Docker 볼륨이 아니라 이 PC의 실제 파일을 직접 읽는다.
// AllowedFoldersStore 화이트리스트 재사용 (RunCommandTool과 동일한 가드레일).
public class FilesReadTool : ITool
{
    public string Name => "files_read";
    public string Description =>
        "허용된 폴더 안의 파일을 UTF-8 텍스트로 읽는다. 줄번호가 붙어 반환된다.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "읽을 파일의 절대 경로" },
            ["offset"] = new JsonObject { ["type"] = "number", ["description"] = "읽기 시작할 줄 (1부터, 기본 1)" },
            ["limit"] = new JsonObject { ["type"] = "number", ["description"] = "최대 줄 수 (기본 전체)" }
        },
        ["required"] = new JsonArray { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var offset = input.TryGetProperty("offset", out var o) ? o.GetInt32() : 1;
        var limit = input.TryGetProperty("limit", out var l) ? l.GetInt32() : int.MaxValue;

        if (!AllowedFoldersStore.IsAllowed(Path.GetDirectoryName(path) ?? path))
            return $"거부됨: \"{path}\"는 허용된 폴더가 아닙니다. 먼저 'LocalAssistant.Cli addfolder <폴더>'로 등록하세요.";

        if (!File.Exists(path))
            return $"파일을 찾을 수 없습니다: \"{path}\"";

        log($"읽는 중: {path}");
        var allLines = await File.ReadAllLinesAsync(path, Encoding.UTF8);
        var startIdx = Math.Max(0, offset - 1);
        var endIdx = Math.Min(allLines.Length, startIdx + limit);

        var sb = new StringBuilder();
        for (var i = startIdx; i < endIdx; i++)
            sb.AppendLine($"{i + 1,6}\t{allLines[i]}");

        return sb.ToString();
    }
}
