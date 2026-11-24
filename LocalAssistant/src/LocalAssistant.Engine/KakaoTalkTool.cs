using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

// 카카오톡에서 특정 채팅방으로 파일을 전송하는 결정적 자동화 - 2026-11-24에 UIA/MSAA로
// 직접 조사하고 실제 전송까지 검증한 절차를 그대로 코드로 고정한 것:
//   1. 메인 "카카오톡" 창 검색창(Edit id=100)에 실제 키 입력으로 검색어를 타이핑하고,
//      검색 결과 한 줄을 상대좌표로 더블클릭해서 채팅방을 연다.
//   2. 채팅방 하단 3번째(문서 모양) 아이콘을 클릭 - 표준 Windows "열기" 파일 선택
//      대화상자가 뜬다("+"는 캡처/메시지 예약 같은 무관한 메뉴이니 쓰지 않는다).
//   3. 그 대화상자의 파일이름 콤보박스(id=1148)에 실제 키 입력으로 전체 경로를 타이핑한
//      뒤(ValuePattern.SetValue는 "진짜 선택"으로 인식되지 않아 반드시 Keyboard.Type을
//      써야 함) "열기(O)" 버튼(id=1)을 Invoke한다.
//   4. 그러면 채팅방 위에 "파일 전송" 확인 카드(파일명/용량 + "N개 전송" 버튼)가 뜬다 -
//      이 카드는 채팅방 창의 자식이 아니라 별도 레이어로 떠서 UIA/PrintWindow로 못 찾기
//      때문에, 채팅방 창 기준 상대좌표를 클릭해서 그 버튼을 누른다.
// 검색 결과 행과 확인 카드 버튼 둘 다 UIA/MSAA에 전혀 노출되지 않는 완전 커스텀 드로잉이라
// 상대좌표 클릭에 의존한다 - 카카오톡 UI가 바뀌거나 창 크기가 크게 달라지면 깨질 수 있다.
public class KakaoTalkTool : ITool
{
    public string Name => "kakao_send_file";
    public string Description =>
        "카카오톡에서 searchName으로 검색해 채팅방을 연 뒤 그 채팅방으로 filePath의 파일을 " +
        "전송한다. '나에게 보내기'(자기 자신과의 채팅방)로 보낼 때는 searchName에 본인 " +
        "카카오톡 프로필 이름을 넣는다. 카카오톡이 이미 실행 중이어야 하고, 도중에 다른 " +
        "창으로 포커스를 뺏으면 실패할 수 있다.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["searchName"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "카카오톡 검색창에 입력할 이름/검색어 (채팅방을 찾기 위함)",
            },
            ["filePath"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "전송할 파일의 전체 경로",
            },
        },
        ["required"] = new JsonArray { "searchName", "filePath" },
    };

    public Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var searchName = input.GetProperty("searchName").GetString() ?? string.Empty;
        var filePath = input.GetProperty("filePath").GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(searchName) || string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult("searchName과 filePath가 모두 필요합니다.");
        if (!File.Exists(filePath))
            return Task.FromResult($"파일을 찾을 수 없습니다: {filePath}");

        try
        {
            return Task.FromResult(SendFile(searchName, filePath, log));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"카카오톡 전송 중 오류: {ex.Message}");
        }
    }

    private static string SendFile(string searchName, string filePath, Action<string> log)
    {
        using var automation = new UIA3Automation();

        log("카카오톡 메인 창 찾는 중");
        var mainWindow = WindowFinder.FindWindow(automation, "카카오톡");
        if (mainWindow is null)
            return "카카오톡 메인 창을 찾지 못했습니다 (실행 중인지 확인 필요).";

        var searchBox = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("100"));
        if (searchBox is null)
            return "카카오톡 검색창(id=100)을 찾지 못했습니다 - UI가 바뀌었을 수 있습니다.";

        ForceForeground(mainWindow.Properties.NativeWindowHandle.Value);
        searchBox.Focus();
        Thread.Sleep(150);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Thread.Sleep(150);
        Keyboard.Type(searchName);
        log($"검색창에 '{searchName}' 입력함");
        Thread.Sleep(800);

        // 검색 결과 행은 UIA/MSAA 어디에도 자식 요소로 노출되지 않는 완전 커스텀
        // 드로잉이다 - 검색창 바로 아래, 결과가 1개일 때 기준 상대좌표를 더블클릭한다.
        var mainRect = mainWindow.BoundingRectangle;
        Mouse.Position = new Point(mainRect.X + 200, mainRect.Y + 190);
        Thread.Sleep(150);
        Mouse.DoubleClick(MouseButton.Left);
        log("검색 결과 더블클릭함");
        Thread.Sleep(700);

        var chatWindow = WindowFinder.FindWindow(automation, searchName);
        if (chatWindow is null)
            return $"'{searchName}' 채팅방 창을 찾지 못했습니다 - 검색 결과가 없거나 이름이 달랐을 수 있습니다.";

        ForceForeground(chatWindow.Properties.NativeWindowHandle.Value);
        var chatRect = chatWindow.BoundingRectangle;

        // 하단 아이콘 3개(+, 이모티콘, 파일) 중 3번째 - 표준 "열기" 파일 선택 대화상자를
        // 띄운다. 이 상대좌표는 채팅방 창이 기본 크기(약 380x640)일 때 기준이다.
        Mouse.Position = new Point(chatRect.X + 87, chatRect.Y + 613);
        Thread.Sleep(200);
        Mouse.Click(MouseButton.Left);
        log("파일첨부 아이콘(3번째) 클릭함");
        Thread.Sleep(1000);

        var dialog = WindowFinder.FindWindow(automation, "열기");
        if (dialog is null)
            return "'열기' 파일 선택 대화상자가 뜨지 않았습니다 - 카카오톡 UI가 바뀌었을 수 있습니다.";

        var fileNameBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1148").And(cf.ByControlType(ControlType.Edit)));
        if (fileNameBox is null)
            return "파일이름 입력란을 찾지 못했습니다.";

        // ValuePattern.SetValue는 대화상자가 닫히긴 해도 "진짜 파일을 선택한 것"으로
        // commdlg가 내부적으로 인식하지 않는다(실측 확인된 함정) - 반드시 실제 키 입력.
        fileNameBox.Focus();
        Thread.Sleep(150);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Thread.Sleep(150);
        Keyboard.Type(filePath);
        log($"파일이름란에 '{filePath}' 입력함");
        Thread.Sleep(300);

        var openButton = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.SplitButton)));
        if (openButton is null || !openButton.Patterns.Invoke.IsSupported)
            return "'열기' 버튼을 찾지 못했습니다.";
        openButton.Patterns.Invoke.Pattern.Invoke();
        log("'열기' 버튼 눌렀음");
        Thread.Sleep(1500);

        // 마지막 단계("파일 전송" 확인 카드의 "N개 전송" 버튼)는 일부러 여기서 자동
        // 클릭하지 않는다. 이 카드는 채팅방 창의 자식이 아니라 별도 레이어로 떠서
        // UIA/PrintWindow로 못 찾고, 뜨는 위치/타이밍이 미묘하게 달라서(실측: 여러 번
        // 시도했지만 고정 좌표 클릭이 가끔 엉뚱한 곳을 침) 결정적 좌표 클릭이
        // 신뢰할 수 없었다. 여기까지(검색->채팅방 열기->파일 대화상자->경로 입력->
        // 열기)는 결정적으로 확실히 되니, 마지막 확인 클릭만 호출한 쪽(Claude)이
        // see_screen으로 화면을 보고 "N개 전송" 버튼을 click_at으로 직접 누르게 한다.
        return $"'{searchName}' 채팅방을 열고 '{filePath}'로 파일 선택까지 마쳤습니다. " +
            "이제 \"파일 전송\"이라는 확인 카드(파일명/용량과 \"N개 전송\" 버튼)가 화면에 " +
            "떠 있을 것이다 - see_screen으로 화면 전체(또는 그 채팅방 창)를 확인하고, " +
            "\"N개 전송\" 버튼을 click_at으로 직접 눌러서 전송을 마무리해라.";
    }

    // GuiInputTool.EnsureForeground와 같은 이유 - 단순 SetForegroundWindow는 "마지막
    // 입력을 받은 스레드"가 아닌 프로세스에서 부르면 조용히 무시된다.
    private static void ForceForeground(IntPtr hwnd)
    {
        NativeMethods.ShowWindow(hwnd, 9);
        var fgHwnd = NativeMethods.GetForegroundWindow();
        var fgThread = NativeMethods.GetWindowThreadProcessId(fgHwnd, out _);
        var curThread = NativeMethods.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != curThread && NativeMethods.AttachThreadInput(curThread, fgThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(curThread, fgThread, false);
        }
        Thread.Sleep(200);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}
