using Google.Cloud.Firestore;

namespace LocalAssistant.Engine;

// 04-android-windows-bridge.md: 폰 앱과 PC 상주 앱이 세션(대화방) 단위로 Firestore를
// 공유한다. 구조는 conversations/{uid}/sessions/{sessionId} 문서(제목/최근사용시각)
// 아래 messages 서브컬렉션. PC는 서비스 계정(Admin)으로 직접 붙어서 보안 규칙을
// 거치지 않는다 - PC는 이미 신뢰된 기기라는 전제.
public class FirestoreBridge : IAsyncDisposable
{
    private readonly DocumentReference _rootDoc;
    private readonly CollectionReference _sessionsCol;
    private readonly Orchestrator _orchestrator;
    private readonly IAssistantHost _host;

    private readonly object _lock = new();
    private readonly Dictionary<string, FirestoreChangeListener> _messageListeners = new();
    private FirestoreChangeListener? _sessionsListener;

    private FirestoreBridge(DocumentReference rootDoc, CollectionReference sessionsCol, Orchestrator orchestrator, IAssistantHost host)
    {
        _rootDoc = rootDoc;
        _sessionsCol = sessionsCol;
        _orchestrator = orchestrator;
        _host = host;
    }

    public static FirestoreBridge Create(FirebaseConfig config, Orchestrator orchestrator, IAssistantHost host)
    {
        var db = new FirestoreDbBuilder
        {
            ProjectId = config.ProjectId,
            JsonCredentials = File.ReadAllText(config.ServiceAccountJsonPath)
        }.Build();

        var rootDoc = db.Collection("conversations").Document(config.Uid);
        var sessionsCol = rootDoc.Collection("sessions");
        var bridge = new FirestoreBridge(rootDoc, sessionsCol, orchestrator, host);

        orchestrator.MessageAdded += bridge.OnLocalMessageAdded;
        orchestrator.StatusChanged += bridge.OnStatusChanged;
        host.SessionsChanged += bridge.OnSessionsChanged;
        host.ConnectionStatusChanged += bridge.OnConnectionStatusChanged;
        return bridge;
    }

    public void StartListening(CancellationToken ct)
    {
        // 브릿지가 뒤늦게 연결돼도(재시작 등) 폰이 바로 PC의 전체 세션 목록을 보도록
        // 지금 들고 있는 세션 메타데이터를 한 번 올려쓴다.
        OnSessionsChanged();

        _sessionsListener = _sessionsCol.Listen(snapshot =>
        {
            foreach (var change in snapshot.Changes)
            {
                if (change.ChangeType == DocumentChange.Type.Removed) continue;

                var doc = change.Document;
                var sessionId = doc.Id;
                var title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "새 대화";

                // PC/폰 어느 쪽에서 왔든: 처음 보는 세션이면 로컬에도 같은 id로
                // 만들어둔다(이미 있으면 Exists 체크로 아무 것도 안 함) - 사이드바 동기화.
                _host.EnsureSessionExists(sessionId, title);

                EnsureMessageListener(sessionId, ct);
            }
        });
    }

    private void EnsureMessageListener(string sessionId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_messageListeners.ContainsKey(sessionId)) return;

