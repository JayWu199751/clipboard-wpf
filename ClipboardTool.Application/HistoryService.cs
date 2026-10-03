using ClipboardTool.Domain.History;

namespace ClipboardTool.Application;

/// <summary>
/// 历史服务的应用侧门面：持有领域 HistoryStore，串行化访问（监听线程/链路线程/UI 三方可达），
/// 记录与提升后广播条目变更事件（渲染层只消费事实）。
/// 持久化与旧档迁移归 T03（JsonStore）。
/// </summary>
public sealed class HistoryService
{
    private readonly HistoryStore _store;

    /// <summary>条目集合变化（记录、提升）。可能在任意线程触发，订阅方自行归队 UI。</summary>
    public event Action? EntriesChanged;

    public HistoryService(HistoryStore store) => _store = store;

    public IReadOnlyList<HistoryEntry> Entries => _store.Entries;

    public HistoryEntry? Find(string id) => _store.Find(id);

    /// <summary>后台记录文字（F01）：空串不生成条目；重复命中提升不新建。返回是否真的落了库。</summary>
    public bool RecordText(string text)
    {
        var outcome = _store.RecordText(text);
        if (outcome.Entry is null)
        {
            return false;
        }
        EntriesChanged?.Invoke();
        return true;
    }

    /// <summary>使用条目时提升到块首（复制并粘贴链路的 settle 步骤之一）。</summary>
    public void Promote(string id)
    {
        if (_store.Promote(id))
        {
            EntriesChanged?.Invoke();
        }
    }
}
