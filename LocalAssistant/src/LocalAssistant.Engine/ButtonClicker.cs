using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class ButtonClicker
{
    // Finds a Button/SplitButton descendant whose Name equals buttonName exactly and
    // invokes it via the Invoke pattern - a direct COM call into the target app, not
    // a simulated mouse click, so it works regardless of window focus/foreground
    // state. Deliberately has no coordinate-click fallback: that would depend on
    // screen position/focus and silently reintroduce the problem this exists to avoid.
    public static bool ClickButtonByName(string windowTitleSubstring, string buttonName)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        var window = desktop.FindAllChildren()
            .FirstOrDefault(e => (e.Name ?? string.Empty)
                .Contains(windowTitleSubstring, StringComparison.OrdinalIgnoreCase));

        if (window is null)
            return false;

        var candidates = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)
            .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.SplitButton))
            .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Hyperlink)));

        var target = candidates.FirstOrDefault(b =>
        {
            try { return b.Name == buttonName; } catch { return false; }
        });

        if (target is null || !target.Patterns.Invoke.IsSupported)
            return false;

        target.Patterns.Invoke.Pattern.Invoke();
        // UIA COM calls occasionally throw a transient reentrancy error
        // (0x80040201) if queried again immediately after an Invoke - a short
        // settle delay avoids that without needing retry logic at every call site.
        Thread.Sleep(250);
        return true;
    }
}
