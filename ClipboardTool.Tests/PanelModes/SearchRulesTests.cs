using ClipboardTool.Domain.History;
using ClipboardTool.Domain.Search;

namespace ClipboardTool.Tests.PanelModes;

/// <summary>
/// 搜索过滤/命中高亮/选中项规则（F22/F23；legacy panelView.ts 移植）：
/// 匹配规则（大小写不敏感、空格分词多词 AND、正文+备注+来源五字段）、
/// 结果排序（保持原始顺序，不做匹配度排序）、选中项（越界夹紧、边界停住）。
/// </summary>
public class SearchRulesTests
{
    private static HistoryEntry Text(string id, string text, string note = "", SourceApp? source = null) =>
        new(id, EntryKind.Text, text, null, CreatedAtMs: 1_000, source, Pinned: false, PinnedAtMs: 0, note);

    private static HistoryEntry Image(string id, string path, string note = "", SourceApp? source = null) =>
        new(id, EntryKind.Image, null, path, CreatedAtMs: 1_000, source, Pinned: false, PinnedAtMs: 0, note);

    // —— 查询分词 ——

    [Fact]
    public void QueryTerms_TrimsSplitsLowercases()
    {
        Assert.Equal(["foo", "bar"], SearchRules.QueryTerms("  Foo \t BAR  "));
    }

    [Fact]
    public void QueryTerms_EmptyOrWhitespace_YieldsNothing()
    {
        Assert.Empty(SearchRules.QueryTerms(""));
        Assert.Empty(SearchRules.QueryTerms("   \t "));
    }

    // —— 过滤：多词 AND、字段覆盖、保序 ——

