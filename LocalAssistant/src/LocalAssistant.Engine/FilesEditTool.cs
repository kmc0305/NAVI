using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// files_read/files_write와 짝 - 파일 전체를 덮어쓰지 않고 정확히 일치하는 구간만 교체.
// old_string이 여러 번 나오면 replace_all 없이는 거부 (의도치 않은 다중 치환 방지).
public class FilesEditTool : ITool
{
    public string Name => "files_edit";
    public string Description =>
        "허용된 폴더 안 파일에서 정확히 일치하는 텍스트 구간만 교체한다. " +
        "old_string이 여러 번 나오면 replace_all=true가 아닌 이상 거부된다.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "수정할 파일의 절대 경로" },
            ["old_string"] = new JsonObject { ["type"] = "string", ["description"] = "교체할 정확한 텍스트" },
            ["new_string"] = new JsonObject { ["type"] = "string", ["description"] = "새 텍스트" },
            ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "모든 일치 항목 교체 (기본 false)" }
        },
        ["required"] = new JsonArray { "path", "old_string", "new_string" }
    };

    public async Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var oldString = input.GetProperty("old_string").GetString() ?? string.Empty;
        var newString = input.GetProperty("new_string").GetString() ?? string.Empty;
        var replaceAll = input.TryGetProperty("replace_all", out var r) && r.GetBoolean();

        if (!AllowedFoldersStore.IsAllowed(Path.GetDirectoryName(path) ?? path))
            return $"거부됨: \"{path}\"는 허용된 폴더가 아닙니다. 먼저 'LocalAssistant.Cli addfolder <폴더>'로 등록하세요.";

        if (!File.Exists(path))
            return $"파일을 찾을 수 없습니다: \"{path}\"";

        var original = await File.ReadAllTextAsync(path);
        var occurrences = original.Split(oldString).Length - 1;

        if (occurrences == 0)
            return "old_string이 파일에서 발견되지 않았습니다. files_read로 다시 읽어 공백까지 정확히 맞추세요.";
        if (occurrences > 1 && !replaceAll)
            return $"old_string이 {occurrences}번 나타납니다. 더 긴 유일한 구간을 쓰거나 replace_all=true를 지정하세요.";

        log($"수정 중: {path}");
        var updated = replaceAll
            ? original.Replace(oldString, newString)
            : ReplaceFirst(original, oldString, newString);

        await File.WriteAllTextAsync(path, updated);
        return $"수정됨: {path} (교체 {(replaceAll ? occurrences : 1)}건)";
    }

    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + replacement + text[(index + search.Length)..];
    }
}
