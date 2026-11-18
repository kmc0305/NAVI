using LocalAssistant.Engine;

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

switch (args[0])
{
    case "dump":
        return RunDump();
    case "shot":
        return RunShot();
    case "list":
        Console.WriteLine(WindowLister.ListTopLevelWindows());
        return 0;
    case "slidecount":
        return RunSlideCount();
    case "click":
        return RunClick();
    case "copyelem":
        return RunCopyElem();
    case "paste":
        return RunPaste();
    case "runjob":
        return RunJob();
    case "totalpages":
        return RunTotalPages();
    case "gotopage":
        return RunGoToPage();
    case "hometab":
        Console.WriteLine(PolarisSlideOps.EnsureHomeTabActive(args.Length > 1 ? args[1] : string.Empty) ? "성공" : "실패");
        return 0;
    case "setkey":
        return RunSetKey();
    default:
        PrintUsage();
        return 1;
}

int RunDump()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    var maxDepth = args.Length > 2 && int.TryParse(args[2], out var d) ? d : 6;

    if (string.IsNullOrWhiteSpace(titleContains))
    {
        PrintUsage();
        return 1;
    }

    var tree = UiaTreeDumper.DumpWindowByTitle(titleContains, maxDepth);

    if (tree is null)
    {
        Console.WriteLine($"창을 찾지 못했습니다: 제목에 \"{titleContains}\" 포함된 창 없음");
        return 1;
    }

    Console.WriteLine(tree);
    return 0;
}

int RunShot()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    var outputPath = args.Length > 2 ? args[2] : "shot.png";

    if (string.IsNullOrWhiteSpace(titleContains))
    {
        PrintUsage();
        return 1;
    }

    var ok = WindowCapture.CaptureWindowByTitle(titleContains, outputPath);
    if (!ok)
    {
        Console.WriteLine($"창을 찾지 못했거나 캡쳐 실패: \"{titleContains}\"");
        return 1;
    }

    Console.WriteLine($"저장됨: {outputPath}");
    return 0;
}

void PrintUsage()
{
    Console.WriteLine("usage: dump <window-title-substring> [maxDepth]");
    Console.WriteLine("       shot <window-title-substring> [outputPath]");
    Console.WriteLine("       list");
    Console.WriteLine("       slidecount <window-title-substring>");
    Console.WriteLine("       click <window-title-substring> <button-name>");
    Console.WriteLine("       copyelem <window-title-substring> <automationId>");
    Console.WriteLine("       paste <window-title-substring>");
    Console.WriteLine("       runjob <chrome-title-substring> <progress-json-path>  (Polaris는 자동으로 실행/생성됨)");
    Console.WriteLine("       setkey <anthropic-api-key>  (DPAPI로 암호화해서 로컬에 저장)");
}

int RunJob()
{
    var chromeTitle = args.Length > 1 ? args[1] : string.Empty;
    var progressPath = args.Length > 2 ? args[2] : string.Empty;

    if (string.IsNullOrWhiteSpace(chromeTitle) || string.IsNullOrWhiteSpace(progressPath))
    {
        PrintUsage();
        return 1;
    }

    var result = PdfToPptxJob.Run(chromeTitle, progressPath, Console.WriteLine);

    Console.WriteLine($"결과: {result.Outcome}, 완료: {result.LastCompletedPage}/{result.TotalPages}");
    return result.Outcome == JobOutcome.Completed ? 0 : 1;
}

int RunTotalPages()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    if (string.IsNullOrWhiteSpace(titleContains))
    {
        PrintUsage();
        return 1;
    }

    var total = ChromePdfViewer.GetTotalPages(titleContains);
    Console.WriteLine(total is null ? "읽기 실패" : $"총 페이지: {total}");
    return total is null ? 1 : 0;
}

int RunGoToPage()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    var pageArg = args.Length > 2 ? args[2] : string.Empty;

    if (string.IsNullOrWhiteSpace(titleContains) || !int.TryParse(pageArg, out var page))
    {
        PrintUsage();
        return 1;
    }

    var ok = ChromePdfViewer.GoToPage(titleContains, page);
    Console.WriteLine(ok ? "이동 성공" : "이동 실패");
    return ok ? 0 : 1;
}

int RunSlideCount()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    if (string.IsNullOrWhiteSpace(titleContains))
    {
        PrintUsage();
        return 1;
    }

    var result = PolarisPptx.GetSlideCounter(titleContains);
    if (result is null)
    {
        Console.WriteLine("슬라이드 카운터를 찾지 못했습니다.");
        return 1;
    }

    Console.WriteLine($"현재: {result.Value.Current}, 전체: {result.Value.Total}");
    return 0;
}

int RunClick()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    var buttonName = args.Length > 2 ? args[2] : string.Empty;

    if (string.IsNullOrWhiteSpace(titleContains) || string.IsNullOrWhiteSpace(buttonName))
    {
        PrintUsage();
        return 1;
    }

    var ok = ButtonClicker.ClickButtonByName(titleContains, buttonName);
    Console.WriteLine(ok ? "클릭 성공" : "버튼을 찾지 못함");
    return ok ? 0 : 1;
}

int RunCopyElem()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    var automationId = args.Length > 2 ? args[2] : string.Empty;

    if (string.IsNullOrWhiteSpace(titleContains) || string.IsNullOrWhiteSpace(automationId))
    {
        PrintUsage();
        return 1;
    }

    var ok = PageToClipboard.CaptureElementToClipboard(titleContains, automationId);
    Console.WriteLine(ok ? "클립보드에 복사됨" : "요소를 찾지 못함");
    return ok ? 0 : 1;
}

int RunSetKey()
{
    var key = args.Length > 1 ? args[1] : string.Empty;
    if (string.IsNullOrWhiteSpace(key))
    {
        PrintUsage();
        return 1;
    }

    ApiKeyStore.Save(key);
    Console.WriteLine("API 키를 암호화해서 저장했습니다.");
    return 0;
}

int RunPaste()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    if (string.IsNullOrWhiteSpace(titleContains))
    {
        PrintUsage();
        return 1;
    }

    var ok = PageToClipboard.PasteInto(titleContains);
    Console.WriteLine(ok ? "붙여넣기 전송됨" : "창을 찾지 못함");
    return ok ? 0 : 1;
}
