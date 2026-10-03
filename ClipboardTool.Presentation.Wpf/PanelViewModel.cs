using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.PanelModes;
using ClipboardTool.Domain.Search;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 面板视图模型（T04 四态键位与搜索）：全量条目内存持有，搜索过滤/高亮/选中落位走 Domain 纯规则。
/// 条目集合由 EntriesChanged 整表重载并重放当前查询（增量刷新归后续工单）；
/// 状态文案为结果契约 { ok, message } 的 message 单源渲染（F12），呼出时清空（F14）。
/// </summary>
public sealed class PanelViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<HistoryEntry> _allEntries = [];
    private string _appliedQuery = string.Empty;
    private string _statusText = string.Empty;
    private bool _searchActive;
    private string? _noteEditingId;

    public ObservableCollection<CardViewModel> Items { get; } = [];

    /// <summary>过滤视图中的索引（ListBox.SelectedIndex 同步）。</summary>
    public int SelectedIndex { get; set; }

    public string CountText => $"{_allEntries.Count} 条";

    /// <summary>空态文案（F23）：无历史与无匹配是两个说法。</summary>
    public string EmptyText { get; private set; } = string.Empty;

    public Visibility EmptyVisible => Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>焦点错误/结果提示（toast 雏形；F46 的完整 toast 归 T05）。空串即隐藏。</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusVisible));
        }
    }

    public Visibility StatusVisible => string.IsNullOrEmpty(_statusText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>搜索态（F20）：输入框可编辑、清除按钮出现、键名 chip 收起。置位由 panel:key 事件驱动。</summary>
    public bool SearchActive
    {
        get => _searchActive;
        set
        {
            if (_searchActive == value) return;
            _searchActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaceholderText));
            OnPropertyChanged(nameof(SearchKeyChipVisible));
        }
    }

    public string PlaceholderText => _searchActive ? "搜索文字、备注或来源应用…" : "搜索剪贴板…";

    /// <summary>搜索键 chip：只在非搜索态展示（「提示 = 行为」，文本由键位注册表推导）。</summary>
    public Visibility SearchKeyChipVisible => _searchActive ? Visibility.Collapsed : Visibility.Visible;

    public string SearchKeyText { get; } = PanelNavKeys.Shortcuts
        .First(s => s.Action == NavAction.Search).Combo.DisplayName;

    /// <summary>备注编辑占位（本票只做状态落位；完整编辑器规则归 T05）。null = 无编辑中的卡片。</summary>
    public string? NoteEditingId
    {
        get => _noteEditingId;
        set
        {
            if (_noteEditingId == value) return;
            _noteEditingId = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 页脚提示（F45）：键组按面板模式从注册表推导，未生效的键不再展示（提示 = 行为）。
    /// 字形沿用原型注册表（ArrowUp→↑、ArrowDown→↓、Enter→⏎、Escape→Esc、z→Z、b→B、Delete→Del）。
    /// </summary>
    public IReadOnlyList<FooterHint> FooterHints { get; private set; } = BrowseHints;

    private static readonly FooterHint[] BrowseHints =
    [
        new("↑↓", "选择"),
        new("⏎", "复制"),
        new("Z", "置顶"),
        new("B", "备注"),
        new("Del", "删除"),
        new("Esc", "隐藏"),
    ];

    private static readonly FooterHint[] SearchHints =
    [
        new("↑↓", "选择"),
        new("⏎", "复制"),
        new("Esc", "返回"),
    ];

    private static readonly FooterHint[] NoteHints =
    [
        new("⏎", "保存"),
        new("Esc", "取消"),
    ];

    public void SetFooterMode(PanelMode mode)
    {
        FooterHints = mode switch
        {
            PanelMode.Browse => BrowseHints,
            PanelMode.Search => SearchHints,
            PanelMode.NoteEdit => NoteHints,
            _ => [],
        };
        OnPropertyChanged(nameof(FooterHints));
    }

    /// <summary>整表重载：全量快照替换并重放当前查询（监听线程的变更事件已由订阅方归队 UI）。</summary>
    public void Reload(IReadOnlyList<HistoryEntry> entries)
    {
        _allEntries = entries;
        ApplySearchQuery(_appliedQuery, resetSelection: false);
    }

    /// <summary>
    /// 应用已生效查询（防抖提交后调用）：多词 AND 过滤、正文高亮；
    /// 查询变化选中第一项（F23），结果缩短夹紧索引；空态文案区分「无历史/无匹配」。
    /// </summary>
    public void ApplySearchQuery(string query, bool resetSelection = true)
    {
        var changed = query != _appliedQuery;
        _appliedQuery = query;

        var filtered = SearchRules.FilterEntries(_allEntries, query);
        var previousIndex = SelectedIndex;

        Items.Clear();
        foreach (var entry in filtered)
        {
            Items.Add(CreateCard(entry, query));
        }

        if (changed && resetSelection)
        {
            SelectedIndex = SearchRules.ClampIndex(0, Items.Count); // 查询变化选中第一项
        }
        else
        {
            SelectedIndex = SearchRules.ClampIndex(previousIndex, Items.Count); // 结果缩短夹紧索引
        }

        EmptyText = _allEntries.Count == 0 ? "还没有剪切板内容" : "无匹配结果";
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyVisible));
    }
    /// <summary>↑↓ 移动选中项（边界停住，不环绕）。</summary>
    public void MoveSelection(NavDirection direction)
    {
        SelectedIndex = SearchRules.MoveIndex(SelectedIndex, Items.Count, direction);
        OnPropertyChanged(nameof(SelectedIndex));
    }

    /// <summary>当前选中条目 id（无选中或越界返回 null）。</summary>
    public string? SelectedItemId =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex].Id : null;

    private static CardViewModel CreateCard(HistoryEntry entry, string query)
    {
        var body = entry.Type == EntryKind.Text ? entry.Text ?? string.Empty : string.Empty;
        var note = entry.Note ?? string.Empty;
        return new CardViewModel(
            entry.Id,
            body,
            note,
            FormatMeta(entry),
            SearchRules.Highlight(body, query),
            SearchRules.Highlight(note, query));
    }

    /// <summary>Meta 行（T02 只有创建时间；来源归 T03、图钉/备注归 T05）。</summary>
    private static string FormatMeta(HistoryEntry entry) =>
        DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAtMs).LocalDateTime.ToString("MM-dd HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed record FooterHint(string Keys, string Action);
}

/// <summary>
/// 卡片视图模型：Spans/NoteSpans 为正文与备注的高亮片段（F23「正文与备注命中高亮」，原文可拼回；
/// 备注在卡片上的可见渲染随 T05 落地）；Body 为完整正文原样（展示层裁三行，不改正文，F01）。
/// 无查询时整段单片段、不着色。
/// </summary>
public sealed record CardViewModel(
    string Id,
    string Body,
    string Note,
    string Meta,
    IReadOnlyList<HighlightSpan> Spans,
    IReadOnlyList<HighlightSpan> NoteSpans);
