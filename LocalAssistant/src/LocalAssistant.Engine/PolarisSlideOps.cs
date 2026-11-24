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
    public static bool EnsureHomeTabActive(string windowTitleSubstring, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return EnsureHomeTabActiveCore(automation, windowTitleSubstring);

        using var owned = new UIA3Automation();
        return EnsureHomeTabActiveCore(owned, windowTitleSubstring);
    }

    private static bool EnsureHomeTabActiveCore(UIA3Automation automation, string windowTitleSubstring)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);
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
        Thread.Sleep(60);
        return window.FindFirstDescendant(cf => cf.ByName("새 슬라이드")) is not null;
    }

    private static void TrySafely(Action action)
    {
        try { action(); } catch { /* fall through to the next mechanism */ }
    }

    private static FlaUI.Core.Definitions.ControlType? SafeGetControlType(AutomationElement element)
    {
        try { return element.ControlType; } catch { return null; }
    }

    // Clicks "새 슬라이드" until the slide count reaches targetCount. Never removes
    // slides - only ever used to grow the deck, so it stays safe to call repeatedly.
    public static bool EnsureSlideCountAtLeast(string windowTitleSubstring, int targetCount, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return EnsureSlideCountAtLeastCore(automation, windowTitleSubstring, targetCount);

        using var owned = new UIA3Automation();
        return EnsureSlideCountAtLeastCore(owned, windowTitleSubstring, targetCount);
    }

    private static bool EnsureSlideCountAtLeastCore(UIA3Automation automation, string windowTitleSubstring, int targetCount)
    {
        for (var i = 0; i < targetCount; i++)
        {
            var counter = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
            if (counter is null) return false;
            // 슬라이드 수는 이미 충분하지만(과거 시도에서 미리 만들어진 슬라이드가
            // 있을 수 있음) "현재 슬라이드"가 targetCount가 아닐 수 있다 - 개수만
            // 보고 끝내면 다음 캡쳐/붙여넣기가 엉뚱한(이전에 보고 있던) 슬라이드에
            // 일어난다(실측: 33쪽 이후 전부 같은 슬라이드를 다시 보는 버그로 이어짐).
            if (counter.Value.Total >= targetCount)
                return SelectSlide(windowTitleSubstring, targetCount, automation);

            if (!EnsureHomeTabActive(windowTitleSubstring, automation))
                return false;

            if (!ButtonClicker.ClickButtonByName(windowTitleSubstring, "새 슬라이드", automation))
                return false;

            Thread.Sleep(80);
        }

        var final = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
        if (final is null || final.Value.Total < targetCount) return false;
        return SelectSlide(windowTitleSubstring, targetCount, automation);
    }

    // 사용자가 슬라이드 몇 장을 지우는 등 Total이 재개 지점보다 커진 상태를
    // 되돌린다(실측: 35,36쪽을 지운 뒤 Total=84, 재개 목표=35 - SelectSlide가
    // 안 먹히는 이 상황에 또 부딪힘). "되돌리기"는 확실히 작동하는 버튼이라
    // (ButtonClicker로 계속 성공 확인됨), 목표 장수에 도달할 때까지 반복
    // 클릭한다 - 슬라이드 생성/붙여넣기 작업을 역순으로 취소해 나가는 것과
    // 같다. 매번 실제 장수를 다시 확인하므로 몇 번 눌러야 하는지 몰라도 안전.
    public static bool TrimToCount(string windowTitleSubstring, int targetTotal, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return TrimToCountCore(automation, windowTitleSubstring, targetTotal);

        using var owned = new UIA3Automation();
        return TrimToCountCore(owned, windowTitleSubstring, targetTotal);
    }

    private static bool TrimToCountCore(UIA3Automation automation, string windowTitleSubstring, int targetTotal)
    {
        var counter = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
        if (counter is null) return false;

        var maxClicks = (counter.Value.Total - targetTotal) * 5 + 20; // 여유 있게
        for (var i = 0; i < maxClicks; i++)
        {
            counter = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
            if (counter is null) return false;
            if (counter.Value.Total == targetTotal) return true;
            if (counter.Value.Total < targetTotal) return false; // 너무 많이 되돌림 - 위험하니 중단

            if (!ButtonClicker.ClickButtonByName(windowTitleSubstring, "되돌리기", automation))
                return false;
            Thread.Sleep(150);
        }

        counter = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
        return counter is not null && counter.Value.Total == targetTotal;
    }

    // 왼쪽 슬라이드 축소판 패널에서 slideNumber번째 항목을 선택해 "현재 슬라이드"로
    // 만든다. 이미 그 슬라이드가 현재 상태면 아무것도 하지 않는다(새로 만든 슬라이드는
    // 보통 자동으로 선택되므로 이 경우가 흔함 - 매번 다시 선택해도 안전).
    public static bool SelectSlide(string windowTitleSubstring, int slideNumber, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return SelectSlideCore(automation, windowTitleSubstring, slideNumber);

        using var owned = new UIA3Automation();
        return SelectSlideCore(owned, windowTitleSubstring, slideNumber);
    }

    private static bool SelectSlideCore(UIA3Automation automation, string windowTitleSubstring, int slideNumber)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);
        if (window is null) return false;

        var counter = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
        if (counter is null) return false;
        if (counter.Value.Current == slideNumber) return true;

        var thumbnailPanel = FindThumbnailPanel(window, counter.Value.Total);
        if (thumbnailPanel is null) return false;

        AutomationElement[] thumbnails;
        try { thumbnails = ThumbnailChildrenOf(thumbnailPanel); }
        catch { return false; }

        if (slideNumber < 1 || slideNumber > thumbnails.Length) return false;
        var target = thumbnails[slideNumber - 1];

        // 실측 결과 이 패널은 LegacyIAccessible만 지원하지만 DoDefaultAction()과
        // accSelect(Select, 0x80020003 DISP_E_MEMBERNOTFOUND) 둘 다 "현재
        // 슬라이드"를 바꾸지 못한다(웹 캔버스로 그려지는 얕은 UIA 제공자라 패턴만
        // 있고 실제 선택 동작은 구현이 안 돼 있는 것으로 보임). 실제 마우스
        // 클릭/키보드 입력으로 우회할 수도 있지만, 이 프로젝트는 그런 실제 입력을
        // 쓰지 않는다는 원칙을 지킨다 - 사용자가 화면 제어 권한을 거부한 경우는
        // 더더욱 그렇다. 되는 UIA 메커니즘이 없으면 정직하게 실패를 보고한다.
        TrySafely(() => target.Patterns.LegacyIAccessible.Pattern.DoDefaultAction());
        Thread.Sleep(120);

        var after = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
        return after is not null && after.Value.Current == slideNumber;
    }

    // 왼쪽 축소판 목록: 각 항목은 Name 없이 LegacyIAccessible 패턴만 지원하는
    // Pane이고, 고정된 AutomationId가 없어서 "Pane 자식 개수가 전체 슬라이드 수와
    // 정확히 일치하는 Pane"이라는 구조적 특징으로 패널 자체를 찾는다. 패널 자신은
    // ScrollBar도 형제 자식으로 갖고 있으므로 Pane 타입만 센다.
    private static AutomationElement? FindThumbnailPanel(AutomationElement window, int totalSlides) =>
        window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Pane))
            .FirstOrDefault(p =>
            {
                try { return ThumbnailChildrenOf(p).Length == totalSlides; }
                catch { return false; }
            });

    private static AutomationElement[] ThumbnailChildrenOf(AutomationElement panel) =>
        panel.FindAllChildren()
            .Where(c => SafeGetControlType(c) == FlaUI.Core.Definitions.ControlType.Pane)
            .ToArray();

    // 작업이 끝난 뒤 전체 슬라이드를 한 번에 확인하기 위한 것 - 슬라이드를
    // 하나씩 선택해서(안 되는 걸로 이미 확인됨) 캡쳐하는 대신, 창 전체를 한 번만
    // 캡쳐하고 왼쪽 축소판 목록의 각 항목 위치로 크롭한다. 각 축소판의
    // BoundingRectangle은 패널 자신의 화면 좌상단 기준 상대좌표라는 게 실측으로
    // 확인됐다(패널 자신의 BoundingRectangle은 정상적으로 절대좌표) - 패널
    // 원점을 더해 창 기준 좌표로 바꾼다. 화질은 낮지만(약 220x108) 전체 검증
    // 목적(완전히 다른 내용이 들어간 중복/누락을 잡아내는 것)에는 충분하다.
    public static Bitmap[]? CaptureAllThumbnails(string windowTitleSubstring, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return CaptureAllThumbnailsCore(automation, windowTitleSubstring);

        using var owned = new UIA3Automation();
        return CaptureAllThumbnailsCore(owned, windowTitleSubstring);
    }

    private static Bitmap[]? CaptureAllThumbnailsCore(UIA3Automation automation, string windowTitleSubstring)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);
        if (window is null || !window.Properties.NativeWindowHandle.IsSupported) return null;

        var counter = PolarisPptx.GetSlideCounter(windowTitleSubstring, automation);
        if (counter is null) return null;

        var thumbnailPanel = FindThumbnailPanel(window, counter.Value.Total);
        if (thumbnailPanel is null) return null;

        // 축소판 패널은 가상화돼 있어서(실측) 한 번에 화면에 보이는 8~9장만
        // BoundingRectangle이 유효하고, 나머지는 {0,0,0,0}으로 나온다 - 스크롤이
        // 필요하다. 화살표/페이지 버튼의 Invoke는 실측으로 효과가 없었다(이
        // 패널의 다른 많은 컨트롤처럼 얕은 UIA 제공자 - 눌러도 실제로 스크롤이
        // 안 됨) - 대신 스크롤바 자체가 지원하는 RangeValue 패턴으로 위치 값을
        // 직접 설정한다. 이 스크롤바는 픽셀이 아니라 0~100 퍼센트 단위다(실측) -
        // 한 라운드에 전체의 7%씩(뷰포트 한 번 분량 정도) 내려간다. 맨 위부터
        // 시작한다.
        var scrollBar = thumbnailPanel.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ScrollBar));
        var rangeValue = scrollBar is not null && scrollBar.Patterns.RangeValue.IsSupported
            ? scrollBar.Patterns.RangeValue.Pattern
            : null;

        if (rangeValue is not null)
        {
            TrySafely(() => rangeValue.SetValue(rangeValue.Minimum.Value));
            Thread.Sleep(150);
        }

        Bitmap?[] results = new Bitmap?[counter.Value.Total];
        var noProgressRounds = 0;
        var stepPercent = rangeValue is not null ? (rangeValue.Maximum.Value - rangeValue.Minimum.Value) * 0.07 : 0;

        for (var round = 0; round < counter.Value.Total && noProgressRounds < 3; round++)
        {
            AutomationElement[] thumbnails;
            try { thumbnails = ThumbnailChildrenOf(thumbnailPanel); }
            catch { break; }

            var windowRect = window.BoundingRectangle;
            var panelRect = thumbnailPanel.BoundingRectangle;

            using var windowBitmap = WindowPrintCapture.CaptureWindow(
                window.Properties.NativeWindowHandle.Value, windowRect.Width, windowRect.Height);
            if (windowBitmap is null) break;

            var filledAny = false;
            for (var i = 0; i < thumbnails.Length && i < results.Length; i++)
            {
                if (results[i] is not null) continue;

                try
                {
                    var r = thumbnails[i].BoundingRectangle;
                    if (r.Width <= 0 || r.Height <= 0) continue; // 지금은 가상화로 안 보임

                    var cropRect = new Rectangle(
                        panelRect.Left + r.Left - windowRect.Left,
                        panelRect.Top + r.Top - windowRect.Top,
                        r.Width,
                        r.Height);
                    cropRect.Intersect(new Rectangle(0, 0, windowBitmap.Width, windowBitmap.Height));
                    if (cropRect.Width <= 0 || cropRect.Height <= 0) continue;

                    results[i] = windowBitmap.Clone(cropRect, windowBitmap.PixelFormat);
                    filledAny = true;
                }
                catch { /* 이 항목은 이번 라운드에 건너뛰고 다음에 재시도 */ }
            }

            if (results.All(r => r is not null)) break;

            noProgressRounds = filledAny ? 0 : noProgressRounds + 1;

            if (rangeValue is null) break; // 스크롤 방법을 못 찾으면 더 진행할 수 없음

            var nextValue = Math.Min(rangeValue.Value.Value + stepPercent, rangeValue.Maximum.Value);
            TrySafely(() => rangeValue.SetValue(nextValue));
            Thread.Sleep(250);
        }

        return results!;
    }

    public static Bitmap? CaptureCurrentSlideArea(string windowTitleSubstring, UIA3Automation? automation = null)
    {
        if (automation is not null)
            return CaptureCurrentSlideAreaCore(automation, windowTitleSubstring);

        using var owned = new UIA3Automation();
        return CaptureCurrentSlideAreaCore(owned, windowTitleSubstring);
    }

    private static Bitmap? CaptureCurrentSlideAreaCore(UIA3Automation automation, string windowTitleSubstring)
    {
        var window = WindowFinder.FindWindow(automation, windowTitleSubstring);
        if (window is null) return null;
        if (!window.Properties.NativeWindowHandle.IsSupported) return null;

        var windowRect = window.BoundingRectangle;

        // 조건 없는 FindAllDescendants()는 트리 전체를 C#에서 한 노드씩 훑는다 -
        // 슬라이드가 늘수록(트리가 커질수록) 선형으로 느려져서 뒤쪽 페이지로
        // 갈수록 한 페이지에 분 단위가 걸리던 핵심 병목이었다(실측). ByName
        // 조건으로 찾으면 UIA 자체의 네이티브 탐색을 타서 트리 크기와 거의
        // 무관하게 빠르다.
        var docWnd = window.FindFirstDescendant(cf => cf.ByName("DocWnd"));
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
