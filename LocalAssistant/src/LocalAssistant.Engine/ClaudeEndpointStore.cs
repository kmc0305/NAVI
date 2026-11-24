namespace LocalAssistant.Engine;

// Claude를 어디로 호출할지(Anthropic 직접 vs 구독 재사용 게이트웨이) 저장.
// 비밀 값이 아니라 URL 하나라 AllowedFoldersStore와 같은 평문 저장 패턴.
public static class ClaudeEndpointStore
{
    // corebrain-gateway 컨테이너가 로컬에 떠 있을 때의 기본 주소.
    // nginx(gateway.conf)가 /llmproxy/ 밑으로 들어오는 요청을 CLIProxyAPI(OAuth로
    // 로그인된 Claude 구독)로 그대로 넘겨준다 - Anthropic Messages API와 동일한 모양.
    public const string DefaultBaseUrl = "http://localhost:7788/llmproxy/v1/";

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");

    private static string FilePath => Path.Combine(DataDir, "endpoint.txt");

    public static string Load()
    {
        if (!File.Exists(FilePath)) return DefaultBaseUrl;
        var saved = File.ReadAllText(FilePath).Trim();
        return string.IsNullOrWhiteSpace(saved) ? DefaultBaseUrl : saved;
    }

    public static void Save(string baseUrl)
    {
        Directory.CreateDirectory(DataDir);
        var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        File.WriteAllText(FilePath, normalized);
    }
}
