using System.Drawing;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class ChromeElementCapture
{
    // Finds a descendant by AutomationId inside the given window, captures the whole
    // window's own content via PrintWindow (works regardless of focus/occlusion - a
    // fullscreen game covering the screen does not block this), then crops to that
    // element's rect relative to the window's own origin.
    public static Bitmap? CaptureElementBitmap(string windowTitleSubstring, string automationId, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return CaptureElementBitmapCore(automation, windowTitleSubstring, automationId);

        using var owned = new UIA3Automation();
        return CaptureElementBitmapCore(owned, windowTitleSubstring, automationId);
    }

    private static Bitmap? CaptureElementBitmapCore(UIA3Automation automation, string windowTitleSubstring, string automationId)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);
        if (window is null) return null;
        if (!window.Properties.NativeWindowHandle.IsSupported) return null;

        var windowRect = window.BoundingRectangle;

        var target = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (target is null) return null;

        var elementRect = target.BoundingRectangle;
        if (elementRect.Width <= 0 || elementRect.Height <= 0) return null;

        using var windowBitmap = WindowPrintCapture.CaptureWindow(
            window.Properties.NativeWindowHandle.Value, windowRect.Width, windowRect.Height);
        if (windowBitmap is null) return null;

        var cropX = elementRect.Left - windowRect.Left;
        var cropY = elementRect.Top - windowRect.Top;
        var fullCropRect = new Rectangle(cropX, cropY, elementRect.Width, elementRect.Height);

        var cropRect = fullCropRect;
        cropRect.Intersect(new Rectangle(0, 0, windowBitmap.Width, windowBitmap.Height));
        if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

        // 실측으로 확인된 버그: 브라우저 창이 작아지면 이 페이지 요소의 렌더링
        // 너비/높이가 창의 실제 보이는 영역보다 커져서(내부 스크롤 발생) 창 경계로
        // 잘린 이미지가 "성공"으로 조용히 반환됐다 - 잘리면 그냥 실패로 처리해서
        // (일시적 재시도로 집계) 창을 키우거나 상황이 바뀔 때까지 재시도하게 한다.
        if (cropRect != fullCropRect) return null;

        return windowBitmap.Clone(cropRect, windowBitmap.PixelFormat);
    }
}
