using System.Text.Json.Nodes;

namespace ClipboardTool.Domain.History;

/// <summary>
/// 「历史」领域核心（legacy history.rs 移植）：条目身份、去重提升、置顶块/普通块两块落位、
/// 上限裁剪、备注归一化、旧档载入重建（F03/F04/F05/F07）。
/// 纯内存规则：不依赖 WPF/Win32，可脱离框架测试；文件系统效果经 IImageFileStore 端口进入，
/// 时间与 id 由 Func 端口注入，本类型不碰时钟与磁盘；持久化由调用方完成（T03 起 HistoryService+JsonStore）。
/// 领域规则出处：legacy ADR-0003（条目身份只由内容决定）、legacy ADR-0004（置顶块与普通块）。
/// </summary>
public sealed class HistoryStore
{
    public const int DefaultMaxHistory = 200;
    public const int DefaultMaxNoteLength = 200;

    private readonly int _max;
    private readonly IImageFileStore _images;
    private readonly Func<string> _newId;
    private readonly Func<long> _nowMs;
    private readonly List<HistoryEntry> _entries = [];
    // 图片规范化像素哈希缓存（ADR-0005 第 4 条）：entry.id -> sha1（图片文件创建后不会变化）
    private readonly Dictionary<string, string> _imageHashCache = [];
    private readonly object _gate = new();

    /// <summary>唯一构造入口：上限 + 图片文件端口（必供）+ 时间/id 端口。</summary>
    public HistoryStore(int max, IImageFileStore images, Func<string> newId, Func<long> nowMs)
    {
        _max = max;
        _images = images;
        _newId = newId;
        _nowMs = nowMs;
    }

    /// <summary>条目列表快照：置顶块在最前（按 pinnedAt 新→旧），普通块随后（最近使用序）。</summary>
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

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public HistoryEntry? Find(string id)
    {
        lock (_gate)
        {
            return FindLocked(id);
        }
    }

