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
    case "restore":
        return RunRestore();
    case "maximize":
        return RunMaximize();
    case "verify":
        return RunVerify();
    case "setkey":
        return RunSetKey();
    case "addfolder":
        return RunAddFolder();
    case "setfirebase":
        return RunSetFirebase();
    case "setendpoint":
        return RunSetEndpoint();
    case "setgatewaykey":
        return RunSetGatewayKey();
    case "trimhistory":
        return RunTrimHistory();
    case "runjob":
        return RunJob();
    case "ask":
        return RunAsk().GetAwaiter().GetResult();
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
    Console.WriteLine("       setkey <api-key>  (corebrain-gateway의 llmproxy 키 또는 Anthropic 콘솔 키, DPAPI로 암호화해서 로컬에 저장)");
    Console.WriteLine("       addfolder <경로>  (run_command가 실행을 허용할 폴더로 등록)");
    Console.WriteLine("       setfirebase <projectId> <uid> <serviceAccountJsonPath>  (폰↔PC 브릿지 설정)");
    Console.WriteLine($"       setendpoint <baseUrl>  (Claude 호출 대상, 기본값 {ClaudeEndpointStore.DefaultBaseUrl} = 로컬 corebrain-gateway)");
    Console.WriteLine("       setgatewaykey <키>  (corebrain-gateway의 COREBRAIN_GATEWAY_SECURITY_KEY, 브라우저 도구 활성화용)");
    Console.WriteLine("       trimhistory  (대화 기록에 누적된 오래된 스크린샷을 지워서 응답 속도를 되돌림)");
    Console.WriteLine("       runjob <chrome-title-substring> <progress-json-path>  (M1: Chrome의 PDF를 Polaris 프레젠테이션으로 복제, Polaris는 자동 실행/생성됨)");
    Console.WriteLine("       restore <window-title-substring>  (최소화된 창을 복원하고 전경으로 가져옴)");
    Console.WriteLine("       maximize <window-title-substring>  (창을 최대화하고 전경으로 가져옴)");
    Console.WriteLine("       verify <chrome-title-substring> <polaris-title-substring> <totalPages>  (완성된 Polaris 문서를 원본 PDF와 전체 비교 - 보조 점검용, 썸네일 해상도 한계로 완벽하지 않음)");
    Console.WriteLine("       ask <메시지>  (개인비서 Orchestrator를 GUI 없이 CLI에서 한 번 실행 - gui_input/see_screen 등 실제 PC 조작 도구를 그대로 씀)");
}

async Task<int> RunAsk()
{
    var message = args.Length > 1 ? string.Join(' ', args.Skip(1)) : string.Empty;
    if (string.IsNullOrWhiteSpace(message))
    {
        PrintUsage();
        return 1;
    }

    var apiKey = ApiKeyStore.Load();
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("API 키가 설정되어 있지 않습니다. 'setkey <키>'를 먼저 실행하세요.");
        return 1;
    }

    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");
    Directory.CreateDirectory(dataDir);
    var history = new ConversationStore(Path.Combine(dataDir, "cli-ask-history.json"));

    var client = new ClaudeClient(new HttpClient(), apiKey, ClaudeEndpointStore.Load());
    var tools = new ToolRegistry();
    tools.Register(new RunCommandTool());
    tools.Register(new ListWindowsTool());
    tools.Register(new DumpWindowTool());
    tools.Register(new ScreenshotWindowTool());
    tools.Register(new FilesReadTool());
    tools.Register(new FilesWriteTool());
    tools.Register(new FilesEditTool());
    tools.Register(new GuiInputTool());
    tools.Register(new SeeScreenTool());
    tools.Register(new KakaoTalkTool());

    var gatewayKey = GatewayKeyStore.Load();
    if (!string.IsNullOrWhiteSpace(gatewayKey))
        tools.Register(new BrowserTool(new GatewayClient(new HttpClient(), gatewayKey)));

    var taskRunner = new TaskRunner();
    var orchestrator = new Orchestrator(client, tools, taskRunner, history);
    orchestrator.MessageAdded += msg => Console.WriteLine($"[{msg.Role}] {msg.Text}");
    orchestrator.StatusChanged += status =>
    {
        if (status is not null) Console.WriteLine($"... {status}");
    };

    await orchestrator.HandleUserMessageAsync(message, CancellationToken.None);
    return 0;
}

