using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

public enum ChatRole { User, Assistant, System }

public record ChatMessage(ChatRole Role, string Text);

// One independent loop runs per incoming user message (03-orchestrator.md
// 2.2) - only the actual tool EXECUTION is serialized via TaskRunner, never
// the Claude API calls, so a plain question answers immediately even while
// another message's tool call is still running.
public class Orchestrator
{
    private const int MaxToolRounds = 15;
    private const string SystemPrompt =
        "너는 사용자의 윈도우 PC에서 작업을 대신 수행하는 상주형 개인 비서다. " +
        "필요하면 제공된 도구를 사용해서 실제로 작업을 실행하고, 그냥 질문이면 도구 없이 자연스럽게 대답해라. " +
        "한국어로 대답해라.";

    private readonly ClaudeClient _client;
    private readonly ToolRegistry _tools;
    private readonly TaskRunner _taskRunner;
    private readonly ConversationStore _history;

    public event Action<ChatMessage>? MessageAdded;

    public Orchestrator(ClaudeClient client, ToolRegistry tools, TaskRunner taskRunner, ConversationStore history)
    {
        _client = client;
        _tools = tools;
        _taskRunner = taskRunner;
        _history = history;
    }

    public async Task HandleUserMessageAsync(string userText, CancellationToken ct)
    {
        _history.AddUserText(userText);
        MessageAdded?.Invoke(new ChatMessage(ChatRole.User, userText));

        var toolDefs = _tools.BuildToolDefinitions();

        for (var round = 0; round < MaxToolRounds; round++)
        {
            ClaudeResult result;
            try
            {
                result = await _client.SendAsync(_history.Messages, toolDefs, SystemPrompt, ct);
            }
            catch (Exception ex)
            {
                MessageAdded?.Invoke(new ChatMessage(ChatRole.System, $"Claude 호출 실패: {ex.Message}"));
                return;
            }

            if (result.StopReason != "tool_use")
            {
                var text = result.Text ?? "(빈 응답)";
                _history.AddAssistantBlocks(result.ContentBlocks);
                MessageAdded?.Invoke(new ChatMessage(ChatRole.Assistant, text));
                return;
            }

            _history.AddAssistantBlocks(result.ContentBlocks);

            foreach (var (id, name, input) in result.ToolUses())
            {
                var tool = _tools.Find(name);
                string resultText;
                if (tool is null)
                {
                    resultText = $"알 수 없는 도구: {name}";
                }
                else
                {
                    try
                    {
                        resultText = await _taskRunner.RunAsync(
                            name,
                            () => tool.ExecuteAsync(input, msg => MessageAdded?.Invoke(new ChatMessage(ChatRole.System, msg))),
                            ct);
                    }
                    catch (Exception ex)
                    {
                        resultText = $"도구 실행 오류: {ex.Message}";
                    }
                }
                _history.AddToolResult(id, resultText);
            }
        }

        MessageAdded?.Invoke(new ChatMessage(ChatRole.System, "작업이 너무 오래 걸려서 중단했습니다."));
    }
}
