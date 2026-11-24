using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class PolarisPptx
{
    private static readonly Regex SlideCounterPattern = new(@"^슬라이드\s+(\d+)/(\d+)$");

    // Reads the "슬라이드 N/M" status bar button to get (current, total) slide numbers.
    public static (int Current, int Total)? GetSlideCounter(string windowTitleSubstring, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return GetSlideCounterCore(automation, windowTitleSubstring);

        using var owned = new UIA3Automation();
        return GetSlideCounterCore(owned, windowTitleSubstring);
    }

    private static (int Current, int Total)? GetSlideCounterCore(UIA3Automation automation, string windowTitleSubstring)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);

        if (window is null)
            return null;

        // 창 전체를 깊이 제한 없이 훑는 FindAllDescendants(Button)은 실측 9초+
        // 걸렸다 - 슬라이드를 많이 붙여넣을수록 콘텐츠 트리(DocWnd, 축소판 패널
        // 안쪽)가 커져서 더 느려진다. "슬라이드 N/M" 버튼은 상태 표시줄 툴바의
        // 바로 아래 자식이라 - 창의 "얕은" 직계 자식들만 먼저 보고, 그 안에서도
        // 얕은 자식만 보면 콘텐츠 트리를 전혀 건드리지 않아 거의 즉시 끝난다.
        foreach (var child in window.FindAllChildren())
        {
            if (SafeGetControlType(child) != FlaUI.Core.Definitions.ControlType.ToolBar)
                continue;

            AutomationElement[] grandchildren;
            try { grandchildren = child.FindAllChildren(); }
            catch { continue; }

            foreach (var b in grandchildren)
            {
                string name;
                try { name = b.Name ?? string.Empty; } catch { continue; }

                var match = SlideCounterPattern.Match(name);
                if (match.Success)
                {
                    var current = int.Parse(match.Groups[1].Value);
                    var total = int.Parse(match.Groups[2].Value);
                    return (current, total);
                }
            }
        }

        return null;
    }

    private static FlaUI.Core.Definitions.ControlType? SafeGetControlType(AutomationElement element)
    {
        try { return element.ControlType; } catch { return null; }
    }
}
