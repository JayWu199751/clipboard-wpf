using ClipboardTool.Domain.History;

namespace ClipboardTool.Domain.Search;

/// <summary>高亮片段：Hit=false 为原文，Hit=true 为命中（渲染层着色，不改文本）。</summary>
public sealed record HighlightSpan(string Text, bool Hit);

/// <summary>导航方向（上下；到边界停住，不环绕）。</summary>
public enum NavDirection
{
    Up,
    Down,
}

/// <summary>
/// 面板视图规则（F22/F23；legacy panelView.ts 移植）：搜索过滤、命中高亮、选中项落位。
/// 纯逻辑、不依赖任何 UI 框架。三条规则的来源：README「操作」与 ADR-0004/0008——
/// 匹配规则（大小写不敏感、空格分词多词 AND、正文+备注+来源应用五字段）、
/// 结果排序（保持原始顺序，不做匹配度排序）、选中项（查询变化重置第一项由调用方执行；
/// 列表变短时拉回有效范围）。
/// </summary>
public static class SearchRules
{
    /// <summary>查询分词：去空白、大小写归一。空查询 = 不过滤。（FilterEntries / Highlight 共用）</summary>
    public static IReadOnlyList<string> QueryTerms(string query) =>
        [.. query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant())];

    /// <summary>参与匹配的字段：正文（仅文字条目）、备注、来源应用名 / 窗口标题 / exe 路径。
    /// 图片不按文件名搜索（F23），ImagePath 不进 haystack。</summary>
    private static string Haystack(HistoryEntry entry) => string.Join(' ',
        entry.Type == EntryKind.Text ? entry.Text ?? string.Empty : string.Empty,
        entry.Note ?? string.Empty,
        entry.SourceApp?.AppName ?? string.Empty,
        entry.SourceApp?.WindowTitle ?? string.Empty,
        entry.SourceApp?.ExePath ?? string.Empty);

    /// <summary>过滤：多词 AND；命中结果保持原始顺序（置顶块 → 最近使用），不按匹配度重排。
    /// 口径与 legacy 一致：haystack 与词条都归一为 Invariant 小写后做 Ordinal 包含。</summary>
    public static IReadOnlyList<HistoryEntry> FilterEntries(IReadOnlyList<HistoryEntry> entries, string query)
    {
        var terms = QueryTerms(query);
        if (terms.Count == 0)
        {
            return entries;
        }

        var matched = new List<HistoryEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var haystack = Haystack(entry).ToLowerInvariant();
            var every = true;
            foreach (var term in terms)
            {
                if (!haystack.Contains(term, StringComparison.Ordinal))
                {
                    every = false;
                    break;
                }
            }
            if (every)
            {
                matched.Add(entry);
            }
        }
        return matched;
    }

    /// <summary>高亮：逐词扫描，已命中的片段不再被后续词二次切分。
    /// 命中索引在小写串上求取（legacy lower.indexOf 同口径），切片始终取原文。</summary>
    public static IReadOnlyList<HighlightSpan> Highlight(string text, string query)
    {
        var terms = QueryTerms(query);
        if (terms.Count == 0)
        {
            return [new HighlightSpan(text, Hit: false)];
        }

        var parts = new List<HighlightSpan> { new(text, Hit: false) };
        foreach (var term in terms)
        {
            var next = new List<HighlightSpan>();
            foreach (var part in parts)
            {
                if (part.Hit)
                {
                    next.Add(part);
                    continue;
                }

                var rest = part.Text;
                var lower = rest.ToLowerInvariant();
                var found = lower.IndexOf(term, StringComparison.Ordinal);
                while (found >= 0)
                {
                    if (found > 0)
                    {
                        next.Add(new HighlightSpan(rest[..found], Hit: false));
                    }
                    next.Add(new HighlightSpan(rest.Substring(found, term.Length), Hit: true));
                    rest = rest[(found + term.Length)..];
                    lower = rest.ToLowerInvariant();
                    found = lower.IndexOf(term, StringComparison.Ordinal);
                }
                if (rest.Length > 0)
                {
                    next.Add(new HighlightSpan(rest, Hit: false));
                }
            }
            parts = next;
        }
        return parts;
    }

    /// <summary>片段拼回原文：自检「高亮不丢字符」，调用方也可降级成纯文本渲染。</summary>
    public static string SpansToText(IEnumerable<HighlightSpan> spans) =>
        string.Concat(spans.Select(span => span.Text));

    /// <summary>选中项越界的唯一修正处。空列表返回 0（配合 EntryAt 得到 null）。</summary>
    public static int ClampIndex(int index, int length)
    {
        if (length <= 0)
        {
            return 0;
        }
        return Math.Clamp(index, 0, length - 1);
    }

    /// <summary>上下移动：到首/尾后停住，不环绕。当前索引越界先夹紧再移动。</summary>
    public static int MoveIndex(int index, int length, NavDirection direction)
    {
        var current = ClampIndex(index, length);
        return direction == NavDirection.Up
            ? ClampIndex(current - 1, length)
            : ClampIndex(current + 1, length);
    }

    /// <summary>按索引取条目：越界（含负数、空列表）返回 null，调用方不必自己防下标越界。</summary>
    public static T? EntryAt<T>(IReadOnlyList<T> entries, int index)
        where T : notnull
    {
        if (index < 0 || index >= entries.Count)
        {
            return default;
        }
        return entries[index];
    }
}
