using System.Text;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class WindowLister
{
    public static string ListTopLevelWindows()
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        var sb = new StringBuilder();
        foreach (var w in desktop.FindAllChildren())
        {
            string name;
            try { name = w.Name ?? string.Empty; } catch { name = "(이름 읽기 실패)"; }

            string rect;
            try { rect = w.BoundingRectangle.ToString(); } catch { rect = "?"; }

            sb.AppendLine($"name=\"{name}\" rect={rect}");
        }
        return sb.ToString();
    }
}