    [Fact]
    public void FilterEntries_EmptyQuery_ReturnsAll()
    {
        var entries = new[] { Text("a", "hello"), Image("b", "C:\\x.png") };

        var result = SearchRules.FilterEntries(entries, "");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterEntries_MultiWordAnd()
    {
        var entries = new[]
        {
            Text("a", "hello world"),
            Text("b", "hello there"),
            Text("c", "world peace"),
        };

        var result = SearchRules.FilterEntries(entries, "hello world");

        Assert.Equal(["a"], result.Select(e => e.Id));
    }

    [Fact]
    public void FilterEntries_CaseInsensitive()
    {
        var entries = new[] { Text("a", "Hello World"), Text("b", "中文备注") };

        Assert.Equal(["a"], SearchRules.FilterEntries(entries, "HELLO").Select(e => e.Id));
        // 中文查询：分词后原样包含匹配
        Assert.Equal(["b"], SearchRules.FilterEntries(entries, "中文").Select(e => e.Id));
    }

    [Fact]
    public void FilterEntries_MatchesAcrossFields()
    {
        var entries = new[]
        {
            Text("body", "普通", source: null),
            Text("note", "普通", note: "命中点在备注里"),
            Text("app", "普通", source: new SourceApp(AppName: " VSCode ")),
            Text("title", "普通", source: new SourceApp(WindowTitle: "main.rs - 编辑器")),
            Text("exe", "普通", source: new SourceApp(ExePath: "C:\\app\\code.exe")),
        };

        Assert.Equal("note", SearchRules.FilterEntries(entries, "备注").Single().Id);
        Assert.Equal("app", SearchRules.FilterEntries(entries, "vscode").Single().Id);
        Assert.Equal("title", SearchRules.FilterEntries(entries, "main.rs").Single().Id);
        Assert.Equal("exe", SearchRules.FilterEntries(entries, "code.exe").Single().Id);
    }

    [Fact]
    public void FilterEntries_ImageNotSearchedByPath()
    {
        // F23：图片不按文件名搜索（ImagePath 不进 haystack），但备注与来源参与匹配
        var entries = new[]
        {
            Image("img", "C:\\shots\\screenshot-01.png"),
            Image("img-note", "C:\\shots\\screenshot-02.png", note: "登录页截图"),
        };

        Assert.Empty(SearchRules.FilterEntries(entries, "screenshot"));
        Assert.Equal(["img-note"], SearchRules.FilterEntries(entries, "登录页").Select(e => e.Id));
    }

    [Fact]
    public void FilterEntries_PreservesHistoryOrder()
    {
        // 结果保持历史顺序（置顶块 → 最近使用），不按匹配度重排
        var entries = new[]
        {
            Text("old", "alpha beta"),
            Text("new", "alpha alpha"),
            Text("newest", "alpha gamma"),
        };

        var result = SearchRules.FilterEntries(entries, "alpha");

        Assert.Equal(["old", "new", "newest"], result.Select(e => e.Id));
    }

    // —— 高亮：逐词扫描、已命中片段不再二次切分、原文可拼回 ——

    [Fact]
    public void Highlight_NoTerms_SingleUnhitSpan()
    {
        var spans = SearchRules.Highlight("hello", "");

        var span = Assert.Single(spans);
        Assert.False(span.Hit);
        Assert.Equal("hello", span.Text);
    }

    [Fact]
    public void Highlight_CaseInsensitive_KeepsOriginalText()
    {
        var spans = SearchRules.Highlight("Hello World", "world").ToList();

        Assert.Equal(2, spans.Count);
        Assert.Equal("Hello ", spans[0].Text);
        Assert.False(spans[0].Hit);
        Assert.Equal("World", spans[1].Text);
        Assert.True(spans[1].Hit);
        // 原文可拼回（高亮不丢字符）
        Assert.Equal("Hello World", SearchRules.SpansToText(spans));
    }

    [Fact]
    public void Highlight_MultipleTerms_HitSegmentsNotResplit()
    {
        // 已命中的片段不再被后续词二次切分（legacy highlight 的核心规则）
        var spans = SearchRules.Highlight("abc abc", "abc bca").ToList();

        // 第一轮 "abc"：[abc(hit), ' '(0), abc(hit)]；第二轮 "bca" 只切未命中片段
        Assert.Equal("abc abc", SearchRules.SpansToText(spans));
        Assert.Equal(3, spans.Count);
        Assert.Equal([true, false, true], spans.Select(s => s.Hit));
    }

    [Fact]
    public void Highlight_ChineseText()
    {
        var spans = SearchRules.Highlight("搜索中文内容", "中文").ToList();

        Assert.Equal("搜索中文内容", SearchRules.SpansToText(spans));
        Assert.Contains(spans, s => s.Hit && s.Text == "中文");
    }

    // —— 选中项规则 ——

    [Fact]
    public void ClampIndex_EmptyList_ReturnsZero()
    {
        Assert.Equal(0, SearchRules.ClampIndex(3, length: 0));
    }

    [Fact]
    public void ClampIndex_PullsBackOutOfRange()
    {
        Assert.Equal(0, SearchRules.ClampIndex(-5, length: 3));
        Assert.Equal(2, SearchRules.ClampIndex(10, length: 3));
        Assert.Equal(1, SearchRules.ClampIndex(1, length: 3));
    }

    [Fact]
    public void MoveIndex_StopsAtBoundaries()
    {
        // 上下到边界停住，不环绕
        Assert.Equal(0, SearchRules.MoveIndex(0, 3, NavDirection.Up));
        Assert.Equal(2, SearchRules.MoveIndex(2, 3, NavDirection.Down));
        Assert.Equal(1, SearchRules.MoveIndex(0, 3, NavDirection.Down));
        Assert.Equal(1, SearchRules.MoveIndex(2, 3, NavDirection.Up));
    }

    [Fact]
    public void MoveIndex_ClampsCurrentFirst()
    {
        // 当前索引越界（结果缩短后未夹紧）先拉回有效范围再移动：10→夹到2→上移到1
        Assert.Equal(1, SearchRules.MoveIndex(10, 3, NavDirection.Up));
        // -1→夹到0→下移到1
        Assert.Equal(1, SearchRules.MoveIndex(-1, 3, NavDirection.Down));
        // 空列表：动不了，仍是 0
        Assert.Equal(0, SearchRules.MoveIndex(0, 0, NavDirection.Down));
    }

    [Fact]
    public void EntryAt_OutOfRangeReturnsNull()
    {
        var entries = new[] { "a", "b" };

        Assert.Equal("a", SearchRules.EntryAt(entries, 0));
        Assert.Equal("b", SearchRules.EntryAt(entries, 1));
        Assert.Null(SearchRules.EntryAt(entries, -1));
        Assert.Null(SearchRules.EntryAt(entries, 2));
        Assert.Null(SearchRules.EntryAt<string>([], 0));
    }
}
