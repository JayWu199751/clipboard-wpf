using ClipboardTool.Domain.History;

namespace ClipboardTool.Application;

/// <summary>
/// 历史服务的应用侧门面：持有领域 HistoryStore，串行化访问（监听线程/链路线程/UI 三方可达），
/// 变更后广播条目事件（渲染层只消费事实）。
/// 持久化收敛（T03，F08）：所有会改变条目集合的操作经此服务执行——变更与落盘在同一临界区完成，
/// 并发时持久化快照不会乱序回退；启动读档按 HistoryFileRead 分派，
/// 坏档先备份再回空历史，读失败本会话禁写，绝不拿内存态覆盖从未读出的原件。
/// </summary>
public sealed class HistoryService
{
    private readonly HistoryStore _store;
    private readonly IHistoryStorage? _storage;
    // 变更、读档与落盘共用一把锁：三方可达（监听/链路/UI），持久化顺序跟随变更顺序
    private readonly object _persistGate = new();

    /// <summary>读档失败（文件在但读不出）后置位：本会话禁止回写，防止覆盖从未读出的原件。</summary>
    private bool _suppressPersist;

    /// <summary>条目集合变化（记录、提升、置顶、备注、删除、载入）。可能在任意线程触发，订阅方自行归队 UI。</summary>
    public event Action? EntriesChanged;

    public HistoryService(HistoryStore store, IHistoryStorage? storage = null)
    {
        _store = store;
        _storage = storage;
    }

    public IReadOnlyList<HistoryEntry> Entries => _store.Entries;

    public HistoryEntry? Find(string id) => _store.Find(id);

    /// <summary>启动读档（F08）：合法数组载入重建；整体坏档先备份原件再回空历史；
    /// 读失败回空历史但置位禁写。读档不回写——不拿刚读出的内容无谓覆盖原件。</summary>
    public void LoadFromStorage()
    {
        if (_storage is null)
        {
            return;
        }
        lock (_persistGate)
        {
            switch (_storage.ReadHistory())
            {
                case HistoryFileRead.Loaded loaded:
                    _suppressPersist = false;
                    _store.Load(HistoryArchive.TryParseArray(loaded.Json));
                    break;
                case HistoryFileRead.Corrupt:
                    _suppressPersist = false;
                    _storage.BackupHistory();
                    _store.Load(null);
                    break;
                case HistoryFileRead.Unreadable:
                    _suppressPersist = true;
                    _store.Load(null);
                    break;
                default:
                    _suppressPersist = false;
                    _store.Load(null);
                    break;
            }
        }
        EntriesChanged?.Invoke();
    }

    /// <summary>后台记录文字（F01）：空串不生成条目；重复命中提升不新建。落库成功即落盘并广播。
    /// sourceApp 为来源应用采集（F06，票 14），透传给新建条目（命中提升属性不变）。</summary>
    public bool RecordText(string text, SourceApp? sourceApp = null) =>
        MutateAndPersist(() => _store.RecordText(text, sourceApp).Entry is not null);

    /// <summary>记录图片（F03/F05 图片侧规则；DIB 解码与监听接线归 T06）：写盘失败不落库不落盘。</summary>
    public bool RecordImage(byte[] png, SourceApp? sourceApp = null) =>
        MutateAndPersist(() => _store.RecordImage(png, sourceApp).Entry is not null);

    /// <summary>使用条目时提升到块首（复制并粘贴链路的 settle 步骤之一）；有变化才落盘广播。</summary>
    public void Promote(string id) =>
        MutateAndPersist(() => _store.Promote(id));

    /// <summary>置顶开关（UI 归 T05）；取消置顶保留 pinnedAt，变化即落盘。</summary>
    public bool TogglePin(string id) =>
        MutateAndPersist(() => _store.TogglePin(id));

    /// <summary>保存备注（UI 归 T05）；归一化后写入，不改变条目身份。</summary>
    public bool SetNote(string id, string note) =>
        MutateAndPersist(() => _store.SetNote(id, note));

    /// <summary>删除条目（UI 归 T05）；联动删 PNG 并落盘。</summary>
    public bool Remove(string id) =>
        MutateAndPersist(() => _store.Remove(id));

    /// <summary>清空历史（托盘用户入口，T07 接线）；全部条目的 PNG 联动删除并落盘。</summary>
    public void Clear() =>
        MutateAndPersist(() =>
        {
            _store.Clear();
            return true;
        });

    /// <summary>变更与落盘同一临界区：先改内存，有变化才整表投影落盘；广播在锁外（订阅方自行归队 UI）。</summary>
    private bool MutateAndPersist(Func<bool> mutation)
    {
        bool changed;
        lock (_persistGate)
        {
            changed = mutation();
            if (changed)
            {
                PersistLocked();
            }
        }
        if (changed)
        {
            EntriesChanged?.Invoke();
        }
        return changed;
    }

    /// <summary>序列化即存档契约投影（HistoryArchive.Serialize）。调用方已持 _persistGate。</summary>
    private void PersistLocked()
    {
        if (_storage is null || _suppressPersist)
        {
            return;
        }
        try
        {
            _storage.SaveHistory(HistoryArchive.Serialize(_store.Entries));
        }
        catch (Exception)
        {
            // 落盘失败不阻断变更：内存态照常广播，原件经原子写保持完好；
            // 失败提示归 T05（toast/F46），诊断日志归 T08。重启后重读原件。
        }
    }
}
