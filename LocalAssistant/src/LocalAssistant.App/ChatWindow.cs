using System.Text.Json.Nodes;
using LocalAssistant.Engine;

namespace LocalAssistant.App;

// The persistent chat room (03-orchestrator.md): toggled by the global
// hotkey instead of a one-shot textbox, keeps conversation history, and
// shows the Task Runner's running/queued items in a side panel.
public class ChatWindow : Form, IAssistantHost
{
    private readonly RichTextBox _transcript = new();
    private readonly TextBox _input = new();
    private readonly Button _sendButton = new();
    private readonly ListBox _taskList = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _spinnerTimer = new() { Interval = 90 };

    // Claude(게이트웨이) 연결 상태를 창 맨 위에 항상 보여준다 - 실제 메시지 호출과는 별개로
    // ClaudeClient.PingAsync(가벼운 GET 한 번)를 주기적으로 찔러서 확인한다.
    private readonly Label _connectionStatusLabel = new();
    private readonly System.Windows.Forms.Timer _connectionCheckTimer = new() { Interval = 15000 };
    private ClaudeClient? _client;

    // 오픈형 AI 채팅처럼 세션(대화방) 여러 개를 좌측 목록에서 전환한다. 각 세션의 실제
    // 메시지는 SessionStore가 들고 있는 별도 파일에 저장되고, 세션을 바꿀 때마다
    // Orchestrator.SwitchHistory로 현재 Orchestrator가 보는 대화 기록만 갈아 끼운다.
    private readonly ListBox _sessionList = new();
    private readonly Button _newChatButton = new();
    private SessionStore? _sessionStore;
    private ConversationStore? _currentHistory;
    private string? _currentSessionId;
    private string? _pendingTitleForSessionId;
    private List<string> _sessionIds = new();

    // 응답을 기다리는 동안 "지금 뭘 하고 있는지"를 보여주는 작은 로딩 표시줄 - Orchestrator가
    // Claude 호출/도구 실행 단계마다 StatusChanged로 알려주는 문구를 스피너와 함께 보여준다.
    private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private string _statusBaseText = string.Empty;
    private int _spinnerFrame;

    private readonly Orchestrator? _orchestrator;
    private readonly TaskRunner? _taskRunner;
    private readonly string? _setupError;

    // TrayApplicationContext가 FirestoreBridge(04-android-windows-bridge.md)를 같은
    // Orchestrator 인스턴스에 연결하기 위해 노출.
    public Orchestrator? Orchestrator => _orchestrator;

    // IAssistantHost 구현 - FirestoreBridge가 세션 목록/연결 상태를 읽고 바꾸는 창구.
    public string? CurrentSessionId => _currentSessionId;
    public IReadOnlyList<SessionInfo> Sessions => _sessionStore?.List() ?? Array.Empty<SessionInfo>();
    public event Action? SessionsChanged;
    public event Action<bool, string?>? ConnectionStatusChanged;

