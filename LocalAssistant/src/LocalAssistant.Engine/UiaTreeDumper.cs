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
        var window = WindowFinder.FindWindow(automation, titleContains);

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
        var patterns = SafeGet(() => string.Join(",", element.GetSupportedPatterns().Select(p => p.Name))) ?? "";

        // 카카오톡처럼 최신 UIA Name/AutomationId는 비어있어도 LegacyIAccessible(MSAA)
        // 쪽에는 실제 이름/역할이 남아있는 구형 Win32 앱이 있다 - 지원되면 같이 찍는다.
        var legacy = "";
        if (element.Patterns.LegacyIAccessible.IsSupported)
        {
            var li = element.Patterns.LegacyIAccessible.Pattern;
            var legacyName = SafeGet(() => li.Name.ValueOrDefault) ?? "";
            var legacyRole = SafeGet(() => li.Role.ValueOrDefault.ToString()) ?? "";
            var legacyValue = SafeGet(() => li.Value.ValueOrDefault) ?? "";
            legacy = $" legacy=[name=\"{legacyName}\" role=\"{legacyRole}\" value=\"{legacyValue}\"]";
        }

        sb.AppendLine($"{indent}[{controlType}] name=\"{name}\" id=\"{automationId}\" rect={rect} patterns=[{patterns}]{legacy}");

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