int RunRestore()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    var ok = WindowRestorer.RestoreAndForeground(titleContains);
    Console.WriteLine(ok ? $"복원됨: \"{titleContains}\"" : $"창을 찾지 못함: \"{titleContains}\"");
    return ok ? 0 : 1;
}

int RunVerify()
{
    var chromeTitle = args.Length > 1 ? args[1] : string.Empty;
    var polarisTitle = args.Length > 2 ? args[2] : string.Empty;
    var totalPages = args.Length > 3 && int.TryParse(args[3], out var t) ? t : 0;

    var mismatches = PdfToPptxJob.VerifyAllPages(chromeTitle, polarisTitle, totalPages, Console.WriteLine);
    Console.WriteLine(mismatches.Count > 0
        ? $"불일치 쪽: {string.Join(", ", mismatches)}"
        : "모든 페이지 일치");
    return mismatches.Count > 0 ? 1 : 0;
}

int RunMaximize()
{
    var titleContains = args.Length > 1 ? args[1] : string.Empty;
    var ok = WindowRestorer.MaximizeAndForeground(titleContains);
    Console.WriteLine(ok ? $"최대화됨: \"{titleContains}\"" : $"창을 찾지 못함: \"{titleContains}\"");
    return ok ? 0 : 1;
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

int RunSetEndpoint()
{
    var url = args.Length > 1 ? args[1] : string.Empty;
    if (string.IsNullOrWhiteSpace(url))
    {
        PrintUsage();
        return 1;
    }

    ClaudeEndpointStore.Save(url);
    Console.WriteLine($"Claude 호출 대상을 저장했습니다: {url}");
    return 0;
}

int RunSetGatewayKey()
{
    var key = args.Length > 1 ? args[1] : string.Empty;
    if (string.IsNullOrWhiteSpace(key))
    {
        PrintUsage();
        return 1;
    }

    GatewayKeyStore.Save(key);
    Console.WriteLine("corebrain-gateway 보안 키를 암호화해서 저장했습니다. (브라우저 도구 활성화)");
    return 0;
}

int RunTrimHistory()
{
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");
    var historyPath = Path.Combine(dataDir, "conversation.json");
    var before = File.Exists(historyPath) ? new FileInfo(historyPath).Length : 0;

    var history = new ConversationStore(historyPath);
    history.TrimAllImageHistory();

    var after = new FileInfo(historyPath).Length;
    Console.WriteLine($"대화 기록 정리 완료: {before:N0} bytes -> {after:N0} bytes");
    return 0;
}

int RunSetFirebase()
{
    if (args.Length < 4)
    {
        PrintUsage();
        return 1;
    }

    var projectId = args[1];
    var uid = args[2];
    var jsonPath = args[3];
    if (!File.Exists(jsonPath))
    {
        Console.WriteLine($"서비스 계정 JSON 파일을 찾을 수 없습니다: \"{jsonPath}\"");
        return 1;
    }

    FirebaseConfigStore.Save(new FirebaseConfig(projectId, uid, Path.GetFullPath(jsonPath)));
    Console.WriteLine("Firebase 설정을 저장했습니다. 앱을 재시작하면 폰↔PC 브릿지가 켜집니다.");
    return 0;
}

int RunAddFolder()
{
    var path = args.Length > 1 ? args[1] : string.Empty;
    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
    {
        Console.WriteLine($"폴더를 찾을 수 없습니다: \"{path}\"");
        return 1;
    }

    AllowedFoldersStore.Add(path);
    Console.WriteLine($"허용된 폴더로 등록했습니다: {Path.GetFullPath(path)}");
    return 0;
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
