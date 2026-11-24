using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

// 실측으로 확인된 핵심 병목: desktop.FindAllChildren()(제목으로 창을 찾기 위해
// 모든 헬퍼가 매 호출마다 하던 동작)이 거의 항상 9초 가까이 걸렸다 - 데스크톱에
// 떠 있는 다른 어떤 창(겹치는 오버레이 등)의 UIA 제공자가 열거 자체를 느리게
// 만드는 것으로 보인다(그 창 자체의 속성 읽기는 빠른데 "열거에 포함시키는" 단계가
// 느림). UIA를 거치지 않는 Win32 EnumWindows로 핸들을 먼저 찾고, 그 핸들 하나만
// UIA로 변환하면 - 다른 모든 창을 건드리지 않으므로 이 병목을 완전히 피한다.
public static class WindowFinder
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static IntPtr? FindTopLevelWindowHandle(string titleContains)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().Contains(titleContains, StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false; // 그만 찾기
            }

            return true;
        }, IntPtr.Zero);

        return found == IntPtr.Zero ? null : found;
    }

    public static AutomationElement? FindWindow(UIA3Automation automation, string titleContains)
    {
        var handle = FindTopLevelWindowHandle(titleContains);
        if (handle is null) return null;

        try { return automation.FromHandle(handle.Value); }
        catch { return null; }
    }
}