    public ChatWindow()
    {
        Text = "개인 비서";
        Width = 980;
        Height = 560;
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

        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = Color.FromArgb(245, 245, 245) };
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Padding = new Padding(8, 0, 0, 0);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Color.FromArgb(90, 90, 90);
        _statusLabel.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Italic);
        statusPanel.Controls.Add(_statusLabel);
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            _statusLabel.Text = $"{SpinnerFrames[_spinnerFrame]} {_statusBaseText}";
        };

        var chatPanel = new Panel { Dock = DockStyle.Fill };
        chatPanel.Controls.Add(_transcript);
        chatPanel.Controls.Add(inputPanel);
        chatPanel.Controls.Add(statusPanel);

        var sideLabel = new Label { Text = "작업 목록", Dock = DockStyle.Top, Height = 24, Padding = new Padding(4) };
        _taskList.Dock = DockStyle.Fill;
        var sidePanel = new Panel { Dock = DockStyle.Fill };
        sidePanel.Controls.Add(_taskList);
        sidePanel.Controls.Add(sideLabel);

        split.Panel1.Controls.Add(chatPanel);
        split.Panel2.Controls.Add(sidePanel);

        var sessionLabel = new Label { Text = "대화 목록", Dock = DockStyle.Top, Height = 24, Padding = new Padding(4) };
        _newChatButton.Text = "+ 새 대화";
        _newChatButton.Dock = DockStyle.Top;
        _newChatButton.Height = 32;
        _sessionList.Dock = DockStyle.Fill;
        var sessionPanel = new Panel { Dock = DockStyle.Fill };
        sessionPanel.Controls.Add(_sessionList);
        sessionPanel.Controls.Add(_newChatButton);
        sessionPanel.Controls.Add(sessionLabel);
        _newChatButton.Click += (_, _) => NewChat();
        _sessionList.SelectedIndexChanged += (_, _) =>
        {
            var idx = _sessionList.SelectedIndex;
            if (idx >= 0 && idx < _sessionIds.Count) SwitchToSession(_sessionIds[idx]);
        };

        var outerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 170
        };
        outerSplit.Panel1.Controls.Add(sessionPanel);
        outerSplit.Panel2.Controls.Add(split);

        var connectionPanel = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Color.FromArgb(250, 250, 250) };
        _connectionStatusLabel.Dock = DockStyle.Fill;
        _connectionStatusLabel.TextAlign = ContentAlignment.MiddleRight;
        _connectionStatusLabel.Padding = new Padding(0, 0, 10, 0);
        _connectionStatusLabel.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
        _connectionStatusLabel.Text = "● 확인 중...";
        _connectionStatusLabel.ForeColor = Color.Gray;
        connectionPanel.Controls.Add(_connectionStatusLabel);
        _connectionCheckTimer.Tick += async (_, _) => await CheckConnectionAsync();

        Controls.Add(outerSplit);
        Controls.Add(connectionPanel);

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
            _setupError = "API 키가 설정되어 있지 않습니다. 터미널에서 'LocalAssistant.Cli setkey <corebrain-gateway의 llmproxy 키>'를 먼저 실행해주세요.";
            AppendLine("시스템", _setupError);
            _connectionStatusLabel.Text = "● 비활성 (API 키 없음)";
            _connectionStatusLabel.ForeColor = Color.Gray;
            return;
        }

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");

        _sessionStore = new SessionStore(Path.Combine(dataDir, "sessions"));
        var existingSessions = _sessionStore.List();
        var legacyPath = Path.Combine(dataDir, "conversation.json");
        SessionInfo currentSession;
        if (existingSessions.Count == 0 && File.Exists(legacyPath))
        {
            // 세션 기능이 생기기 전부터 있던 단일 대화 기록을 잃어버리지 않도록 첫 세션으로 편입.
            currentSession = _sessionStore.CreateFromExistingFile("이전 대화", legacyPath);
        }
        else if (existingSessions.Count == 0)
        {
            currentSession = _sessionStore.Create("새 대화");
            _pendingTitleForSessionId = currentSession.Id;
        }
        else
        {
            currentSession = existingSessions[0]; // 가장 최근에 쓴 세션
        }
        _currentSessionId = currentSession.Id;

        var history = new ConversationStore(_sessionStore.HistoryPath(currentSession.Id));
        _currentHistory = history;
        var client = new ClaudeClient(new HttpClient(), apiKey, ClaudeEndpointStore.Load());
        _client = client;
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
        {
            var gateway = new GatewayClient(new HttpClient(), gatewayKey);
            tools.Register(new BrowserTool(gateway));
        }
        else
        {
            AppendLine("시스템", "브라우저 도구는 비활성화됨: 'LocalAssistant.Cli setgatewaykey <키>'로 corebrain-gateway 보안 키를 등록하면 켜집니다.");
        }
        _taskRunner = new TaskRunner();
        _orchestrator = new Orchestrator(client, tools, _taskRunner, history);
        _orchestrator.MessageAdded += OnMessageAdded;
        _orchestrator.StatusChanged += OnStatusChanged;
        _taskRunner.Changed += OnTasksChanged;

        RenderHistory(history.Messages);
        RefreshSessionList();

        _ = CheckConnectionAsync();
        _connectionCheckTimer.Start();
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

        if (message.Role == ChatRole.User && _pendingTitleForSessionId == _currentSessionId && _sessionStore is not null)
        {
            _sessionStore.SetTitle(_currentSessionId!, DeriveTitle(message.Text));
            _pendingTitleForSessionId = null;
        }

        // 오픈형 AI 채팅처럼 방금 대화한 세션이 사이드바 맨 위로 오도록 "최근 사용 시각"을
        // 갱신한다. 이 목록 갱신이 SessionsChanged를 울려서 FirestoreBridge가 Firestore의
        // 세션 메타데이터(제목/순서)도 같이 갱신하므로, 폰 사이드바도 똑같이 따라온다.
        if ((message.Role == ChatRole.User || message.Role == ChatRole.Assistant) && _sessionStore is not null && _currentSessionId is not null)
        {
            _sessionStore.Touch(_currentSessionId);
            RefreshSessionList();
        }

        var label = message.Role switch
        {
            ChatRole.User => "나",
            ChatRole.Assistant => "비서",
            ChatRole.Error => "오류",
            _ => "시스템"
        };
        AppendLine(label, message.Text);

        // Claude 호출이 실패하면 15초 주기를 기다리지 않고 바로 연결 상태를 다시 확인한다.
        // 401(API 키 오류)은 게이트웨이 자체는 살아있어서 PingAsync가 "연결됨"으로 오판하므로
        // (실측: 게이트웨이 컨테이너가 재생성되며 키가 바뀌었는데 연결 표시는 계속 초록이었음)
        // 따로 구분해서 바로 보여준다 - 재확인 핑을 기다리지 않는다.
        if (message.Role == ChatRole.Error && message.Text.Contains("호출 실패"))
        {
            if (message.Text.Contains(" 401"))
                SetConnectionStatus(false, "API 키 오류 (게이트웨이가 재발급했을 수 있음)");
            else
                _ = CheckConnectionAsync();
        }
    }

    private async Task CheckConnectionAsync()
    {
        if (_client is null) return;
        var connected = await _client.PingAsync(CancellationToken.None);
        if (InvokeRequired) { BeginInvoke(() => SetConnectionStatus(connected)); return; }
        SetConnectionStatus(connected);
    }

    private void SetConnectionStatus(bool connected, string? detail = null)
    {
        _connectionStatusLabel.Text = detail ?? (connected ? "● 연결됨" : "● 연결 끊김");
        if (detail is not null) _connectionStatusLabel.Text = $"● {detail}";
        _connectionStatusLabel.ForeColor = connected ? Color.FromArgb(40, 160, 70) : Color.FromArgb(200, 50, 50);
        ConnectionStatusChanged?.Invoke(connected, detail);
    }

    // "+ 새 대화" - 지금 세션은 그대로 디스크에 남겨두고, 방금까지 쓰던 대화를 요약해서
    // memory.md에 남긴 뒤(다음에 새 대화를 열 때 다시 꺼내 쓸 수 있도록) 빈 세션으로 전환한다.
    // 요약은 네트워크 호출이라 느릴 수 있어서, 새 세션으로의 전환 자체는 기다리지 않고 바로 하고
    // 요약은 백그라운드에서 끝나는 대로 memory.md에 추가한다.
    private void NewChat()
    {
        if (_orchestrator is null || _sessionStore is null || _currentHistory is null) return;

        var oldHistory = _currentHistory;
        var oldTitle = _sessionList.SelectedIndex >= 0 && _sessionList.SelectedIndex < _sessionList.Items.Count
            ? _sessionList.Items[_sessionList.SelectedIndex]?.ToString() ?? "이전 대화"
            : "이전 대화";

        var newSession = _sessionStore.Create("새 대화");
        _pendingTitleForSessionId = newSession.Id;
        var newHistory = new ConversationStore(_sessionStore.HistoryPath(newSession.Id));
        _currentHistory = newHistory;
        _currentSessionId = newSession.Id;
        _orchestrator.SwitchHistory(newHistory);

        _transcript.Clear();
        RefreshSessionList();

        if (oldHistory.Messages.Count > 0)
            _ = SummarizeOldSessionInBackgroundAsync(oldHistory, oldTitle);
    }

    private async Task SummarizeOldSessionInBackgroundAsync(ConversationStore oldHistory, string oldTitle)
    {
        if (_orchestrator is null) return;
        try
        {
            var summary = await _orchestrator.SummarizeSessionAsync(oldHistory, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(summary))
                MemoryStore.Append(oldTitle, summary);
        }
        catch
        {
            // 요약 실패해도 이미 새 대화는 시작됐으니 조용히 넘어간다.
        }
    }

    private void SwitchToSession(string id)
    {
        if (_orchestrator is null || _sessionStore is null || id == _currentSessionId) return;
        ActivateSessionCore(id);
    }

    private void ActivateSessionCore(string id)
    {
        var newHistory = new ConversationStore(_sessionStore!.HistoryPath(id));
        _currentHistory = newHistory;
        _currentSessionId = id;
        _orchestrator!.SwitchHistory(newHistory);

        _transcript.Clear();
        RenderHistory(newHistory.Messages);
    }

    // IAssistantHost: 폰에서 세션 문서만 먼저 생겼을 때(아직 메시지 없음) PC에도 같은 id로
    // 빈 세션을 만들어만 둔다 - 활성 세션은 바꾸지 않아 PC 사용자 작업을 방해하지 않는다.
    public void EnsureSessionExists(string id, string title)
    {
        if (InvokeRequired) { BeginInvoke(() => EnsureSessionExists(id, title)); return; }
        if (_sessionStore is null || _sessionStore.Exists(id)) return;
        _sessionStore.CreateWithId(id, title);
        // 폰에서 세션 문서만 먼저 생기고(아직 메시지 없음) 메시지가 나중에 올 수도 있으므로,
        // 여기서 미리 예약해둬야 첫 메시지가 도착했을 때 NewChat()과 같은 방식으로 제목이
        // 자동으로 뽑힌다 - EnsureAndActivateSession 시점엔 이미 Exists==true라 놓칠 수 있음.
        _pendingTitleForSessionId = id;
        RefreshSessionList();
    }

    // IAssistantHost: 폰에서 메시지가 도착한 세션으로 PC를 전환한다 - 로컬에 없으면
    // (폰에서 막 만든 새 세션이면) 먼저 만들고, 제목은 NewChat()과 같은 방식으로
    // 첫 메시지에서 자동으로 뽑아내도록 예약해둔다.
    public void EnsureAndActivateSession(string id, string title)
    {
        if (InvokeRequired) { BeginInvoke(() => EnsureAndActivateSession(id, title)); return; }
        if (_orchestrator is null || _sessionStore is null) return;
        if (!_sessionStore.Exists(id))
        {
            _sessionStore.CreateWithId(id, title);
            _pendingTitleForSessionId = id;
        }
        if (id != _currentSessionId) ActivateSessionCore(id);
        RefreshSessionList();
    }

    private void RefreshSessionList()
    {
        if (_sessionStore is null) return;
        var sessions = _sessionStore.List();
        _sessionIds = sessions.Select(s => s.Id).ToList();

        _sessionList.Items.Clear();
        foreach (var s in sessions) _sessionList.Items.Add(s.Title);

        var selected = _sessionIds.IndexOf(_currentSessionId ?? string.Empty);
        if (selected >= 0) _sessionList.SelectedIndex = selected;

        SessionsChanged?.Invoke();
    }

    private static string DeriveTitle(string text)
    {
        var oneLine = text.Replace("\n", " ").Trim();
        return oneLine.Length <= 24 ? oneLine : oneLine[..24] + "…";
    }

    private void OnStatusChanged(string? status)
    {
        if (InvokeRequired) { BeginInvoke(() => OnStatusChanged(status)); return; }

        if (status is null)
        {
            _spinnerTimer.Stop();
            _statusLabel.Text = string.Empty;
            return;
        }

        _statusBaseText = status;
        if (!_spinnerTimer.Enabled)
        {
            _spinnerFrame = 0;
            _statusLabel.Text = $"{SpinnerFrames[0]} {status}";
            _spinnerTimer.Start();
        }
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
