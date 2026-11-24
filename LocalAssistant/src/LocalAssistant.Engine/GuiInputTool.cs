using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

// UiaTreeDumper(읽기 전용)와 짝 - 실제 마우스 클릭/키보드 입력을 PC 화면에 보낸다.
// run_command/files_*는 폴더 화이트리스트(AllowedFoldersStore)로 범위를 제한하지만,
// 화면 좌표·키 입력은 폴더 단위로 제한할 수 있는 개념이 아니라서 그런 가드가 없다
// (사용자 요청: "클릭·키 입력 가능하게 완전 권한으로" - PC 전체에 대한 입력 제어).
public class GuiInputTool : ITool
{
    public string Name => "gui_input";
    public string Description =>
        "실제 PC 화면에 마우스 클릭/키보드 입력을 보낸다. 사용자가 클릭/입력/전송 같은 실제 조작을 " +
        "명확히 요청했을 때만 써라 - 단순히 화면을 확인하거나 설명해달라는 요청에는 쓰지 마라 " +
        "(그럴 때는 see_screen/dump_window로 보기만 할 것). action으로 동작을 고른다: " +
        "focus_window(titleContains로 찾은 창을 최소화/가려짐 상태와 상관없이 맨 앞으로 가져옴 - " +
        "다른 창을 조작하기 전에 먼저 호출하면 안전함), " +
        "click_element(titleContains로 창을 찾고 nameContains로 그 안의 요소를 찾아 클릭 - 그 창을 " +
        "자동으로 맨 앞으로 가져온 뒤 클릭하므로 따로 focus_window를 안 불러도 됨), " +
        "type_into_element(같은 방식으로 요소를 찾아 창을 앞으로 가져오고 포커스 준 뒤 text 입력), " +
        "click_at(see_screen이 보여준 이미지에서 본 x,y 픽셀 좌표와, 그 see_screen 응답에 적힌 " +
        "capture_id를 captureId로 같이 넘기면 실제 화면 좌표로 환산해서 클릭 - 그 사이에 see_screen을 " +
        "또 호출해서 capture_id가 바뀌었는데 옛 번호를 넘기면 좌표 기준이 틀려질 수 있어 클릭을 거부한다), " +
        "type_text(현재 포커스된 곳에 text 입력 - 직전에 어느 창이 포커스를 가졌는지 확실하지 않으면 " +
        "click_element/focus_window로 먼저 포커스를 확실히 옮긴 뒤 쓸 것, 안 그러면 다른 창에 잘못 입력될 수 있음), " +
        "press_key(키 또는 조합 입력, 예: \"enter\", \"tab\", \"ctrl+a\", \"ctrl+shift+s\").";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "focus_window", "click_element", "type_into_element", "click_at", "type_text", "press_key" }
            },
            ["titleContains"] = new JsonObject { ["type"] = "string", ["description"] = "대상 창 제목에 포함된 부분 문자열" },
            ["nameContains"] = new JsonObject { ["type"] = "string", ["description"] = "대상 UI 요소의 이름/AutomationId에 포함된 부분 문자열" },
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "입력할 텍스트" },
            ["key"] = new JsonObject { ["type"] = "string", ["description"] = "키 또는 조합, 예: \"enter\", \"tab\", \"ctrl+a\"" },
            ["x"] = new JsonObject { ["type"] = "number", ["description"] = "see_screen 이미지에서 본 x 픽셀 좌표 (실제 화면 좌표 아님, 자동 환산됨)" },
            ["y"] = new JsonObject { ["type"] = "number", ["description"] = "see_screen 이미지에서 본 y 픽셀 좌표 (실제 화면 좌표 아님, 자동 환산됨)" },
            ["captureId"] = new JsonObject { ["type"] = "number", ["description"] = "click_at 전용, 필수: 이 x,y를 읽은 see_screen 응답의 capture_id" }
        },
        ["required"] = new JsonArray { "action" }
    };

    public Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var action = input.GetProperty("action").GetString() ?? string.Empty;
        log($"gui_input: {action}");

        try
        {
            switch (action)
            {
                case "focus_window":
                    return Task.FromResult(FocusWindow(input));
                case "click_element":
                    return Task.FromResult(ClickElement(input));
                case "type_into_element":
                    return Task.FromResult(TypeIntoElement(input));
                case "click_at":
                {
                    // x,y는 see_screen이 보낸(리사이즈된) 이미지에서 Claude가 읽은 픽셀 좌표로
                    // 받는다 - ScreenCoordinateMap이 실제 화면 좌표로 환산해준다.
                    // 버그 재발 방지: captureId가 그 사이에 또 호출된 see_screen 때문에
                    // ScreenCoordinateMap.Generation과 달라졌으면(=이 x,y가 가리키는 캡쳐가 더
                    // 이상 "최신"이 아님) 조용히 틀린 곳을 클릭하는 대신 거부한다.
                    // Mouse.Position(즉시 이동)을 쓴다 - Mouse.MoveTo는 사람처럼 커서를
                    // 애니메이션으로 움직여서 매 클릭마다 불필요하게 느려짐.
                    if (!input.TryGetProperty("captureId", out var captureIdProp))
                        return Task.FromResult("거부됨: captureId가 없습니다. 먼저 see_screen을 호출해서 받은 capture_id를 같이 넘기세요.");
                    var captureId = (long)captureIdProp.GetDouble();
                    if (captureId != ScreenCoordinateMap.Generation)
                        return Task.FromResult(
                            $"거부됨: captureId({captureId})가 최신 캡쳐({ScreenCoordinateMap.Generation})와 다릅니다 - " +
                            "그 사이에 see_screen을 또 호출해서 좌표 기준이 바뀌었습니다. see_screen으로 화면을 다시 " +
                            "확인하고 그 응답의 capture_id로 다시 클릭하세요.");

                    var imgX = (int)input.GetProperty("x").GetDouble();
                    var imgY = (int)input.GetProperty("y").GetDouble();
                    var (x, y) = ScreenCoordinateMap.ImageToScreen(imgX, imgY);
                    Mouse.Position = new Point(x, y);
                    Mouse.Click(MouseButton.Left);
                    return Task.FromResult($"클릭함: 이미지 좌표 ({imgX},{imgY}) -> 실제 화면 좌표 ({x},{y})");
                }
                case "type_text":
                {
                    var text = input.GetProperty("text").GetString() ?? string.Empty;
                    Keyboard.Type(text);
                    return Task.FromResult($"입력함: \"{text}\"");
                }
                case "press_key":
                {
                    var key = input.GetProperty("key").GetString() ?? string.Empty;
                    return Task.FromResult(PressKey(key));
                }
                default:
                    return Task.FromResult($"알 수 없는 action: {action}");
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult($"gui_input 오류: {ex.Message}");
        }
    }

    private static string FocusWindow(JsonElement input)
    {
        var titleContains = input.GetProperty("titleContains").GetString() ?? string.Empty;
        var (window, _) = FindWindowAndTarget(titleContains, string.Empty);
        if (window is null) return $"창을 찾지 못했습니다: 제목에 \"{titleContains}\" 포함된 창 없음";
        EnsureForeground(window);
        return $"포커스함: \"{titleContains}\"";
    }

    private static string ClickElement(JsonElement input)
    {
        var titleContains = input.GetProperty("titleContains").GetString() ?? string.Empty;
        var nameContains = input.TryGetProperty("nameContains", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var (window, target) = FindWindowAndTarget(titleContains, nameContains);
        if (window is null) return $"창을 찾지 못했습니다: 제목에 \"{titleContains}\" 포함된 창 없음";
        if (target is null) return $"요소를 찾지 못했습니다: 창=\"{titleContains}\" 요소=\"{nameContains}\"";

        EnsureForeground(window);
        target.Focus();
        // 가능하면 Mouse.Position(즉시 이동)으로 직접 클릭 - element.Click()의 기본 동작은
        // 커서를 애니메이션으로 움직여서 느리다. 클릭 가능한 지점을 못 찾으면 그때만 폴백.
        if (target.TryGetClickablePoint(out var point))
        {
            Mouse.Position = point;
            Mouse.Click(MouseButton.Left);
        }
        else
        {
            target.Click();
        }
        return $"클릭함: 창=\"{titleContains}\" 요소=\"{nameContains}\"";
    }

    private static string TypeIntoElement(JsonElement input)
    {
        var titleContains = input.GetProperty("titleContains").GetString() ?? string.Empty;
        var nameContains = input.TryGetProperty("nameContains", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var text = input.GetProperty("text").GetString() ?? string.Empty;
        var (window, target) = FindWindowAndTarget(titleContains, nameContains);
        if (window is null) return $"창을 찾지 못했습니다: 제목에 \"{titleContains}\" 포함된 창 없음";
        if (target is null) return $"요소를 찾지 못했습니다: 창=\"{titleContains}\" 요소=\"{nameContains}\"";

        EnsureForeground(window);
        target.Focus();
        Keyboard.Type(text);
        return $"입력함: 창=\"{titleContains}\" 요소=\"{nameContains}\" 텍스트=\"{text}\"";
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;

    // click_element/type_into_element/focus_window가 공통으로 쓴다 - 창이 최소화돼
    // 있거나 다른 창에 가려져 있어도(사용자가 지적한 "작업표시줄 클릭" 시나리오) 화면을
    // 보고 타겟을 찾을 필요 없이 바로 맨 앞으로 가져와서, 클릭/입력이 엉뚱한(예: 비서
    // 자신의) 창으로 새는 걸 막는다.
    //
    // SetForegroundWindow 하나만으로는 안 됨: Windows는 "마지막으로 입력을 받은 스레드"가
    // 아닌 프로세스가 포커스를 뺏는 걸 막는다 - 실측으로 SW_RESTORE는 성공(최소화 풀림)해도
    // SetForegroundWindow는 조용히 무시되는 걸 확인함. AttachThreadInput으로 현재 스레드를
    // 지금 포그라운드 스레드에 잠깐 붙여서 그 제약을 우회하는 표준적인 방법을 쓴다.
    private static void EnsureForeground(AutomationElement window)
    {
        if (!window.Properties.NativeWindowHandle.IsSupported) return;
        var hwnd = window.Properties.NativeWindowHandle.Value;
        ShowWindow(hwnd, SW_RESTORE);

        var foregroundHwnd = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
        var currentThread = GetCurrentThreadId();

        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                        AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static (AutomationElement? Window, AutomationElement? Target) FindWindowAndTarget(string titleContains, string nameContains)
    {
        using var automation = new UIA3Automation();
        var window = WindowFinder.FindWindow(automation, titleContains);
        if (window is null) return (null, null);

        var target = string.IsNullOrEmpty(nameContains) ? window : FindDescendant(window, nameContains);
        return (window, target);
    }

    private static AutomationElement? FindDescendant(AutomationElement root, string nameContains)
    {
        AutomationElement[] children;
        try { children = root.FindAllChildren(); } catch { return null; }

        foreach (var child in children)
        {
            var name = SafeGet(() => child.Name) ?? string.Empty;
            var id = SafeGet(() => child.AutomationId) ?? string.Empty;
            if (name.Contains(nameContains, StringComparison.OrdinalIgnoreCase) ||
                id.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                return child;

            var found = FindDescendant(child, nameContains);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try { return getter(); } catch { return null; }
    }

    private static string PressKey(string key)
    {
        var parts = key.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var keys = new VirtualKeyShort[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var mapped = MapKey(parts[i]);
            if (mapped is null) return $"알 수 없는 키: \"{parts[i]}\" (입력: \"{key}\")";
            keys[i] = mapped.Value;
        }

        if (keys.Length == 1)
            Keyboard.Type(keys);
        else
            Keyboard.TypeSimultaneously(keys);
        return $"키 입력함: \"{key}\"";
    }

    private static VirtualKeyShort? MapKey(string token)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control": return VirtualKeyShort.CONTROL;
            case "shift": return VirtualKeyShort.SHIFT;
            case "alt": return VirtualKeyShort.ALT;
            case "win":
            case "windows": return VirtualKeyShort.LWIN;
            case "enter":
            case "return": return VirtualKeyShort.ENTER;
            case "esc":
            case "escape": return VirtualKeyShort.ESCAPE;
            case "tab": return VirtualKeyShort.TAB;
            case "space": return VirtualKeyShort.SPACE;
            case "backspace": return VirtualKeyShort.BACK;
            case "delete":
            case "del": return VirtualKeyShort.DELETE;
            case "home": return VirtualKeyShort.HOME;
            case "end": return VirtualKeyShort.END;
            case "up": return VirtualKeyShort.UP;
            case "down": return VirtualKeyShort.DOWN;
            case "left": return VirtualKeyShort.LEFT;
            case "right": return VirtualKeyShort.RIGHT;
        }

        if (token.Length == 1 && char.IsLetter(token[0]) &&
            Enum.TryParse<VirtualKeyShort>($"KEY_{token.ToUpperInvariant()}", out var letterKey))
            return letterKey;

        if (token.Length == 1 && char.IsDigit(token[0]) &&
            Enum.TryParse<VirtualKeyShort>($"KEY_{token}", out var digitKey))
            return digitKey;

        if (Enum.TryParse<VirtualKeyShort>(token.ToUpperInvariant(), out var named))
            return named;

        return null;
    }
}
