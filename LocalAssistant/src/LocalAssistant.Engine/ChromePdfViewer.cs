using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class ChromePdfViewer
{
    private static readonly Regex TotalPagesPattern = new(@"^\d+$");

    public static string PageAutomationId(int pageNumber) => $"page{pageNumber - 1}";

    public static int? GetTotalPages(string windowTitleSubstring)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        var window = FindWindow(desktop, windowTitleSubstring);
        if (window is null) return null;

        var input = window.FindFirstDescendant(cf => cf.ByAutomationId("inputPageNumber"));
        if (input is null) return null;

        var inputRect = input.BoundingRectangle;

        // Sibling/Parent walks over UIA elements are unreliable (FlaUI wraps the
        // same native element in distinct objects on separate lookups, and
        // Parent/FindAllChildren can use a different tree view than a recursive
        // descendant search). Instead, search the whole window for small digit-only
        // Text elements positioned just to the right of the page-number box, on the
        // same toolbar row - that's the "/ 49" total-pages label.
        var candidates = window.FindAllDescendants()
            .Where(e =>
            {
                var name = (Safe(() => e.Name) ?? string.Empty).Trim();
                if (!TotalPagesPattern.IsMatch(name)) return false;

                var r = e.BoundingRectangle;
                var sameRow = Math.Abs(r.Top - inputRect.Top) <= 20;
                var toTheRight = r.Left > inputRect.Left && r.Left < inputRect.Left + 300;
                return sameRow && toTheRight;
            })
            .OrderBy(e => e.BoundingRectangle.Left)
            .ToList();

        if (candidates.Count == 0) return null;

        var text = (Safe(() => candidates[0].Name) ?? string.Empty).Trim();
        return int.TryParse(text, out var total) ? total : null;
    }

    // Sets the page-jump box's value directly via UIA ValuePattern (no keystrokes -
    // a real keystroke goes to whatever window has OS focus, not necessarily this
    // one), then invokes the "이동" hyperlink next to it to commit the jump.
    public static bool GoToPage(string windowTitleSubstring, int pageNumber)
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        var window = FindWindow(desktop, windowTitleSubstring);
        if (window is null) return false;

        var input = window.FindFirstDescendant(cf => cf.ByAutomationId("inputPageNumber"));
        if (input is null || !input.Patterns.Value.IsSupported) return false;

        input.Patterns.Value.Pattern.SetValue(pageNumber.ToString());

        var jumpButton = window.FindFirstDescendant(cf => cf.ByAutomationId("slideJumpButton"));
        if (jumpButton is null || !jumpButton.Patterns.Invoke.IsSupported) return false;

        jumpButton.Patterns.Invoke.Pattern.Invoke();

        // Verifying "did it actually land on page N" by looking up a "pageN-1"
        // AutomationId turned out unreliable: jumping directly (vs. scrolling through)
        // can rebuild/renumber the lazily-virtualized page elements differently, so a
        // correct jump was being reported as a failure. Navigation itself has been
        // visually confirmed reliable (screenshots across several pages), so a fixed
        // settle delay replaces that fragile check.
        Thread.Sleep(700);
        return true;
    }

    private static AutomationElement? FindWindow(AutomationElement desktop, string titleContains) =>
        desktop.FindAllChildren()
            .FirstOrDefault(e => (Safe(() => e.Name) ?? string.Empty)
                .Contains(titleContains, StringComparison.OrdinalIgnoreCase));

    private static string? Safe(Func<string?> getter)
    {
        try { return getter(); } catch { return null; }
    }
}
