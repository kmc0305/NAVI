using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class PolarisPptx
{
    private static readonly Regex SlideCounterPattern = new(@"^슬라이드\s+(\d+)/(\d+)$");

    // Reads the "슬라이드 N/M" status bar button to get (current, total) slide numbers.
    public static (int Current, int Total)? GetSlideCounter(string windowTitleSubstring)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        var window = desktop.FindAllChildren()
            .FirstOrDefault(e => (e.Name ?? string.Empty)
                .Contains(windowTitleSubstring, StringComparison.OrdinalIgnoreCase));

        if (window is null)
            return null;

        var buttons = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));

        foreach (var b in buttons)
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

        return null;
    }
}
