using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class UiaTreeDumper
{
    public static string? DumpWindowByTitle(string titleContains, int maxDepth = 6)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        var window = desktop.FindAllChildren()
            .FirstOrDefault(e => (e.Name ?? string.Empty)
                .Contains(titleContains, StringComparison.OrdinalIgnoreCase));

        if (window is null)
            return null;

        var sb = new StringBuilder();
        AppendElement(sb, window, 0, maxDepth);
        return sb.ToString();
    }

    private static void AppendElement(StringBuilder sb, AutomationElement element, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);
        var name = SafeGet(() => element.Name) ?? string.Empty;
        var automationId = SafeGet(() => element.AutomationId) ?? string.Empty;
        var controlType = SafeGet(() => element.ControlType.ToString()) ?? "?";
        var rect = SafeGet(() => element.BoundingRectangle.ToString()) ?? "?";

        sb.AppendLine($"{indent}[{controlType}] name=\"{name}\" id=\"{automationId}\" rect={rect}");

        if (depth >= maxDepth)
            return;

        AutomationElement[] children;
        try
        {
            children = element.FindAllChildren();
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            AppendElement(sb, child, depth + 1, maxDepth);
        }
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }
}
