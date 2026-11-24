using System.Text.Json;

namespace LocalAssistant.Engine;

public record SessionInfo(string Id, string Title, DateTimeOffset UpdatedAt);

// 세션(=오픈형 AI 채팅의 "대화방") 여러 개를 관리한다. 각 세션의 실제 대화 내용은
// 기존 ConversationStore가 그대로 쓰는 {id}.json 파일에 있고, 이 클래스는 제목/최근
// 사용 시각 같은 메타데이터만 sessions-index.json에 따로 들고 있어서 목록 UI를
// 그릴 때 전체 대화를 다 읽지 않아도 되게 한다.
public class SessionStore
{
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly List<SessionInfo> _index;

    public SessionStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "sessions-index.json");
        _index = Load();
    }

    public IReadOnlyList<SessionInfo> List() => _index.OrderByDescending(s => s.UpdatedAt).ToList();

    public string HistoryPath(string id) => Path.Combine(_dir, $"{id}.json");

    public SessionInfo Create(string title) => CreateWithId(Guid.NewGuid().ToString("N"), title);

    // 폰에서 먼저 만들어진 세션(Firestore sessions/{id} 문서)을 PC 쪽에도 같은 id로
    // 만들어서 두 쪽이 같은 세션을 가리키게 할 때 쓴다 (FirestoreBridge 전용).
    public SessionInfo CreateWithId(string id, string title)
    {
        var info = new SessionInfo(id, title, DateTimeOffset.UtcNow);
        _index.Add(info);
        Save();
        return info;
    }

    public bool Exists(string id) => _index.Any(s => s.Id == id);

    // 세션에 메시지가 오가면 호출해서 "최근 사용 시각"을 갱신한다 - List()가 이 값으로
    // 정렬하므로, 방금 대화한 세션이 사이드바 맨 위로 올라오게 하는 용도.
    public void Touch(string id)
    {
        var idx = _index.FindIndex(s => s.Id == id);
        if (idx < 0) return;
        _index[idx] = _index[idx] with { UpdatedAt = DateTimeOffset.UtcNow };
        Save();
    }

    // 기존(세션 기능이 없던 때) conversation.json을 첫 세션으로 그대로 편입시킬 때 쓴다 -
    // 새 id를 발급하되 이미 존재하는 대화 파일을 그 id 이름으로 복사해서 쓴다.
    public SessionInfo CreateFromExistingFile(string title, string existingPath)
    {
        var info = Create(title);
        File.Copy(existingPath, HistoryPath(info.Id), overwrite: true);
        return info;
    }

    public void SetTitle(string id, string title)
    {
        var idx = _index.FindIndex(s => s.Id == id);
        if (idx < 0) return;
        _index[idx] = _index[idx] with { Title = title };
        Save();
    }

    private List<SessionInfo> Load()
    {
        if (!File.Exists(_indexPath)) return new();
        var json = File.ReadAllText(_indexPath);
        return JsonSerializer.Deserialize<List<SessionInfo>>(json) ?? new();
    }

    private void Save() => File.WriteAllText(_indexPath, JsonSerializer.Serialize(_index));
}
