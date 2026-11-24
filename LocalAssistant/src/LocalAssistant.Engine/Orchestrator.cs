using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// System: 도구 진행 로그("화면 캡쳐 중" 등) - 잦고 PC 전용, Firestore에 올리지 않음.
// Error: 작업이 중간에 멈춘 걸 알리는 종료성 메시지(API 호출 실패, 라운드 초과) - 드물고
// 사용자가 반드시 알아야 하므로 FirestoreBridge가 폰에도 올려준다.
public enum ChatRole { User, Assistant, System, Error }

public record ChatMessage(ChatRole Role, string Text, string Source = "local");

// One independent loop runs per incoming user message (03-orchestrator.md
// 2.2) - only the actual tool EXECUTION is serialized via TaskRunner, never
// the Claude API calls, so a plain question answers immediately even while
// another message's tool call is still running.
public class Orchestrator
{
    // PDF 뷰어를 한 쪽씩 캡쳐하는 식의 작업은 쪽수만큼 라운드가 필요하다(86쪽 문서 +
    // 중간중간 창 복구 등 문제 해결용 추가 라운드까지 감안). 구독 재사용 게이트웨이라
    // 토큰당 과금은 아니므로 여유 있게 잡는다 - 그래도 진짜 무한 루프를 막는 안전판으로
    // 상한은 유지한다.
    private const int MaxToolRounds = 200;
    private const string BasePrompt =
        "너는 사용자의 윈도우 PC에서 작업을 대신 수행하는 상주형 개인 비서다. " +
        "필요하면 제공된 도구를 사용해서 실제로 작업을 실행하고, 그냥 질문이면 도구 없이 자연스럽게 대답해라. " +
        "중요: 사용자가 클릭/입력/전송 같은 실제 조작을 명확히 요청한 경우에만 gui_input(focus_window, " +
        "click_element, click_at, type_text, type_into_element, press_key)을 사용해라. 단순히 화면에 " +
        "뭐가 있는지 보거나 확인해달라는 요청(예: '뭐가 열려있어?', '화면 좀 봐줘', '이거 맞아?')에는 " +
        "see_screen이나 dump_window 같은 읽기 전용 도구로만 보고, 눈에 보이는 입력창이나 버튼이 있어도 " +
        "거기에 클릭하거나 타이핑하거나 메시지를 보내지 말고 본 내용만 그대로 보고해라. " +
        "효율적으로 작업해라: 특정 앱(카카오톡, 메모장 등)을 조작할 때는 전체 화면을 반복 캡쳐하지 " +
        "말고, 먼저 dump_window로 그 앱의 버튼/입력창 이름을 확인한 뒤 click_element/type_into_element로 " +
        "바로 조작해라 - 창이 최소화돼 있거나 다른 창에 가려져 있어도 자동으로 맨 앞으로 가져오니 " +
        "작업표시줄을 따로 클릭할 필요 없다. 화면 구조를 모르거나 접근성 트리가 없는 경우(게임, 그림판 " +
        "같은 캔버스 앱 등)에만 see_screen(가능하면 titleContains로 그 창만)으로 보고 click_at을 써라. " +
        "type_text나 press_key로 실제 입력을 보내기 전에는 그 입력이 의도한 창으로 가는 게 확실해야 " +
        "한다 - 방금 click_element/focus_window로 포커스를 옮긴 창이 아니라면 먼저 그렇게 포커스를 " +
        "확실히 옮긴 뒤에 입력해라 (안 그러면 입력이 엉뚱한 창, 특히 이 채팅창 자신으로 샐 수 있다). " +
        "반복되는 고정 작업(예: 카카오톡으로 파일 보내기)은 먼저 전용 도구(예: kakao_send_file)가 " +
        "있는지부터 확인하고, 있으면 그것부터 써라 - 매번 화면을 보고 클릭 위치를 다시 판단하는 " +
        "것보다 훨씬 빠르고 안정적이다. 전용 도구가 없으면 dump_window로 이름을 확인해 " +
        "click_element/type_into_element를 쓰고, 그래도 안 되면(접근성 트리가 비어있거나 커스텀 " +
        "드로잉인 경우) see_screen+click_at으로 간다. " +
        "한국어로 대답해라.";

    private readonly ClaudeClient _client;
    private readonly ToolRegistry _tools;
    private readonly TaskRunner _taskRunner;

    // 같은 세션에 메시지가 겹쳐 들어오면(예: 폰에서 도중에 "하던 거 멈추고 이거 해줘") 두
    // HandleUserMessageAsync 호출이 동시에 같은 history를 건드리게 되는데, 그러면 Claude
    // API가 요구하는 tool_use/tool_result 순서가 깨져서 "API 호출 오류"가 난다(실측). 그래서
    // 새 메시지가 들어오면 먼저 이전 호출에 취소 신호를 보내고, 한 번에 하나만 history를
    // 건드리도록 세마포어로 직렬화한다.
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private CancellationTokenSource? _activeCts;

