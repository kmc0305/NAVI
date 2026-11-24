using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// corebrain-gateway의 /api/{group}/{tool} 로컬 REST API 호출용 얇은 래퍼
// (BrowserTool이 사용 - exec/files는 샌드박스 볼륨이라 네이티브 도구로 대체).
public class GatewayClient
{
    private const string DefaultBaseUrl = "http://localhost:7788/api/";
    private readonly HttpClient _http;

    public GatewayClient(HttpClient http, string gatewayKey, string baseUrl = DefaultBaseUrl)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gatewayKey);
    }

    // group 예: "browser", tool 예: "browser_navigate".
    public async Task<JsonObject> CallAsync(string group, string tool, JsonObject body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{group}/{tool}", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["ok"] = false, ["error"] = text };
    }
}
