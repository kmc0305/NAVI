namespace LocalAssistant.Engine;

public enum TaskStatus2 { Queued, Running }

public class TaskItem
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public TaskStatus2 Status { get; set; }
}

// Serializes tool EXECUTION only (the shared UIA engine can't safely drive two
// automations at once), never the chat/Claude API calls themselves - see
// 03-orchestrator.md section 2.2. The side-panel task list binds to Items.
public class TaskRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<TaskItem> _items = new();

    public event Action? Changed;

    public IReadOnlyList<TaskItem> Items
    {
        get { lock (_items) return _items.ToList(); }
    }

    public async Task<string> RunAsync(string name, Func<Task<string>> action, CancellationToken ct)
    {
        var item = new TaskItem { Id = Guid.NewGuid(), Name = name, Status = TaskStatus2.Queued };
        lock (_items) _items.Add(item);
        Changed?.Invoke();

        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                item.Status = TaskStatus2.Running;
                Changed?.Invoke();
                return await action();
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            lock (_items) _items.Remove(item);
            Changed?.Invoke();
        }
    }
}