    /// <summary>备注归一化（F07 存储侧）：去首尾空白，按 Unicode 标量（EnumerateRunes）截断 200——
    /// 与旧存储 chars().take(200) 语义一致，emoji/代理对按整标量取舍，不损坏 UTF-16。</summary>
    public string NormalizeNote(string note)
    {
        var trimmed = note.Trim();
        if (trimmed.EnumerateRunes().Count() <= DefaultMaxNoteLength)
        {
            return trimmed;
        }
        var sb = new System.Text.StringBuilder();
        var taken = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (taken >= DefaultMaxNoteLength)
            {
                break;
            }
            sb.Append(rune);
            taken++;
        }
        return sb.ToString();
    }

    /// <summary>文本复制进历史（F01/F03/F04）：身份命中（全文逐字符相等）→ 提升且属性不变；
    /// 否则新建（id/时间端口注入）插到普通块最前并裁剪。空串不落库。</summary>
    public RecordOutcome RecordText(string text, SourceApp? sourceApp = null)
    {
        lock (_gate)
        {
            if (text.Length == 0)
            {
                return new RecordOutcome(null, false);
            }
            var index = _entries.FindIndex(entry => entry.Type == EntryKind.Text && entry.Text == text);
            if (index >= 0)
            {
                var id = _entries[index].Id;
                PromoteLocked(id);
                return new RecordOutcome(FindLocked(id), true);
            }
            var entry = new HistoryEntry(
                _newId(), EntryKind.Text, text, ImagePath: null, _nowMs(),
                sourceApp, Pinned: false, PinnedAtMs: 0, Note: string.Empty);
            InsertNewLocked(entry);
            return new RecordOutcome(entry, false);
        }
    }

    /// <summary>图片复制进历史（F03/F05 图片侧规则；解码与编码归 Infrastructure）：
    /// 身份按规范化像素 SHA-1（ADR-0005 第 4 条，经端口取哈希——旧版与新版编码同像素同身份）。
    /// 命中 → 提升且不写新文件；未命中 → 经端口写盘后插入。
    /// 写盘失败返回 Entry null，调用方不更新轮询基线（下次轮询重试）。</summary>
    public RecordOutcome RecordImage(byte[] png, SourceApp? sourceApp = null)
    {
        lock (_gate)
        {
            if (png.Length == 0)
            {
                return new RecordOutcome(null, false);
            }
            var hash = _images.HashPng(png);
            var match = MatchImageHashLocked(hash);
            if (match is not null)
            {
                var id = match.Id;
                PromoteLocked(id);
                return new RecordOutcome(FindLocked(id), true, hash);
            }
            var newId = _newId();
            var imagePath = _images.SavePng(png, newId);
            if (imagePath is null)
            {
                return new RecordOutcome(null, false, hash);
            }
            var entry = new HistoryEntry(
                newId, EntryKind.Image, Text: null, imagePath, _nowMs(),
                sourceApp, Pinned: false, PinnedAtMs: 0, Note: string.Empty);
            _imageHashCache[newId] = hash;
            InsertNewLocked(entry);
            return new RecordOutcome(entry, false, hash);
        }
    }

    /// <summary>图片身份匹配（F03）：按注入端口取文件规范化哈希（带 id 缓存）与给定哈希比对；
    /// 空哈希永不命中。测试与 T06 记录路径共用。</summary>
    internal HistoryEntry? MatchImageHash(string hash)
    {
        lock (_gate)
        {
            return MatchImageHashLocked(hash);
        }
    }

    /// <summary>使用条目时提升（F04）：普通条目移到普通块最前；置顶条目刷新 pinnedAt 并移到置顶块最前。
    /// 备注/来源/创建时间等属性保持不变。</summary>
    public bool Promote(string id)
    {
        lock (_gate)
        {
            return PromoteLocked(id);
        }
    }

    /// <summary>置顶开关（F24 归属 T05，规则在此收敛）：置顶 → 刷新 pinnedAt 移到置顶块最前；
    /// 取消置顶 → 回到普通块最前（pinnedAt 保留，存档契约）。</summary>
    public bool TogglePin(string id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0)
            {
                return false;
            }
            var entry = _entries[index];
            _entries.RemoveAt(index);
            if (entry.Pinned)
            {
                entry = entry with { Pinned = false };
                _entries.Insert(PinnedCountLocked(), entry);
            }
            else
            {
                entry = entry with { Pinned = true, PinnedAtMs = _nowMs() };
                _entries.Insert(0, entry);
            }
            return true;
        }
    }

    /// <summary>删除条目（F05）：同步删除关联 PNG 并使图片缓存失效。</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0)
            {
                return false;
            }
            var removed = _entries[index];
            _entries.RemoveAt(index);
            DropImageFileLocked(removed);
            return true;
        }
    }

    /// <summary>清空历史（托盘用户入口触发，T07 接线）：全部条目的 PNG 联动删除。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                DropImageFileLocked(entry);
            }
            _entries.Clear();
        }
    }

    /// <summary>保存备注（F07）：空字符串等同删除；归一化后写入，不改变条目身份。</summary>
    public bool SetNote(string id, string note)
    {
        var normalized = NormalizeNote(note);
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0)
            {
                return false;
            }
            _entries[index] = _entries[index] with { Note = normalized };
            return true;
        }
    }

    /// <summary>从旧档 JSON 载入（F08）：过滤无效/丢文件条目、归一化缺省字段、
    /// 重建置顶块（按 pinnedAt 新→旧，与载入顺序无关；普通块保持存档顺序即最近使用序）、按上限裁剪。
    /// raw 为解析后的 JSON 数组（HistoryArchive.TryParseArray），null/空 → 空历史。</summary>
    public void Load(IReadOnlyList<JsonNode?>? raw)
    {
        lock (_gate)
        {
            _imageHashCache.Clear();
            var list = new List<HistoryEntry>();
            if (raw is not null)
            {
                foreach (var element in raw)
                {
                    var entry = HistoryArchive.TryReadEntry(element);
                    if (entry is null)
                    {
                        continue; // 单条损坏只丢该条
                    }
                    switch (entry.Type)
                    {
                        case EntryKind.Image:
                            if (entry.ImagePath is null || !_images.FileExists(entry.ImagePath))
                            {
                                continue; // 图片路径缺失或文件丢失 → 跳过该条
                            }
                            break;
                        case EntryKind.Text:
                            if (entry.Text is null)
                            {
                                continue;
                            }
                            break;
                    }
                    list.Add(entry with { Note = NormalizeNote(entry.Note) });
                }
            }
            _entries.Clear();
            _entries.AddRange(list);
            SortLocked();
            TrimLocked();
        }
    }

    private HistoryEntry? FindLocked(string id) => _entries.FirstOrDefault(entry => entry.Id == id);

    private HistoryEntry? MatchImageHashLocked(string hash)
    {
        if (hash.Length == 0)
        {
            return null;
        }
        var candidates = _entries
            .Where(entry => entry.Type == EntryKind.Image && entry.ImagePath is not null)
            .Select(entry => (entry.Id, Path: entry.ImagePath!))
            .ToList();
        foreach (var (id, path) in candidates)
        {
            if (!_imageHashCache.TryGetValue(id, out var cached))
            {
                cached = _images.HashFile(path);
                if (cached.Length > 0)
                {
                    _imageHashCache[id] = cached;
                }
            }
            if (cached == hash)
            {
                return FindLocked(id);
            }
        }
        return null;
    }

    private bool PromoteLocked(string id)
    {
        var index = _entries.FindIndex(entry => entry.Id == id);
        if (index < 0)
        {
            return false;
        }
        var entry = _entries[index];
        _entries.RemoveAt(index);
        if (entry.Pinned)
        {
            entry = entry with { PinnedAtMs = _nowMs() };
            _entries.Insert(0, entry);
        }
        else
        {
            _entries.Insert(PinnedCountLocked(), entry);
        }
        return true;
    }

    private int PinnedCountLocked()
    {
        var count = 0;
        while (count < _entries.Count && _entries[count].Pinned)
        {
            count++;
        }
        return count;
    }

    private void InsertNewLocked(HistoryEntry entry)
    {
        // 新内容永远插在置顶块之后、普通块最前，然后按上限裁剪
        _entries.Insert(PinnedCountLocked(), entry);
        TrimLocked();
    }

    /// <summary>排序规则（F04）：置顶条目固定在最前（按置顶时间新→旧，稳定排序），
    /// 之后按原数组顺序（即最近使用新→旧）。</summary>
    private void SortLocked()
    {
        var ordered = _entries
            .Where(entry => entry.Pinned)
            .OrderByDescending(entry => entry.PinnedAtMs)
            .Concat(_entries.Where(entry => !entry.Pinned))
            .ToList();
        _entries.Clear();
        _entries.AddRange(ordered);
    }

    private void DropImageFileLocked(HistoryEntry entry)
    {
        _imageHashCache.Remove(entry.Id);
        if (entry.Type == EntryKind.Image && entry.ImagePath is not null)
        {
            _images.RemoveFile(entry.ImagePath);
        }
    }

    /// <summary>历史上限裁剪（F05）：上限含置顶，置顶不是无限容量豁免——
    /// 超出先裁最旧普通条目，全部置顶时才裁最旧置顶条目；裁剪联动删 PNG 与缓存失效。</summary>
    private void TrimLocked()
    {
        while (_entries.Count > _max)
        {
            var index = -1;
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (!_entries[i].Pinned)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                index = _entries.Count - 1;
            }
            var removed = _entries[index];
            _entries.RemoveAt(index);
            DropImageFileLocked(removed);
        }
    }
}
