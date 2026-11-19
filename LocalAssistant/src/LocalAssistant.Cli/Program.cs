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
    Console.WriteLine("       setkey <anthropic-api-key>  (DPAPI로 암호화해서 로컬에 저장)");
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


