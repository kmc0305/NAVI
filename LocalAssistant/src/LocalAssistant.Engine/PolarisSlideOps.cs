using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class PolarisSlideOps
{
    // Selecting an object (e.g. right after a paste or an undo) can make Polaris
    // auto-switch the ribbon to a contextual tab (observed: jumping to "애니메이션").
    // "새 슬라이드"/"붙여넣기" live under the "홈" tab's own ribbon group, so they
    // disappear from the UIA tree whenever a different tab is active. Explicitly
    // reselecting "홈" first makes every Home-tab action reliable regardless of
    // whatever the ribbon last auto-switched to.
    public static bool EnsureHomeTabActive(string windowTitleSubstring)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        var window = desktop.FindAllChildren()
            .FirstOrDefault(e => (e.Name ?? string.Empty)
                .Contains(windowTitleSubstring, StringComparison.OrdinalIgnoreCase));
        if (window is null) return false;

        var homeTab = window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem)
            .And(cf.ByName("홈")));
        if (homeTab is null) return false;

        // SelectionItem.Select() reports success (no exception) on this control even
        // when it doesn't actually switch the ribbon - a known symptom of a shallow
        // UIA provider wrapping a custom-drawn control. Try every mechanism this
        // control might respond to, then verify the ONLY way that actually matters:
        // whether "새 슬라이드" is now actually present in the tree.
        //
        // Deliberately UIA-only here - no element.Click() fallback. Click() moves the
        // real OS mouse cursor and fires a real click at screen coordinates, which (a)
        // depends on Z-order/focus like the old CopyFromScreen bugs, and (b) a real
        // input event landing on another top-level window is exactly what makes
        // Windows kick an exclusive-fullscreen game out to the desktop.
        if (homeTab.Patterns.SelectionItem.IsSupported)
            TrySafely(() => homeTab.Patterns.SelectionItem.Pattern.Select());

        if (!HomeTabIsActive(window) && homeTab.Patterns.Invoke.IsSupported)
            TrySafely(() => homeTab.Patterns.Invoke.Pattern.Invoke());

        if (!HomeTabIsActive(window) && homeTab.Patterns.LegacyIAccessible.IsSupported)
            TrySafely(() => homeTab.Patterns.LegacyIAccessible.Pattern.DoDefaultAction());

        return HomeTabIsActive(window);
    }

    private static bool HomeTabIsActive(AutomationElement window)
    {
        Thread.Sleep(200);
        return window.FindFirstDescendant(cf => cf.ByName("새 슬라이드")) is not null;
    }

    private static void TrySafely(Action action)
    {
        try { action(); } catch { /* fall through to the next mechanism */ }
    }

    // Clicks "새 슬라이드" until the slide count reaches targetCount. Never removes
    // slides - only ever used to grow the deck, so it stays safe to call repeatedly.
    public static bool EnsureSlideCountAtLeast(string windowTitleSubstring, int targetCount)
    {
        for (var i = 0; i < targetCount; i++)
        {
            var counter = PolarisPptx.GetSlideCounter(windowTitleSubstring);
            if (counter is null) return false;
            if (counter.Value.Total >= targetCount) return true;

            if (!EnsureHomeTabActive(windowTitleSubstring))
                return false;

            if (!ButtonClicker.ClickButtonByName(windowTitleSubstring, "새 슬라이드"))
                return false;

            Thread.Sleep(300);
        }

        var final = PolarisPptx.GetSlideCounter(windowTitleSubstring);
        return final is not null && final.Value.Total >= targetCount;
    }

    public static Bitmap? CaptureCurrentSlideArea(string windowTitleSubstring)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        var window = desktop.FindAllChildren()
            .FirstOrDefault(e => (e.Name ?? string.Empty)
                .Contains(windowTitleSubstring, StringComparison.OrdinalIgnoreCase));
        if (window is null) return null;
        if (!window.Properties.NativeWindowHandle.IsSupported) return null;

        var windowRect = window.BoundingRectangle;

        var docWnd = window.FindAllDescendants()
            .FirstOrDefault(e =>
            {
                try { return e.Name == "DocWnd"; } catch { return false; }
            });
        if (docWnd is null) return null;

        var elementRect = docWnd.BoundingRectangle;
        if (elementRect.Width <= 0 || elementRect.Height <= 0) return null;

        using var windowBitmap = WindowPrintCapture.CaptureWindow(
            window.Properties.NativeWindowHandle.Value, windowRect.Width, windowRect.Height);
        if (windowBitmap is null) return null;

        var cropRect = new Rectangle(
            elementRect.Left - windowRect.Left,
            elementRect.Top - windowRect.Top,
            elementRect.Width,
            elementRect.Height);
        cropRect.Intersect(new Rectangle(0, 0, windowBitmap.Width, windowBitmap.Height));
        if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

        return windowBitmap.Clone(cropRect, windowBitmap.PixelFormat);
    }
}
