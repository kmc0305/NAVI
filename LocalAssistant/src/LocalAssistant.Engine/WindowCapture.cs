using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class WindowCapture
{
    // Uses PrintWindow (via WindowPrintCapture) rather than Graphics.CopyFromScreen,
    // which only sees whatever is visually on top on screen - occluded by another
    // window (or a fullscreen game), it silently captures the wrong thing instead.
    public static bool CaptureWindowByTitle(string titleContains, string outputPath)
    {
        using var automation = new UIA3Automation();
        var window = WindowFinder.FindWindow(automation, titleContains);

        if (window is null || !window.Properties.NativeWindowHandle.IsSupported)
            return false;

        var rect = window.BoundingRectangle;
        using var bitmap = WindowPrintCapture.CaptureWindow(
            window.Properties.NativeWindowHandle.Value, rect.Width, rect.Height);
        if (bitmap is null)
            return false;

        bitmap.Save(outputPath, ImageFormat.Png);
        return true;
    }
}
