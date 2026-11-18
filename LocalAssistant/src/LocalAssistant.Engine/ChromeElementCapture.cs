using System.Drawing;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class ChromeElementCapture
{
    // Finds a descendant by AutomationId inside the given window, captures the whole
    // window's own content via PrintWindow (works regardless of focus/occlusion - a
    // fullscreen game covering the screen does not block this), then crops to that
    // element's rect relative to the window's own origin.
    public static Bitmap? CaptureElementBitmap(string windowTitleSubstring, string automationId)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        var window = desktop.FindAllChildren()
            .FirstOrDefault(e => (e.Name ?? string.Empty)
                .Contains(windowTitleSubstring, StringComparison.OrdinalIgnoreCase));
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
        var cropRect = new Rectangle(cropX, cropY, elementRect.Width, elementRect.Height);

        // Clamp to the captured bitmap's bounds in case the element pokes slightly
        // outside the window's own reported rect.
        cropRect.Intersect(new Rectangle(0, 0, windowBitmap.Width, windowBitmap.Height));
        if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

        return windowBitmap.Clone(cropRect, windowBitmap.PixelFormat);
    }
}
