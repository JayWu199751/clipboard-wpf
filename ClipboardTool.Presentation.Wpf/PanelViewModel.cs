using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.PanelModes;
using ClipboardTool.Domain.Search;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 面板视图模型（T05 置顶备注延迟删除）：全量条目内存持有，搜索过滤/高亮/选中走 Domain 纯规则；
/// 延迟删除的隐藏遮罩由服务经窗口注入（hiddenIds 只遮渲染，不动领域存储）；
/// 页脚六组与备注组全部由 PanelFooterRegistry 注册表推导（提示 = 行为）。
/// 状态文案为结果契约 { ok, message } 的 message 单源渲染（F12），呼出时清空（F14）。
/// </summary>
public sealed class PanelViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<HistoryEntry> _allEntries = [];
    private HashSet<string> _hiddenIds = [];
    private string _appliedQuery = string.Empty;
    private string _footerStatus = string.Empty;
    private bool _searchActive;
    private string? _noteEditingId;
    private string _noteDraft = string.Empty;
    private PanelMode _footerMode = PanelMode.Browse;
    private bool _footerCompact;

    public ObservableCollection<CardViewModel> Items { get; } = [];

    /// <summary>toast 栈（F46）：删除撤销/失败/结果提示。生命周期由窗口计时管理。</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>过滤视图中的索引（ListBox.SelectedIndex 同步）。</summary>
    public int SelectedIndex { get; set; }

    /// <summary>页脚左侧可见历史总数（F45：不是过滤结果数，延迟删除摘除的不计）。</summary>
    public string CountText => $"{VisibleTotal} 条";

    private int VisibleTotal =>
        _allEntries.Count(entry => !_hiddenIds.Contains(entry.Id));

    /// <summary>空态文案（F23）：无历史与无匹配是两个说法（legacy 判据 total===0）。</summary>
    public string EmptyText { get; private set; } = string.Empty;

    public Visibility EmptyVisible => Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>页脚右侧焦点错误（F45：覆盖普通键位组；legacy footer-error）。空串即隐藏。</summary>
    public string FooterStatus
    {
        get => _footerStatus;
        set
        {
            if (_footerStatus == value) return;
            _footerStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FooterErrorVisible));
            OnPropertyChanged(nameof(FooterHintsVisible));
        }
    }

    public Visibility FooterErrorVisible =>
        string.IsNullOrEmpty(_footerStatus) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FooterHintsVisible =>
        string.IsNullOrEmpty(_footerStatus) ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>备注编辑中的条目（null = 无编辑中的卡片）。</summary>
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

    /// <summary>页脚紧凑态（F47 窄窗收紧：≤340 DIP 时左右留白 10、文字 9.5、组距 3）。窗口按实际宽度置位。</summary>
    public bool FooterCompact
    {
        get => _footerCompact;
        set
        {
            if (_footerCompact == value) return;
            _footerCompact = value;
            OnPropertyChanged();
        }
    }

    /// <summary>备注草稿（编辑器窗口级单条；TextBox 双向绑定，字数段实时跟随）。</summary>
    public string NoteDraft
    {
        get => _noteDraft;
        set
        {
            if (_noteDraft == value) return;
            _noteDraft = value;
            OnPropertyChanged();
            if (_noteEditingId is not null)
            {
                RefreshFooterHints(_footerMode); // 备注态字数段随草稿实时刷新
            }
        }
    }

    /// <summary>
    /// 页脚提示（F45）：键组按面板模式从注册表推导，未生效的键不再展示（提示 = 行为）。
    /// 备注态附字数段（n/200，随草稿刷新）；焦点错误覆盖普通组（FooterStatus 优先）。
    /// </summary>
    public IReadOnlyList<FooterHint> FooterHints { get; private set; } = [];

    private void RefreshFooterHints(PanelMode mode = PanelMode.Browse)
    {
        FooterHints = mode switch
        {
            PanelMode.Browse or PanelMode.Search =>
                PanelFooterRegistry.BrowseChips().Select(c => new FooterHint(c.Keys, c.Action)).ToArray(),
            PanelMode.NoteEdit =>
                PanelFooterRegistry.NoteChips()
                    .Select(c => new FooterHint(c.Keys, c.Action))
                    .Append(new FooterHint(
                        $"{_noteDraft.Length}/{PanelFooterRegistry.MaxNoteLength}", string.Empty, IsCount: true))
                    .ToArray(),
            _ => [],
        };
        OnPropertyChanged(nameof(FooterHints));
    }

    public void SetFooterMode(PanelMode mode)
    {
        _footerMode = mode;
        RefreshFooterHints(mode);
    }

    /// <summary>注入延迟删除的隐藏集合（服务 HiddenIds 快照）并重放当前视图。</summary>
    public void SetHiddenIds(IReadOnlyCollection<string> ids)
    {
        _hiddenIds = [.. ids];
        ApplySearchQuery(_appliedQuery, resetSelection: false);
    }

    /// <summary>
    /// 共享缩略图缓存（T06）：App 构造注入（解码后台线程、回调归队 Dispatcher）。
    /// 缓存自身双上限与失效规则见 Infrastructure.ThumbnailCache；条目集合变化时
    /// 已消失的 id 在此立即失效（移除/裁剪/清空统一走 Reload 差量）。
    /// </summary>
    public Infrastructure.Windows.ThumbnailCache? Thumbnails { get; private set; }

    public void SetThumbnailCache(Infrastructure.Windows.ThumbnailCache cache)
    {
        Thumbnails = cache;
        OnPropertyChanged(nameof(Thumbnails));
    }

    /// <summary>整表重载：全量快照替换并重放当前查询（监听线程的变更事件已由订阅方归队 UI）；
    /// 差量出已消失的条目 id → 缩略图缓存立即失效（移除/裁剪/清空联动，F05）。</summary>
    public void Reload(IReadOnlyList<HistoryEntry> entries)
    {
        if (Thumbnails is { } cache && _allEntries.Count > 0)
        {
            var current = new HashSet<string>(entries.Select(entry => entry.Id));
            foreach (var entry in _allEntries)
            {
                if (!current.Contains(entry.Id))
                {
                    cache.Remove(entry.Id);
                }
            }
        }
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

        var filtered = SearchRules.FilterEntries(_allEntries, query)
            .Where(entry => !_hiddenIds.Contains(entry.Id)); // 延迟删除摘除的条目不在可见列表
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

        EmptyText = VisibleTotal == 0 ? "还没有剪切板内容" : "无匹配结果";
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

    private CardViewModel CreateCard(HistoryEntry entry, string query)
    {
        var isImage = entry.Type == EntryKind.Image;
        var body = entry.Type == EntryKind.Text ? entry.Text ?? string.Empty : string.Empty;
        var note = entry.Note ?? string.Empty;
        return new CardViewModel(
            entry.Id,
            body,
            note,
            FormatMeta(entry),
            entry.Pinned,
            SearchRules.Highlight(body, query),
            SearchRules.Highlight(note, query),
            isImage,
            entry.ImagePath,
            // 文件名行（F42）：图卡 mono 文件名 = 磁盘真名 <id>.png
            isImage ? entry.Id + ".png" : string.Empty);
    }

    /// <summary>Meta 行（F42：来源·时间，图钉/备注/编辑为后续段；来源缺失显示「未知来源」与 legacy 同口径）。</summary>
    private static string FormatMeta(HistoryEntry entry)
    {
        var source = entry.SourceApp?.AppName.Trim();
        if (string.IsNullOrEmpty(source))
        {
            source = "未知来源";
        }
        var time = DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAtMs).LocalDateTime.ToString("MM-dd HH:mm");
        return $"{source} · {time}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed record FooterHint(string Keys, string Action, bool IsCount = false);
}

