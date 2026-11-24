using System.Security.Cryptography;
using System.Text;

namespace LocalAssistant.Engine;

public static class ApiKeyStore
{
    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");

    private static string FilePath => Path.Combine(DataDir, "apikey.dat");

    public static void Save(string apiKey)
    {
        Directory.CreateDirectory(DataDir);
        var plainBytes = Encoding.UTF8.GetBytes(apiKey);
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