    // 세션 전환(새 대화/다른 대화로 이동) 때 ChatWindow가 SwitchHistory로 갈아 끼운다.
    // 진행 중인 HandleUserMessageAsync 호출은 시작할 때 로컬 변수로 한 번 캡쳐해서 쓰므로
    // 중간에 세션이 바뀌어도 그 호출은 원래 세션에 계속 안전하게 쓰인다.
    private ConversationStore _history;
    private string _systemPrompt;

    public event Action<ChatMessage>? MessageAdded;

    // ChatWindow가 입력 대기 중 "지금 뭘 하고 있는지"를 보여주는 로딩 표시줄에 쓴다.
    // null이면 로딩 표시를 숨긴다. MessageAdded(영구 대화 기록)와는 별개로, 일시적인
    // 상태 문구만 전달한다.
    public event Action<string?>? StatusChanged;

    // 디버깅/점검용 - 지금 이 세션에 실제로 쓰이는 시스템 프롬프트(memory.md 포함 여부 등)를
    // 그대로 노출한다.
    public string CurrentSystemPrompt => _systemPrompt;

    public Orchestrator(ClaudeClient client, ToolRegistry tools, TaskRunner taskRunner, ConversationStore history)
    {
        _client = client;
        _tools = tools;
        _taskRunner = taskRunner;
        _history = history;
        _systemPrompt = BuildSystemPrompt();
    }

    // ChatWindow가 다른 세션으로 전환할 때 호출 - memory.md가 그 사이에 바뀌었을 수 있으니
    // 시스템 프롬프트도 같이 다시 만든다.
    public void SwitchHistory(ConversationStore newHistory)
    {
        _history = newHistory;
        _systemPrompt = BuildSystemPrompt();
    }

    private static string BuildSystemPrompt()
    {
        var prompt = BasePrompt;

        // 사용자가 매번 전체 경로를 말해주지 않아도 run_command를 쓸 수 있게, 이미
        // 허용된(AllowedFoldersStore) 폴더 목록을 시스템 프롬프트에 그대로 넣어둔다 -
        // 폴더 이름/내용으로 요청과 매칭되면 그 경로를 workingDirectory로 바로 쓰면 된다.
        var allowedFolders = AllowedFoldersStore.Load();
        if (allowedFolders.Count > 0)
        {
            prompt += "\n\n다음은 run_command 실행이 이미 허용된 폴더 목록이다. 사용자가 폴더 경로를 " +
                "직접 말해주지 않아도, 요청 내용(도구 이름, 하려는 작업)과 폴더 이름이 맞으면 경로를 " +
                "되묻지 말고 이 중에서 찾아서 바로 workingDirectory로 써라. 폴더 안에 뭐가 있는지 " +
                "확실하지 않으면 먼저 run_command로 'dir'을 실행해 확인한 뒤 진행해라:\n" +
                string.Join("\n", allowedFolders.Select(f => $"- {f}"));
        }

        var memory = MemoryStore.Load();
        if (string.IsNullOrWhiteSpace(memory)) return prompt;
        return prompt +
            "\n\n다음은 사용자와 예전에 나눈 대화에서 네가 실제로 기억해둔 내용이다 - 사용자가 전에 " +
            "직접 말해준 사실이니 그대로 믿고 써라. 사용자가 \"기억나?\", \"뭐 기억해둔 거 있어?\" 같이 " +
            "물어보면 지금 대화에 없는 내용이라도 아래 내용을 바탕으로 자신 있게 답해라 - \"기억에 없다\"고 " +
            "하지 마라, 지금 네 기억이 바로 이것이다. 다만 지금 대화 내용과 명백히 모순되면 지금 대화를 " +
            "우선해라:\n" +
            memory;
    }

