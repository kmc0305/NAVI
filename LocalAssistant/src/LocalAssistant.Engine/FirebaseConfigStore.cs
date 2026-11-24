using System.Text.Json;

namespace LocalAssistant.Engine;

// 04-android-windows-bridge.md 2/3: PC가 Firestore에 붙기 위한 설정. 서비스 계정 JSON
// 자체는 저장소 밖 경로에 그대로 두고, 여기서는 그 경로/프로젝트ID/uid만 저장한다
// (ApiKeyStore와 같은 폴더, 암호화는 하지 않음 - 경로 문자열 자체는 비밀이 아님).
public record FirebaseConfig(string ProjectId, string Uid, string ServiceAccountJsonPath);

public static class FirebaseConfigStore
{
    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");

    private static string FilePath => Path.Combine(DataDir, "firebase-config.json");

    public static void Save(FirebaseConfig config)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config));
    }

    public static FirebaseConfig? Load()
    {
        if (!File.Exists(FilePath)) return null;
        return JsonSerializer.Deserialize<FirebaseConfig>(File.ReadAllText(FilePath));
    }
}
