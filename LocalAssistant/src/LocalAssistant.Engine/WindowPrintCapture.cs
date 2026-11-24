using System.Drawing;
using System.Runtime.InteropServices;

namespace LocalAssistant.Engine;

// Captures a window's own content directly via PrintWindow, instead of grabbing
// screen pixels with Graphics.CopyFromScreen. This works even when the window is
// occluded, minimized-to-visible, or not the foreground window (e.g. a fullscreen
// game is covering it) - CopyFromScreen cannot see it in that case, but PrintWindow
// asks the window itself to render into our bitmap.
public static class WindowPrintCapture
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public static Bitmap? CaptureWindow(IntPtr hWnd, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            var ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
            if (!ok)
            {
                bitmap.Dispose();
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return bitmap;
    }
}