    // 새 대화를 시작하기 전에, 방금까지 쓰던 세션을 요약해서 memory.md에 남기기 위해
    // ChatWindow가 부른다. 실제 세션 히스토리에는 아무것도 추가하지 않는 별도 호출이라,
    // 요약이 실패해도(네트워크 오류 등) 새 세션 생성 자체를 막지 않는다.
    public async Task<string?> SummarizeSessionAsync(ConversationStore history, CancellationToken ct)
    {
        var messages = history.Messages;
        if (messages.Count == 0) return null;

        var clone = new JsonArray();
        foreach (var m in messages) clone.Add(m!.DeepClone());
        clone.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "지금까지의 대화를 나중에 새 대화를 시작할 때 참고할 수 있도록, 사용자에 대해 " +
                "기억해둘 만한 핵심 사실/맥락/진행 중이던 작업만 5줄 이내 bullet로 요약해라. 인사말이나 " +
                "사소한 내용은 빼라."
        });

        try
        {
            var result = await _client.SendAsync(
                clone, new JsonArray(), "너는 대화를 간결하게 요약하는 보조 도구다. 한국어로 답해라.", ct);
            return result.Text;
        }
        catch
        {
            return null;
        }
    }

    public async Task HandleUserMessageAsync(string userText, CancellationToken ct, string source = "local")
    {
        // 혹시 아직 안 끝난 이전 호출이 있으면 먼저 중단시키고, 그게 실제로 멈출 때까지
        // (세마포어를 놓을 때까지) 기다린 뒤에야 이 메시지를 history에 추가한다 - 두 호출이
        // 동시에 history를 건드리는 일이 없도록 보장.
        _activeCts?.Cancel();
        await _historyLock.WaitAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts = cts;
        var callCt = cts.Token;

        var history = _history;
        var systemPrompt = _systemPrompt;

        history.AddUserText(userText);
        MessageAdded?.Invoke(new ChatMessage(ChatRole.User, userText, source));

        var toolDefs = _tools.BuildToolDefinitions();

        try
        {
            for (var round = 0; round < MaxToolRounds; round++)
            {
                StatusChanged?.Invoke("Claude에게 물어보는 중...");
                ClaudeResult result;
                try
                {
                    result = await _client.SendAsync(
                        history.RecentForApi(), toolDefs, systemPrompt, callCt,
                        status => StatusChanged?.Invoke(status));
                }
                catch (OperationCanceledException)
                {
                    return; // 새 메시지로 대체됨 - 에러로 보여주지 않고 조용히 멈춘다.
                }
                catch (Exception ex)
                {
                    MessageAdded?.Invoke(new ChatMessage(ChatRole.Error, $"Claude 호출 실패: {ex.Message}"));
                    return;
                }

                // 03-orchestrator.md 7: minimum viable cost tracking. Goes to the
                // console/log, not the visible chat - a running token tally isn't
                // something the user needs to read in every reply.
                Console.WriteLine(
                    $"[토큰 사용] 입력 {result.InputTokens} (캐시생성 {result.CacheCreationInputTokens}, " +
                    $"캐시적중 {result.CacheReadInputTokens}), 출력 {result.OutputTokens}");

                if (result.StopReason != "tool_use")
                {
                    var text = result.Text ?? "(빈 응답)";
                    history.AddAssistantBlocks(result.ContentBlocks);
                    MessageAdded?.Invoke(new ChatMessage(ChatRole.Assistant, text));
                    return;
                }

                history.AddAssistantBlocks(result.ContentBlocks);

                foreach (var (id, name, input) in result.ToolUses())
                {
                    var tool = _tools.Find(name);
                    void Log(string msg) => MessageAdded?.Invoke(new ChatMessage(ChatRole.System, msg));

                    if (tool is null)
                    {
                        history.AddToolResult(id, $"알 수 없는 도구: {name}");
                        continue;
                    }

                    StatusChanged?.Invoke(ToolStatusLabel(name));

                    if (tool is IImageTool imageTool)
                    {
                        try
                        {
                            var imageResult = await _taskRunner.RunAsync(name, () => imageTool.ExecuteImageAsync(input, Log), callCt);
                            if (imageResult.ImageBytes is { Length: > 0 } bytes)
                                history.AddToolResultWithImage(id, imageResult.Text, bytes, imageResult.MediaType);
                            else
                                history.AddToolResult(id, imageResult.Text);
                        }
                        catch (OperationCanceledException)
                        {
                            return; // 새 메시지로 대체됨 - 조용히 멈춘다.
                        }
                        catch (Exception ex)
                        {
                            history.AddToolResult(id, $"도구 실행 오류: {ex.Message}");
                        }
                        continue;
                    }

                    string resultText;
                    try
                    {
                        resultText = await _taskRunner.RunAsync(name, () => tool.ExecuteAsync(input, Log), callCt);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // 새 메시지로 대체됨 - 조용히 멈춘다.
                    }
                    catch (Exception ex)
                    {
                        resultText = $"도구 실행 오류: {ex.Message}";
                    }
                    history.AddToolResult(id, resultText);
                }
            }

            MessageAdded?.Invoke(new ChatMessage(ChatRole.Error, "작업이 너무 오래 걸려서 중단했습니다."));
        }
        finally
        {
            StatusChanged?.Invoke(null);
            if (_activeCts == cts) _activeCts = null;
            _historyLock.Release();
        }
    }

    private static string ToolStatusLabel(string name) => name switch
    {
        "see_screen" => "화면 보는 중...",
        "gui_input" => "클릭/입력하는 중...",
        "dump_window" => "창 구조 확인하는 중...",
        "screenshot_window" => "창 캡쳐하는 중...",
        "list_windows" => "창 목록 확인하는 중...",
        "run_command" => "명령 실행하는 중...",
        "files_read" => "파일 읽는 중...",
        "files_write" => "파일 쓰는 중...",
        "files_edit" => "파일 수정하는 중...",
        "browser_use" => "브라우저 조작하는 중...",
        _ => $"{name} 실행하는 중..."
    };
}
