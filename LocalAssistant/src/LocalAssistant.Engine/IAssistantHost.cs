namespace LocalAssistant.Engine;

// FirestoreBridge가 ChatWindow의 세션 목록/연결 상태를 다루기 위한 최소 창구.
// Engine은 App(WinForms)을 참조하지 않으므로, ChatWindow가 이 인터페이스를 구현해서
// 넘겨주는 방식으로 양쪽을 연결한다 (04-android-windows-bridge.md 세션 동기화).
public interface IAssistantHost
{
    string? CurrentSessionId { get; }
    IReadOnlyList<SessionInfo> Sessions { get; }

    // PC의 세션 목록/제목이 바뀔 때마다(새 대화, 제목 자동 생성 등) 울려서
    // FirestoreBridge가 Firestore의 sessions 메타데이터를 다시 올려쓰게 한다.
    event Action? SessionsChanged;

    // ChatWindow의 연결 상태 표시줄(_connectionStatusLabel)이 바뀔 때마다 울려서
    // FirestoreBridge가 같은 상태를 Firestore에 올려써 폰 쪽도 보게 한다.
    event Action<bool, string?>? ConnectionStatusChanged;

    // id로 된 로컬 세션이 없으면 title로 새로 만들어만 두고(활성화는 안 함) - 폰에서
    // 새 세션 문서만 생성되고 아직 메시지는 없는 경우에 쓴다. 이미 있으면 아무것도 안 함.
    void EnsureSessionExists(string id, string title);

    // id로 된 로컬 세션이 없으면 만들고, PC의 활성 세션을 그 세션으로 전환한다 - 폰에서
    // 메시지가 도착했는데 그 세션이 PC의 현재 세션이 아닐 때 쓴다.
    void EnsureAndActivateSession(string id, string title);
}
