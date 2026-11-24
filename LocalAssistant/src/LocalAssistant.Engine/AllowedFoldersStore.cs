using System.Text.Json;

namespace LocalAssistant.Engine;

// Whitelist for RunCommandTool (03-orchestrator.md 3.3 반전: 임의 쉘 실행을 허용하되,
// corebrain의 "folder add --scopes exec" 패턴처럼 등록된 폴더 밖에서는 구조적으로 거부).
// 비밀 값이 아니라 경로 목록이라 DPAPI 암호화 없이 평문 JSON으로 저장.
public static class AllowedFoldersStore
{
    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");

    private static string FilePath => Path.Combine(DataDir, "allowed-folders.json");

    public static IReadOnlyList<string> Load()
    {
        if (!File.Exists(FilePath)) return Array.Empty<string>();
        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
    }

    public static void Add(string path)
    {
        var normalized = Path.GetFullPath(path);
        var folders = Load().ToList();
        if (!folders.Any(f => string.Equals(f, normalized, StringComparison.OrdinalIgnoreCase)))
            folders.Add(normalized);

        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(folders));
    }

    // workingDirectory가 등록된 폴더 중 하나와 같거나 그 하위 경로일 때만 허용.
    public static bool IsAllowed(string workingDirectory)
    {
        string normalized;
        try { normalized = Path.GetFullPath(workingDirectory); }
        catch { return false; }

        return Load().Any(allowed =>
            normalized.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