            var messages = _sessionsCol.Document(sessionId).Collection("messages");
            var listener = messages.Listen(snapshot =>
            {
                foreach (var change in snapshot.Changes)
                {
                    if (change.ChangeType != DocumentChange.Type.Added) continue;

                    var doc = change.Document;
                    var source = doc.ContainsField("source") ? doc.GetValue<string>("source") : "pc";
                    var handled = doc.ContainsField("handled") && doc.GetValue<bool>("handled");
                    if (source != "phone" || handled) continue;

                    var text = doc.GetValue<string>("text");
                    _ = doc.Reference.UpdateAsync("handled", true);

                    // 이 메시지가 도착한 세션이 PC의 현재 세션이 아니면(또는 폰이 방금
                    // 새로 만든 세션이면) 먼저 그 세션으로 전환한 뒤에 처리한다.
                    _host.EnsureAndActivateSession(sessionId, "새 대화");
                    _ = _orchestrator.HandleUserMessageAsync(text, ct, "phone");
                }
            });
            _messageListeners[sessionId] = listener;
        }
    }

    // 폰에서 온 메시지(source=="phone")는 이미 Firestore에 있으므로 다시 쓰지 않음 -
    // PC에서 직접 입력했거나(로컬) 비서가 응답한 메시지만 현재 활성 세션에 올려써서
    // 양쪽이 같은 방을 보게 한다. 도구 진행 로그(ChatRole.System, "화면 캡쳐 중" 등)는
    // 매 도구 호출마다 생겨서 너무 잦으므로 PC 전용으로 남기고 올리지 않는다 - 단,
    // ChatRole.Error(작업이 조용히 멈췄다는 걸 알리는 종료성 메시지)는 폰 사용자가
    // 전혀 알 길이 없었던 실제 버그였으므로 반드시 올려서 폰에도 보이게 한다.
    private void OnLocalMessageAdded(ChatMessage message)
    {
        if (message.Source == "phone" || message.Role == ChatRole.System) return;
        var sessionId = _host.CurrentSessionId;
        if (sessionId is null) return;

        var role = message.Role switch
        {
            ChatRole.User => "user",
            ChatRole.Error => "system",
            _ => "assistant",
        };
        var text = message.Role == ChatRole.Error ? $"⚠️ {message.Text}" : message.Text;
        var source = message.Role == ChatRole.User ? message.Source : "pc";
        var messages = _sessionsCol.Document(sessionId).Collection("messages");
        _ = messages.AddAsync(new Dictionary<string, object>
        {
            ["role"] = role,
            ["text"] = text,
            ["source"] = source,
            ["handled"] = true,
            ["timestamp"] = Timestamp.GetCurrentTimestamp()
        });
    }

    // Orchestrator.StatusChanged("Claude에게 물어보는 중...", "화면 보는 중..." 등, 라운드
    // 사이에는 null)를 현재 세션 문서에 올려써서, 폰이 자기가 보고 있는 세션의 activity
    // 필드만 보고 "지금 PC가 처리 중"인지 바로 알 수 있게 한다 - 전에는 MessageAdded(최종
    // 메시지)만 동기화해서 여러 라운드 걸리는 작업 중에는 폰 화면에 아무 변화가 없었다.
    private void OnStatusChanged(string? status)
    {
        var sessionId = _host.CurrentSessionId;
        if (sessionId is null) return;

        var sessionDoc = _sessionsCol.Document(sessionId);
        _ = sessionDoc.SetAsync(new Dictionary<string, object?>
        {
            ["activity"] = status,
        }, SetOptions.MergeAll);
    }

    // PC의 세션 목록/제목/최근사용시각이 바뀔 때마다(새 대화, 제목 자동 생성, 메시지로
    // 인한 순서 변경) Firestore의 세션 메타데이터를 다시 올려써서 폰 사이드바가 항상
    // PC와 같은 목록/순서를 보게 한다.
    private void OnSessionsChanged()
    {
        foreach (var session in _host.Sessions)
        {
            _ = _sessionsCol.Document(session.Id).SetAsync(new Dictionary<string, object>
            {
                ["title"] = session.Title,
                ["updatedAt"] = Timestamp.FromDateTimeOffset(session.UpdatedAt),
            }, SetOptions.MergeAll);
        }
    }

    // ChatWindow 맨 위의 연결 상태 표시줄과 같은 내용을 Firestore에도 올려써서, 폰에서도
    // PC 쪽 Claude/게이트웨이 연결 상태(연결됨/끊김/401 등)를 그대로 보여줄 수 있게 한다.
    private void OnConnectionStatusChanged(bool connected, string? detail)
    {
        _ = _rootDoc.SetAsync(new Dictionary<string, object?>
        {
            ["pcStatus"] = new Dictionary<string, object?>
            {
                ["connected"] = connected,
                ["detail"] = detail,
                ["updatedAt"] = Timestamp.GetCurrentTimestamp(),
            }
        }, SetOptions.MergeAll);
    }

    public async ValueTask DisposeAsync()
    {
        _orchestrator.MessageAdded -= OnLocalMessageAdded;
        _orchestrator.StatusChanged -= OnStatusChanged;
        _host.SessionsChanged -= OnSessionsChanged;
        _host.ConnectionStatusChanged -= OnConnectionStatusChanged;

        if (_sessionsListener is not null) await _sessionsListener.StopAsync();

        FirestoreChangeListener[] listeners;
        lock (_lock) { listeners = _messageListeners.Values.ToArray(); }
        foreach (var listener in listeners) await listener.StopAsync();
    }
}
