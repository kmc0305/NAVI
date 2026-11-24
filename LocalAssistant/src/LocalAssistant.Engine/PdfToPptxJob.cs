using FlaUI.UIA3;

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

        // 창이 작으면 PDF 페이지 콘텐츠가 뷰포트보다 커져 내부 스크롤이 생기고,
        // 캡쳐가 그 잘린 부분만 가져온다(실측: 페이지 아래/오른쪽이 잘려서
        // 붙여넣어짐) - 작업 시작 전에 두 창을 모두 최대화해 여유 공간을 확보한다.
        WindowRestorer.MaximizeAndForeground(polarisTitle);
        WindowRestorer.MaximizeAndForeground(chromeTitle);

        // 작업 전체에서 COM 자동화 세션 하나를 공유한다. 호출마다 새
        // UIA3Automation을 만들고 버리면 느릴 뿐 아니라(세션 생성 자체가 가볍지
        //않음), 세션이 빠르게 생성/폐기되는 사이에 다른 호출이 끼어들면
        // 0x80040201(이벤트 재진입) COM 예외가 연쇄로 나는 것도 실측으로 확인됨 -
        // 같은 페이지에서 계속 재시도만 반복되고 못 끝나던 원인이 바로 이것.
        using var automation = new UIA3Automation();

        if (progress.TotalPages == 0)
        {
            var total = ChromePdfViewer.GetTotalPages(chromeTitle, automation);
            if (total is null)
            {
                log("총 페이지 수를 읽지 못했습니다.");
                return new JobResult(JobOutcome.SetupFailed, null, 0, 0);
            }
            progress.TotalPages = total.Value;
            progress.Save(progressPath);
        }

        // 사용자가 잘못된 슬라이드를 지우는 등으로 실제 장수가 재개 지점보다
        // 많아질 수 있다(실측: 35,36쪽 삭제 후 Total=84, 재개 목표=35) - 그대로
        // 두면 EnsureSlideCountAtLeast가 "이미 충분함" 경로로 가서 안 되는
        // SelectSlide를 타게 된다. 되돌리기로 미리 맞춰 놓는다.
        var currentCounter = PolarisPptx.GetSlideCounter(polarisTitle, automation);
        if (currentCounter is not null && currentCounter.Value.Total > progress.LastCompletedPage)
        {
            log($"실제 슬라이드 수({currentCounter.Value.Total})가 재개 지점({progress.LastCompletedPage})보다 많음 - 되돌리기로 맞춤");
            if (!PolarisSlideOps.TrimToCount(polarisTitle, progress.LastCompletedPage, automation))
            {
                log("슬라이드 수를 맞추지 못했습니다.");
                return new JobResult(JobOutcome.SetupFailed, null, progress.LastCompletedPage, progress.TotalPages);
            }
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
                    if (!PolarisSlideOps.EnsureSlideCountAtLeast(polarisTitle, page, automation))
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
                    using var beforeBitmap = PolarisSlideOps.CaptureCurrentSlideArea(polarisTitle, automation);
                    if (beforeBitmap is null)
                    {
                        log("  붙여넣기 전 캔버스 캡쳐 실패");
                        transientRetries++;
                        continue;
                    }

                    if (!ChromePdfViewer.GoToPage(chromeTitle, page, automation))
                    {
                        log("  Chrome 페이지 이동/렌더링 대기 실패");
                        transientRetries++;
                        continue;
                    }

                    var sourceAutomationId = ChromePdfViewer.PageAutomationId(page);

                    using var referenceBitmap = ChromeElementCapture.CaptureElementBitmap(chromeTitle, sourceAutomationId, automation);
                    if (referenceBitmap is null)
                    {
                        log("  원본 캡쳐 실패");
                        // 창이 어떤 이유로든 최대화 해제되면 이 페이지의 내용이
                        // 뷰포트보다 커져 다시 잘린다(실측) - 재시도 전에 복구.
                        WindowRestorer.MaximizeAndForeground(chromeTitle);
                        transientRetries++;
                        continue;
                    }

                    ClipboardImage.SetImage(referenceBitmap);

                    if (!PageToClipboard.PasteInto(polarisTitle, automation))
                    {
                        log("  붙여넣기 실패");
                        transientRetries++;
                        continue;
                    }

                    // 캔버스 렌더링이 아직 안 끝난 상태에서 비교하면 "변화 없음"으로
                    // 오판할 수 있다(실측: 내용이 많은 페이지일수록 붙여넣기 직후
                    // 렌더링이 늦게 끝남) - 바로 포기하지 않고 간격을 늘려가며 몇 번
                    // 더 다시 캡쳐해서 비교한다.
                    Bitmap? afterBitmap = null;
                    var changed = false;
                    int[] pollDelaysMs = [120, 150, 200];
                    foreach (var delay in pollDelaysMs)
                    {
                        Thread.Sleep(delay);
                        afterBitmap?.Dispose();
                        afterBitmap = PolarisSlideOps.CaptureCurrentSlideArea(polarisTitle, automation);
                        if (afterBitmap is not null && !ImageSimilarity.IsSimilar(beforeBitmap, afterBitmap, threshold: 6))
                        {
                            changed = true;
                            break;
                        }
                    }

                    using var _ = afterBitmap;

                    if (afterBitmap is null)
                    {
                        log("  붙여넣은 슬라이드 캡쳐 실패");
                        ButtonClicker.ClickButtonByName(polarisTitle, "되돌리기", automation);
                        transientRetries++;
                        continue;
                    }

                    if (!changed)
                    {
                        log("  검증 실패 (붙여넣기 전후 변화 없음) - 되돌리고 재시도");
                        ButtonClicker.ClickButtonByName(polarisTitle, "되돌리기", automation);
                        Thread.Sleep(120);
                        contentFailures++;
                        continue;
                    }

                    // "변화가 있었다"만으로는 부족하다 - 창이 작아 원본이 잘린 채로
                    // 붙여넣어지는 것처럼, 뭔가 바뀌긴 했지만 틀린/일부만 있는 내용일
                    // 수 있다(실측: 34쪽 아래/오른쪽 잘림, 그리고 Chrome 페이지 이동이
                    // 조용히 실패해서 34쪽이 35,36쪽에 중복된 사건). 캡쳐한
                    // 원본(reference)과 실제로 붙여넣어진 결과(afterBitmap)를 직접
                    // 비교해 확인한다. 정상적으로 완전히 똑같이 붙여넣어져도 표/색상
                    // 배경처럼 캔버스 여백 비중이 큰 레이아웃은 점수가 꽤 올라갈 수
                    // 있다(실측: 코드 블록 5.08, 녹색 표 10.11 - 둘 다 육안으로 완전
                    // 일치 확인됨) - 둘 다 통과하도록 문턱값을 넉넉히 둔다.
                    if (ImageSimilarity.GridDissimilarity(referenceBitmap, afterBitmap, gridSize: 16) > 20)
                    {
                        log("  검증 실패 (원본과 붙여넣은 결과가 다름 - 잘렸거나 손상된 것으로 보임) - 되돌리고 재시도");
                        ButtonClicker.ClickButtonByName(polarisTitle, "되돌리기", automation);
                        Thread.Sleep(120);
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
                    Thread.Sleep(150);
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

    // 페이지별 검증(이전/이후 비교, 원본/결과 비교)이 통과해도 놓칠 수 있는
    // 사건이 실측으로 확인됐다 - Chrome 페이지 이동이 조용히 실패하면 "원본"
    // 캡쳐 자체가 이전 페이지 내용이 되어, 틀린 원본과 틀린 결과가 서로 일치해서
    // 통과해버린다(86쪽 작업 중 34쪽 내용이 35, 36쪽에 중복된 사건 - 이 메서드가
    // 찾으려던 바로 그 사건). 슬라이드를 하나씩 선택할 필요 없이(안 되는 걸로
    // 확인됨) 왼쪽 축소판을 한 번에 캡쳐해서 비교하는 시도였지만, 실측 결과
    // 축소판 해상도에서는 "같은 템플릿, 다른 본문" 경우를 안정적으로 구분하지
    // 못한다는 게 드러났다(정상 사례 점수가 비정상 사례 점수 사이에 끼어
    // 들어가 임계값을 어떻게 잡아도 한쪽은 놓친다 - 실측: 정상 17.4/17.5,
    // 비정상 13.6/27.6). 그래서 Run()에서 자동 호출하지는 않는다 - "전체 검증
    // 통과"라는 거짓 확신을 주는 게 아무 검증도 안 하는 것보다 나쁘다. 완전히
    // 다른 내용/빈 슬라이드처럼 큰 차이가 나는 사고를 잡아내는 보조 점검
    // 용도로만 수동(CLI `verify`)으로 쓴다.
    public static List<int> VerifyAllPages(string chromeTitle, string polarisTitle, int totalPages, Action<string> log)
    {
        using var automation = new UIA3Automation();
        return VerifyAllPagesCore(automation, chromeTitle, polarisTitle, totalPages, log);
    }

    private static List<int> VerifyAllPagesCore(
        UIA3Automation automation, string chromeTitle, string polarisTitle, int totalPages, Action<string> log)
    {
        var mismatches = new List<int>();

        WindowRestorer.MaximizeAndForeground(polarisTitle);
        WindowRestorer.MaximizeAndForeground(chromeTitle);

        var thumbnails = PolarisSlideOps.CaptureAllThumbnails(polarisTitle, automation);
        if (thumbnails is null)
        {
            log("  전체 검증: 축소판을 가져오지 못해 건너뜀");
            return mismatches;
        }

        if (thumbnails.Length != totalPages)
            log($"  전체 검증: 슬라이드 수({thumbnails.Length})가 쪽수({totalPages})와 다름");

        for (var page = 1; page <= Math.Min(thumbnails.Length, totalPages); page++)
        {
            var thumbnail = thumbnails[page - 1];
            if (thumbnail is null)
            {
                log($"  [검증 {page}/{totalPages}] 축소판 캡쳐 실패 - 건너뜀");
                continue;
            }

            if (!ChromePdfViewer.GoToPage(chromeTitle, page, automation))
            {
                log($"  [검증 {page}/{totalPages}] 원본 페이지 이동 실패 - 건너뜀");
                continue;
            }

            using var referenceBitmap = ChromeElementCapture.CaptureElementBitmap(
                chromeTitle, ChromePdfViewer.PageAutomationId(page), automation);
            if (referenceBitmap is null)
            {
                log($"  [검증 {page}/{totalPages}] 원본 캡쳐 실패 - 건너뜀");
                continue;
            }

            // 축소판은 Chrome의 꽉 찬 크롭과 비교 기준이 달라서(해상도가 낮고
            // 여백/압축 특성이 다름) 본문 검증(threshold 8.5)과 같은 기준을 쓰면
            // 안 된다 - 실측으로 확인한 축소판 전용 기준: 같은 내용이면 10~15,
            // 다른 내용이면 100+ 로 차이가 훨씬 크게 난다.
            if (ImageSimilarity.GridDissimilarity(referenceBitmap, thumbnail, gridSize: 16) > 40)
            {
                log($"  [검증 {page}/{totalPages}] 불일치 (원본과 다른 것으로 보임)");
                mismatches.Add(page);
            }
        }

        return mismatches;
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
            // 이전 실행이 실패 직후(예: 되돌리기 직후) 바로 종료됐을 수 있다 - 짧게
            // 한 번 안정화 시간을 준다(근본 원인은 세션 재사용으로 고쳤으므로
            // 길게 둘 필요는 없음).
            Thread.Sleep(150);
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

    private static bool WindowExists(string titleSubstring) =>
        WindowFinder.FindTopLevelWindowHandle(titleSubstring) is not null;
}
