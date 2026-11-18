namespace LocalAssistant.Engine;

public enum JobOutcome
{
    Completed,
    FailedOnPage,
    SetupFailed
}

public record JobResult(JobOutcome Outcome, int? FailedPage, int LastCompletedPage, int TotalPages);

public static class PdfToPptxJob
{
    // Caps genuine content failures (pasted result verified different from source) -
    // this is the "something is actually wrong" budget from the design doc.
    private const int MaxContentFailuresPerPage = 3;

    // Caps transient UIA/COM hiccups (timeouts, reentrancy errors) separately and far
    // more generously - these are infrastructure noise, not evidence anything is
    // wrong with the page itself, and get worse under system load (e.g. a game
    // running at the same time), so they must not eat into the content-failure budget.
    private const int MaxTransientRetriesPerPage = 8;

    public static JobResult Run(
        string chromeTitle,
        string progressPath,
        Action<string> log)
    {
        var progress = JobProgress.Load(progressPath);

        var polarisTitle = ResolvePolarisWindow(progress, progressPath, log);
        if (polarisTitle is null)
            return new JobResult(JobOutcome.SetupFailed, null, progress.LastCompletedPage, progress.TotalPages);

        if (progress.TotalPages == 0)
        {
            var total = ChromePdfViewer.GetTotalPages(chromeTitle);
            if (total is null)
            {
                log("총 페이지 수를 읽지 못했습니다.");
                return new JobResult(JobOutcome.SetupFailed, null, 0, 0);
            }
            progress.TotalPages = total.Value;
            progress.Save(progressPath);
        }

        log($"총 {progress.TotalPages}페이지, {progress.LastCompletedPage}페이지까지 완료된 상태에서 시작");

        for (var page = progress.LastCompletedPage + 1; page <= progress.TotalPages; page++)
        {
            var success = false;
            var contentFailures = 0;
            var transientRetries = 0;

            while (!success && contentFailures < MaxContentFailuresPerPage && transientRetries < MaxTransientRetriesPerPage)
            {
                log($"[{page}/{progress.TotalPages}] 시도 (내용실패 {contentFailures}/{MaxContentFailuresPerPage}, 일시적 재시도 {transientRetries}/{MaxTransientRetriesPerPage})");

                try
                {
                    if (!PolarisSlideOps.EnsureSlideCountAtLeast(polarisTitle, page))
                    {
                        log("  슬라이드 확보 실패");
                        transientRetries++;
                        continue;
                    }

                    // Baseline BEFORE pasting: comparing the pasted result against this
                    // (rather than against the tightly-cropped Chrome source) avoids a
                    // framing mismatch - the source crop is just the page content, while
                    // the slide canvas capture includes margins/whitespace around it, so
                    // the two would never look "similar" even on a correct paste. What we
                    // actually want to know is: did the canvas visibly change at all.
                    using var beforeBitmap = PolarisSlideOps.CaptureCurrentSlideArea(polarisTitle);
                    if (beforeBitmap is null)
                    {
                        log("  붙여넣기 전 캔버스 캡쳐 실패");
                        transientRetries++;
                        continue;
                    }

                    if (!ChromePdfViewer.GoToPage(chromeTitle, page))
                    {
                        log("  Chrome 페이지 이동/렌더링 대기 실패");
                        transientRetries++;
                        continue;
                    }

                    var sourceAutomationId = ChromePdfViewer.PageAutomationId(page);

                    using var referenceBitmap = ChromeElementCapture.CaptureElementBitmap(chromeTitle, sourceAutomationId);
                    if (referenceBitmap is null)
                    {
                        log("  원본 캡쳐 실패");
                        transientRetries++;
                        continue;
                    }

                    ClipboardImage.SetImage(referenceBitmap);

                    if (!PageToClipboard.PasteInto(polarisTitle))
                    {
                        log("  붙여넣기 실패");
                        transientRetries++;
                        continue;
                    }

                    Thread.Sleep(500);

                    using var afterBitmap = PolarisSlideOps.CaptureCurrentSlideArea(polarisTitle);
                    if (afterBitmap is null)
                    {
                        log("  붙여넣은 슬라이드 캡쳐 실패");
                        ButtonClicker.ClickButtonByName(polarisTitle, "되돌리기");
                        transientRetries++;
                        continue;
                    }

                    if (ImageSimilarity.IsSimilar(beforeBitmap, afterBitmap, threshold: 6))
                    {
                        log("  검증 실패 (붙여넣기 전후 변화 없음) - 되돌리고 재시도");
                        ButtonClicker.ClickButtonByName(polarisTitle, "되돌리기");
                        Thread.Sleep(300);
                        contentFailures++;
                        continue;
                    }

                    log("  검증 통과");
                    success = true;
                }
                catch (Exception ex)
                {
                    // UIA COM calls are occasionally flaky mid-transition (e.g. right
                    // after an Undo animation, or just under system load) - never let
                    // one exception kill the whole multi-page job. This is infrastructure
                    // noise, not a sign the page itself is bad, so it draws from the
                    // transient budget, not the content-failure budget.
                    log($"  예외 발생, 재시도: {ex.Message}");
                    transientRetries++;
                    Thread.Sleep(500);
                }
            }

            if (!success)
            {
                log($"[{page}/{progress.TotalPages}] 재시도 소진(내용실패 {contentFailures}, 일시적 {transientRetries}) - 작업 중단");
                return new JobResult(JobOutcome.FailedOnPage, page, progress.LastCompletedPage, progress.TotalPages);
            }

            progress.LastCompletedPage = page;
            progress.Save(progressPath);
        }

        log("전체 완료");
        return new JobResult(JobOutcome.Completed, null, progress.LastCompletedPage, progress.TotalPages);
    }

    // Decides which Polaris window to paste into. A fresh job (no stored title)
    // or one whose window is still open just reuses/creates as needed. A job
    // that had already pasted pages but whose window is now gone (closed without
    // saving) refuses rather than silently starting a new blank deck - that would
    // throw away everything already pasted in without telling anyone.
    private static string? ResolvePolarisWindow(JobProgress progress, string progressPath, Action<string> log)
    {
        if (!string.IsNullOrEmpty(progress.PolarisWindowTitle) && WindowExists(progress.PolarisWindowTitle))
        {
            log($"기존 작업 중인 프레젠테이션 창을 재사용합니다: \"{progress.PolarisWindowTitle}\"");
            return progress.PolarisWindowTitle;
        }

        if (!string.IsNullOrEmpty(progress.PolarisWindowTitle) && progress.LastCompletedPage > 0)
        {
            log($"이전 작업 중이던 프레젠테이션 창(\"{progress.PolarisWindowTitle}\")을 찾을 수 없습니다. 저장하지 않고 닫힌 것으로 보입니다 - 진행을 이어갈 수 없습니다.");
            return null;
        }

        log("Polaris Office에서 새 프레젠테이션을 생성합니다.");
        var title = PolarisLauncher.OpenBlankPresentation(log);
        if (title is null) return null;

        progress.PolarisWindowTitle = title;
        progress.Save(progressPath);
        return title;
    }

    private static bool WindowExists(string titleSubstring)
    {
        using var automation = new FlaUI.UIA3.UIA3Automation();
        return automation.GetDesktop().FindAllChildren()
            .Any(e =>
            {
                try { return (e.Name ?? string.Empty).Contains(titleSubstring, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
    }
}
