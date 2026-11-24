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
    // automation: 넘겨주면 그 세션을 재사용하고(호출자가 Dispose 책임), null이면
    // 이 호출 하나만을 위한 세션을 새로 만들고 바로 버린다. 한 작업 안에서
    // 반복 호출될 때마다 매번 새 COM 세션을 만들고 버리면 느릴 뿐 아니라, 세션이
    // 빠르게 생성/폐기되는 동안 다른 호출이 끼어들면 0x80040201 재진입 예외가
    // 나는 것도 실측으로 확인됐다 - 가능하면 호출자가 세션을 재사용해야 한다.
    public static bool ClickButtonByName(string windowTitleSubstring, string buttonName, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return ClickButtonByNameCore(automation, windowTitleSubstring, buttonName);

        using var owned = new UIA3Automation();
        return ClickButtonByNameCore(owned, windowTitleSubstring, buttonName);
    }

    private static bool ClickButtonByNameCore(UIA3Automation automation, string windowTitleSubstring, string buttonName)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);

        if (window is null)
            return false;

        // 컨트롤 종류 조건과 이름 조건을 하나로 합쳐 FindFirstDescendant 한 번만
        // 호출한다 - 이름 비교를 C#에서 따로 하면 모든 후보를 먼저 다 모아야
        // 하지만, 조건을 합치면 UIA 자체가 네이티브로 탐색하고 첫 매치에서 멈춘다.
        var target = window.FindFirstDescendant(cf =>
            (cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)
                .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.SplitButton))
                .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Hyperlink))
                .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem)))
            .And(cf.ByName(buttonName)));

        if (target is null || !target.Patterns.Invoke.IsSupported)
            return false;

        target.Patterns.Invoke.Pattern.Invoke();
        // UIA COM calls occasionally throw a transient reentrancy error
        // (0x80040201) if queried again immediately after an Invoke - a short
        // settle delay avoids that without needing retry logic at every call site.
        Thread.Sleep(60);
        return true;
    }
}
