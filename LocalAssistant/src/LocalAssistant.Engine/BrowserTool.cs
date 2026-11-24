using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// corebrain-gateway의 /api/browser(Playwright)를 호출 - LocalAssistant에 없던
// 브라우저 자동화 기능을 CORE 게이트웨이에서 빌려옴. exec/files와 달리 브라우저는
// 결과(텍스트/스크린샷)만 대화로 돌아오면 되므로 컨테이너 안에서 헤드리스로 떠도 의미가 있음.
public class BrowserTool : ITool
{
    private const string SessionName = "localassistant";
    private const string ProfileName = "personal";

    private readonly GatewayClient _gateway;

    public BrowserTool(GatewayClient gateway) => _gateway = gateway;

    public string Name => "browser_use";
    public string Description =>
        "corebrain-gateway의 Playwright 브라우저를 조작한다 (navigate/snapshot/click/fill/type/press_key/screenshot/evaluate/scroll/close). " +
        "결과 텍스트/스크린샷 경로만 돌아온다.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "navigate", "snapshot", "click", "fill", "type", "press_key", "screenshot", "evaluate", "scroll", "close" }
            },
            ["url"] = new JsonObject { ["type"] = "string", ["description"] = "navigate에 필요" },
            ["element"] = new JsonObject { ["type"] = "string", ["description"] = "click/fill/type 대상 요소 설명" },
            ["ref"] = new JsonObject { ["type"] = "string", ["description"] = "CSS/ARIA 셀렉터 (element보다 우선)" },
            ["value"] = new JsonObject { ["type"] = "string", ["description"] = "fill에 입력할 값" },
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "type으로 입력할 텍스트" },
            ["key"] = new JsonObject { ["type"] = "string", ["description"] = "press_key로 누를 키 (예: Enter)" },
            ["script"] = new JsonObject { ["type"] = "string", ["description"] = "evaluate로 실행할 JS" }
        },
        ["required"] = new JsonArray { "action" }
    };

    public async Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var ct = CancellationToken.None;
        var action = input.GetProperty("action").GetString() ?? string.Empty;

        if (action == "navigate")
            await EnsureSessionAsync(log, ct);

        var body = new JsonObject { ["session"] = SessionName };
        string tool;
        switch (action)
        {
            case "navigate":
                tool = "browser_navigate";
                body["url"] = GetOptionalString(input, "url");
                break;
            case "snapshot":
                tool = "browser_snapshot";
                break;
            case "click":
                tool = "browser_click";
                body["element"] = GetOptionalString(input, "element");
                body["ref"] = GetOptionalString(input, "ref");
                break;
            case "fill":
                tool = "browser_fill";
                body["element"] = GetOptionalString(input, "element");
                body["value"] = GetOptionalString(input, "value");
                body["ref"] = GetOptionalString(input, "ref");
                break;
            case "type":
                tool = "browser_type";
                body["element"] = GetOptionalString(input, "element");
                body["text"] = GetOptionalString(input, "text");
                body["ref"] = GetOptionalString(input, "ref");
                break;
            case "press_key":
                tool = "browser_press_key";
                body["key"] = GetOptionalString(input, "key");
                break;
            case "screenshot":
                tool = "browser_screenshot";
                break;
            case "evaluate":
                tool = "browser_evaluate";
                body["script"] = GetOptionalString(input, "script");
                break;
            case "scroll":
                tool = "browser_scroll";
                break;
            case "close":
                tool = "browser_close_session";
                break;
            default:
                return $"알 수 없는 action: {action}";
        }

        log($"브라우저: {action}");
        var response = await _gateway.CallAsync("browser", tool, body, ct);
        return response.ToJsonString();
    }

    private async Task EnsureSessionAsync(Action<string> log, CancellationToken ct)
    {
        var body = new JsonObject { ["session"] = SessionName, ["profile"] = ProfileName };
        var response = await _gateway.CallAsync("browser", "browser_create_session", body, ct);
        var ok = response["ok"]?.GetValue<bool>() ?? false;
        if (ok) { log($"브라우저 세션 생성: {SessionName}"); return; }

        var error = response["error"]?["message"]?.GetValue<string>() ?? string.Empty;
        if (!error.Contains("already exists"))
            log($"브라우저 세션 생성 실패(계속 진행): {error}");
    }

    private static string? GetOptionalString(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) ? value.GetString() : null;
}
