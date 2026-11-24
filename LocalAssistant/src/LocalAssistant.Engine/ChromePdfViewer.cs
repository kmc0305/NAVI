using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class ChromePdfViewer
{
    private static readonly Regex TotalPagesPattern = new(@"^\d+$");

    public static string PageAutomationId(int pageNumber) => $"page{pageNumber - 1}";

    public static int? GetTotalPages(string windowTitleSubstring, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return GetTotalPagesCore(automation, windowTitleSubstring);

        using var owned = new UIA3Automation();
        return GetTotalPagesCore(owned, windowTitleSubstring);
    }

    private static int? GetTotalPagesCore(UIA3Automation automation, string windowTitleSubstring)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);
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
    public static bool GoToPage(string windowTitleSubstring, int pageNumber, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return GoToPageCore(automation, windowTitleSubstring, pageNumber);

        using var owned = new UIA3Automation();
        return GoToPageCore(owned, windowTitleSubstring, pageNumber);
    }

    private static bool GoToPageCore(UIA3Automation automation, string windowTitleSubstring, int pageNumber)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);
        if (window is null) return false;

        // 실측으로 확인된 버그: 이동 시도(SetValue+Invoke)가 예외 없이 "성공"을
        // 반환해도 실제로는 페이지가 안 바뀔 때가 있었다(86쪽 작업 중 35, 36쪽이
        // 34쪽 내용으로 중복된 사건 - 조용히 실패한 이동 뒤에 이전 페이지를 다시
        // 캡쳐해서 붙여넣음). "pageN-1" AutomationId로 확인하는 건 가상화 때문에
        // 불안정하다고 알려져 있었지만(옛 주석), 입력창 자신의 현재 값을 다시
        // 읽어서 우리가 설정한 쪽수와 일치하는지 보는 건 안정적이다 - 맞을 때까지
        // 몇 번 더 시도한다.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var input = window.FindFirstDescendant(cf => cf.ByAutomationId("inputPageNumber"));
            if (input is null || !input.Patterns.Value.IsSupported) return false;

            input.Patterns.Value.Pattern.SetValue(pageNumber.ToString());

            var jumpButton = window.FindFirstDescendant(cf => cf.ByAutomationId("slideJumpButton"));
            if (jumpButton is null || !jumpButton.Patterns.Invoke.IsSupported) return false;

            jumpButton.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(200);

            var confirmInput = window.FindFirstDescendant(cf => cf.ByAutomationId("inputPageNumber"));
            var confirmedValue = Safe(() => confirmInput?.Patterns.Value.Pattern.Value.Value);
            if (confirmedValue == pageNumber.ToString())
                return true;
        }

        return false;
    }

    private static string? Safe(Func<string?> getter)
    {
        try { return getter(); } catch { return null; }
    }
}
