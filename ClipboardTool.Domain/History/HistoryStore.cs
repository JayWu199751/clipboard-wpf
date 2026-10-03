namespace ClipboardTool.Domain.History;

/// <summary>历史条目（T02 文字段：Id/正文/创建时间；图片、备注、来源、置顶落位归 T03/T05/T06）。
/// 时间单位为 epoch 毫秒（02-spec/02 存档契约）。</summary>
public sealed record HistoryEntry(string Id, string Text, long CreatedAtMs);

/// <summary>记录结果：Entry 为 null 表示本次没有生成条目（空正文）。</summary>
public sealed record RecordOutcome(HistoryEntry? Entry, bool Deduped);

/// <summary>
/// 历史存储的 T02 文字段（legacy history.rs 的 record_text/promote 语义）：
/// 条目身份只看内容（文字逐字符相等，F03），重复命中沿用原 id/创建时间并提升落位（F04）；
/// 空字符串不生成条目、不自动 trim 正文（F01）。
/// 时间与 id 由端口注入（Func），本类型不碰时钟与磁盘；持久化归 T03。
/// </summary>
public sealed class HistoryStore
{
    private readonly List<HistoryEntry> _entries = [];
    private readonly Func<string> _newId;
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();

    public HistoryStore(Func<string> newId, Func<long> nowMs)
    {
        _newId = newId;
        _nowMs = nowMs;
    }

    /// <summary>条目列表快照：置顶块在最前（T02 无置顶，即最近使用序）。</summary>
    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public HistoryEntry? Find(string id)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(entry => entry.Id == id);
        }
    }

    /// <summary>后台记录文字（F01/F03/F04）：空串不生成条目；重复命中沿用原条目并提升；否则插到普通块最前。</summary>
    public RecordOutcome RecordText(string text)
    {
        lock (_gate)
        {
            if (text.Length == 0)
            {
                return new RecordOutcome(null, false);
            }
            var existing = _entries.FirstOrDefault(entry => entry.Text == text);
            if (existing is not null)
            {
                PromoteLocked(existing.Id);
                return new RecordOutcome(existing, true);
            }
            var entry = new HistoryEntry(_newId(), text, _nowMs());
            _entries.Insert(0, entry);
            return new RecordOutcome(entry, false);
        }
    }

    /// <summary>使用条目时提升到普通块最前（F04；置顶两块语义归 T05）。</summary>
    public bool Promote(string id)
    {
        lock (_gate)
        {
            return PromoteLocked(id);
        }
    }

    private bool PromoteLocked(string id)
    {
        var index = _entries.FindIndex(entry => entry.Id == id);
        if (index <= 0)
        {
            return index == 0;
        }
        var entry = _entries[index];
        _entries.RemoveAt(index);
        _entries.Insert(0, entry);
        return true;
    }
}
