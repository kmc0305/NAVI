using System.Security.Cryptography;
using System.Text;

namespace LocalAssistant.Engine;

// corebrain-gateway의 COREBRAIN_GATEWAY_SECURITY_KEY(/api/* 로컬 REST API 인증용).
// /llmproxy/용 ApiKeyStore 키와는 별개 값 - 같은 DPAPI 암호화 저장 패턴.
public static class GatewayKeyStore
{
    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");

    private static string FilePath => Path.Combine(DataDir, "gatewaykey.dat");

    public static void Save(string key)
    {
        Directory.CreateDirectory(DataDir);
        var plainBytes = Encoding.UTF8.GetBytes(key);
        var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, protectedBytes);
    }

    public static string? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var protectedBytes = File.ReadAllBytes(FilePath);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
