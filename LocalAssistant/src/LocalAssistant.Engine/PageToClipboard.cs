namespace LocalAssistant.Engine;

public static class PageToClipboard
{
    // Finds a descendant element by AutomationId inside the given window, screenshots
    // exactly its bounding rect (not the whole window), and puts that image on the
    // clipboard.
    public static bool CaptureElementToClipboard(string windowTitleSubstring, string automationId)
    {
        using var bitmap = ChromeElementCapture.CaptureElementBitmap(windowTitleSubstring, automationId);
        if (bitmap is null)
            return false;

        ClipboardImage.SetImage(bitmap);
        return true;
    }

    // Clicks the "붙여넣기" (Paste) toolbar button via UIA Invoke instead of sending a
    // real Ctrl+V keystroke. A real keystroke goes to whatever window actually has OS
    // keyboard focus - if a fullscreen game (or anything else) holds focus instead of
    // Polaris, the keystroke goes there, not here. Invoke bypasses that entirely.
    public static bool PasteInto(string windowTitleSubstring) =>
        PolarisSlideOps.EnsureHomeTabActive(windowTitleSubstring) &&
        ButtonClicker.ClickButtonByName(windowTitleSubstring, "붙여넣기");
}