/// <summary>
/// 卡片视图模型：Spans/NoteSpans 为正文与备注的高亮片段（F23「正文与备注命中高亮」，原文可拼回）；
/// Pinned 供 meta 行图钉（F24：HUD 只以图钉显示，无独立置顶分组标题）；
/// Body 为完整正文原样（展示层裁三行，不改正文，F01）。无查询时整段单片段、不着色。
/// 图片卡（T06，F42–F44）：IsImage 分支渲染缩略图 + mono 文件名行；正文区收起。
/// </summary>
public sealed record CardViewModel(
    string Id,
    string Body,
    string Note,
    string Meta,
    bool Pinned,
    IReadOnlyList<HighlightSpan> Spans,
    IReadOnlyList<HighlightSpan> NoteSpans,
    bool IsImage = false,
    string? ImagePath = null,
    string FileName = "")
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>toast 一条（F46）：成功绿勾/错误红叉 + 消息 + 次级 dim + 可选动作（撤销）。
/// Entering 支撑入场动画（240ms）：入栈时 true，窗口在布局就绪后置 false 触发淡入。
/// F48：Announcement 是 Narrator 播报文本（错误前缀/动作/dim 并入一句，锚在 Message
/// 文本块的 AutomationProperties.Name 上）；Animated=false（系统减少动态）时 XAML
/// 不播入场/离场过渡，直接落地/消失。</summary>
public sealed class ToastViewModel : INotifyPropertyChanged
{
    private bool _entering = true;
    private bool _leaving;

    public string Message { get; init; } = string.Empty;

    public string? Dim { get; init; }

    public bool IsError { get; init; }

    public string? ActionLabel { get; init; }

    public Action? OnAction { get; init; }

    /// <summary>Narrator 播报文本（F48）；由 ToastAnnouncement 在入栈时拼定。</summary>
    public string Announcement { get; init; } = string.Empty;

    /// <summary>是否播放过渡动画（F48：系统减少动态时为 false，入栈时按策略赋值）。</summary>
    public bool Animated { get; init; } = true;

    /// <summary>入场中（入栈后由窗口置 false，触发 240ms 淡入）。</summary>
    public bool Entering
    {
        get => _entering;
        set
        {
            if (_entering == value) return;
            _entering = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entering)));
        }
    }

    /// <summary>离场标记（淡出动画期间为 true，160ms 后由栈移除）。</summary>
    public bool Leaving
    {
        get => _leaving;
        set
        {
            if (_leaving == value) return;
            _leaving = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Leaving)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
