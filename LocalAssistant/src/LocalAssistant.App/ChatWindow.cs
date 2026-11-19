using System.Text.Json.Nodes;
using LocalAssistant.Engine;
using LocalAssistant.Engine.Tools;

namespace LocalAssistant.App;

// The persistent chat room (03-orchestrator.md): toggled by the global
// hotkey instead of a one-shot textbox, keeps conversation history, and
// shows the Task Runner's running/queued items in a side panel.
public class ChatWindow : Form
{
    private readonly RichTextBox _transcript = new();
    private readonly TextBox _input = new();
    private readonly Button _sendButton = new();
    private readonly ListBox _taskList = new();

    private readonly Orchestrator? _orchestrator;
    private readonly TaskRunner? _taskRunner;
    private readonly string? _setupError;

    public ChatWindow()
    {
        Text = "개인 비서";
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 560
        };

        _transcript.Dock = DockStyle.Fill;
        _transcript.ReadOnly = true;
        _transcript.BackColor = Color.White;

        var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        _input.Dock = DockStyle.Fill;
        _sendButton.Text = "전송";
        _sendButton.Dock = DockStyle.Right;
        _sendButton.Width = 70;
        inputPanel.Controls.Add(_input);
        inputPanel.Controls.Add(_sendButton);

        var chatPanel = new Panel { Dock = DockStyle.Fill };
        chatPanel.Controls.Add(_transcript);
        chatPanel.Controls.Add(inputPanel);

        var sideLabel = new Label { Text = "작업 목록", Dock = DockStyle.Top, Height = 24, Padding = new Padding(4) };
        _taskList.Dock = DockStyle.Fill;
        var sidePanel = new Panel { Dock = DockStyle.Fill };
        sidePanel.Controls.Add(_taskList);
        sidePanel.Controls.Add(sideLabel);

        split.Panel1.Controls.Add(chatPanel);
        split.Panel2.Controls.Add(sidePanel);
        Controls.Add(split);

        _sendButton.Click += (_, _) => Send();
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Send();
            }
        };

        var apiKey = ApiKeyStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _setupError = "API 키가 설정되어 있지 않습니다. 터미널에서 'LocalAssistant.Cli setkey <키>'를 먼저 실행해주세요.";
            AppendLine("시스템", _setupError);
            return;
        }

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");
        var history = new ConversationStore(Path.Combine(dataDir, "conversation.json"));
        var memory = new MemoryNoteStore(Path.Combine(dataDir, "memory.md"));
        var client = new ClaudeClient(new HttpClient(), apiKey);
        var tools = new ToolRegistry();
        tools.Register(new UpdateMemoryNoteTool(memory));
        _taskRunner = new TaskRunner();
        _orchestrator = new Orchestrator(client, tools, _taskRunner, history, memory);
        _orchestrator.MessageAdded += OnMessageAdded;
        _taskRunner.Changed += OnTasksChanged;

        RenderHistory(history.Messages);
    }

    private void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _orchestrator is null) return;
        _input.Clear();
        _ = _orchestrator.HandleUserMessageAsync(text, CancellationToken.None);
    }

    private void OnMessageAdded(ChatMessage message)
    {
        if (InvokeRequired) { BeginInvoke(() => OnMessageAdded(message)); return; }
        var label = message.Role switch
        {
            ChatRole.User => "나",
            ChatRole.Assistant => "비서",
            _ => "시스템"
        };
        AppendLine(label, message.Text);
    }

    private void OnTasksChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnTasksChanged); return; }
        if (_taskRunner is null) return;
        _taskList.Items.Clear();
        foreach (var item in _taskRunner.Items)
        {
            var status = item.Status == TaskStatus2.Running ? "실행 중" : "대기 중";
            _taskList.Items.Add($"[{status}] {item.Name}");
        }
    }

    private void RenderHistory(JsonArray messages)
    {
        foreach (var message in messages)
        {
            var role = message?["role"]?.GetValue<string>();
            var content = message?["content"];
            var text = ExtractDisplayText(content);
            if (string.IsNullOrWhiteSpace(text)) continue;
            AppendLine(role == "user" ? "나" : "비서", text);
        }
    }

    private static string ExtractDisplayText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue(out string? s)) return s ?? string.Empty;
        if (content is JsonArray blocks)
        {
            var texts = blocks
                .Select(b => b?["type"]?.GetValue<string>() == "text" ? b["text"]?.GetValue<string>() : null)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return string.Join("\n", texts);
        }
        return string.Empty;
    }

    private void AppendLine(string label, string text)
    {
        _transcript.AppendText($"[{label}] {text}\n\n");
        _transcript.SelectionStart = _transcript.Text.Length;
        _transcript.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide instead of actually closing - the tray icon keeps this window
        // alive for the lifetime of the app (toggled by the hotkey).
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
