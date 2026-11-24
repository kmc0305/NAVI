using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// files_read와 짝 - 허용된 폴더 안에서만 파일을 새로 만들거나 덮어쓴다.
public class FilesWriteTool : ITool
{
    public string Name => "files_write";
    public string Description =>
        "허용된 폴더 안에 파일을 새로 만들거나 덮어쓴다. 상위 폴더가 없으면 만든다.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "쓸 파일의 절대 경로" },
            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "파일 내용 (UTF-8)" }
        },
        ["required"] = new JsonArray { "path", "content" }
    };

    public async Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var content = input.GetProperty("content").GetString() ?? string.Empty;

        if (!AllowedFoldersStore.IsAllowed(Path.GetDirectoryName(path) ?? path))
            return $"거부됨: \"{path}\"는 허용된 폴더가 아닙니다. 먼저 'LocalAssistant.Cli addfolder <폴더>'로 등록하세요.";

        log($"쓰는 중: {path}");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);

        return $"저장됨: {path} ({content.Length} bytes)";
    }
}
