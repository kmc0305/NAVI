namespace LocalAssistant.Engine;

// 세션이 바뀔 때(새 대화 시작) 방금 끝난 세션의 요약을 여기 누적 저장해두고, 다음에
// 새 세션을 열 때 시스템 프롬프트에 끼워 넣어서 과거 대화를 "기억"하게 한다.
// (03-orchestrator.md가 가리키던 "memory.md" 아이디어를 실제로 구현한 것 - ClaudeClient.cs의
// 캐시 주석이 이걸 참조하고 있었지만 지금까지는 실제로 만들어진 적이 없었다.)
public static class MemoryStore
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant", "memory.md");

    public static string Load() => File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;

    public static void Append(string sessionTitle, string summary)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var entry = $"## {sessionTitle} ({DateTimeOffset.Now:yyyy-MM-dd HH:mm})\n{summary.Trim()}\n\n";
        File.AppendAllText(FilePath, entry);
    }
}
