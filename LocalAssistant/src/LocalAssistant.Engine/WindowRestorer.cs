using System.Runtime.InteropServices;

namespace LocalAssistant.Engine;

// 최소화된 창을 제목 일부로 찾아 복원하고 전경으로 가져온다.
public static class WindowRestorer
{
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static bool RestoreAndForeground(string titleContains) =>
        WithWindowHandle(titleContains, handle =>
        {
            ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
        });

    // 브라우저 창이 작아지면 PDF 페이지의 렌더링 너비/높이가 뷰포트보다 커져서
    // 내부 스크롤이 생기고, 캡쳐는 그 스크롤된 부분만 가져와 아래/오른쪽이
    // 잘린다(실측) - 작업 전에 창을 최대화해 이 여지를 없앤다.
    public static bool MaximizeAndForeground(string titleContains) =>
        WithWindowHandle(titleContains, handle =>
        {
            ShowWindow(handle, SW_MAXIMIZE);
            SetForegroundWindow(handle);
        });

    private static bool WithWindowHandle(string titleContains, Action<IntPtr> action)
    {
        var handle = WindowFinder.FindTopLevelWindowHandle(titleContains);
        if (handle is null) return false;

        action(handle.Value);
        return true;
    }
}
