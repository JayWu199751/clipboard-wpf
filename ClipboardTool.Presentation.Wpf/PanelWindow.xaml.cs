using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipboardTool.Application;
using ClipboardTool.Domain.Geometry;
using ClipboardTool.Domain.PanelModes;
using ClipboardTool.Domain.Search;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 呼出面板窗（本仓库 ADR-0002）：无边框透明圆角壳，常驻 WS_EX_TOOLWINDOW，
/// 浏览态常驻 WS_EX_NOACTIVATE 不抢前台；停靠=屏外驻留不销毁，同 HWND 再呼出落地。
/// 呼出几何按 F15/F16 由 PanelGeometry 计算，落地回读验证走 WindowPlacer（legacy landing_verdict）。
/// T04：四态键位由 Application.PanelCoordinator 状态机推导（本窗只渲染事实）；
/// 搜索头/选中/空态/高亮走 PanelViewModel + Domain 纯规则；IME composition 上报协调器（F21）。
/// </summary>
public partial class PanelWindow : Window
{
    private const double FallbackMonitorHeightPx = 1080;
    private const double FallbackDpiScale = 1.0;

    /// <summary>外壳圆角与描边（与 PanelWindow.xaml 保持一致）；内容裁剪半径 = 圆角 − 描边 − 让位。</summary>
    private const double ShellCornerRadiusDip = 36;
    private const double ShellBorderThicknessDip = 2;

    /// <summary>
    /// 裁剪弧相对描边内缘再让位 1 DIP：裁剪与描边内曲线重合时，裁剪的抗锯齿边缘会把
    /// 页脚底色混进描边内圈像素，底部圆角观感变细；让位后描边 fringe 落在与页脚同色的
    /// 外壳底色上，圆角粗细四角一致。
    /// </summary>
    private const double ShellClipInsetDip = 1;

    private readonly ScreenMetricsProvider _screens = new();
    private readonly PanelViewModel _viewModel;
    private readonly SearchDebouncer _searchDebouncer;
    private bool _docked = true;
    private bool _syncingSelection;
    private bool _suppressQueryEvents; // 程序性写查询文本（重置/清除）时不进防抖
    private bool _composing;           // 搜索输入框 IME 组合中的本地镜像（Esc 取消组合的兜底恢复用）

    /// <summary>双击卡片：复制并粘贴该条目（F11 第二入口；单击仅选中）。Enter 键路径复用同一事件。</summary>
    public event Action<string>? CardPasteRequested;

    /// <summary>浏览态点击搜索井（进搜索，F20/F22）。</summary>
    public event Action? SearchActivationRequested;

    /// <summary>IME composition 开始/结束（F21；经宿主上报协调器做导航键让位）。</summary>
    public event Action<bool>? CompositionChanged;

    /// <summary>备注编辑占位退出（Enter/Esc；完整编辑器归 T05）。</summary>
    public event Action? NoteEditExitRequested;

    public PanelWindow()
    {
        InitializeComponent();
        _viewModel = new PanelViewModel();
        DataContext = _viewModel;
        _searchDebouncer = new SearchDebouncer(new DispatcherDelayScheduler(Dispatcher));
        _searchDebouncer.QueryCommitted += query => _viewModel.ApplySearchQuery(query);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SourceInitialized += (_, _) =>
        {
            FocusAdapter.EnsureToolWindow(this);
            FocusAdapter.SetNoActivate(this, on: true);
            Dock(); // 初始停靠：显示前先落位屏外，避免闪现
        };

        // Esc 停靠是浏览态全局键（F18，让位模型）：由协调器差量注册后经 HandlePanelKey 到达
        HookCompositionEvents();
        SearchBox.PreviewKeyDown += OnSearchBoxPreviewKeyDown;
    }

    public bool IsDocked => _docked;

    private IntPtr Hwnd => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>
    /// 内层三行 Grid 按内圆角裁剪（对齐原型的 overflow:hidden + border-radius）。
    /// WPF Border 绘制顺序为背景→描边→子内容：页脚等通栏背景的方角会伸进描边内圆角的
    /// 角落扇区、从内侧盖掉弧线。ShellRows 的原点正是描边内缘，r34 裁剪弧与描边内曲线
    /// （CornerRadius−Thickness，圆心再内缩 Thickness）完全重合，既挡住越线又不伤描边。
    /// </summary>
    private void OnShellRowsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateShellClip();

    private void UpdateShellClip()
    {
        var radius = ShellCornerRadiusDip - ShellBorderThicknessDip - ShellClipInsetDip;
        var clip = new RectangleGeometry(new Rect(new Point(0, 0), ShellRows.RenderSize), radius, radius);
        clip.Freeze();
        ShellRows.Clip = clip;
    }

    /// <summary>再次呼出键：停靠时呼出，落地时停靠。</summary>
    public void ToggleSummon()
    {
        if (_docked)
        {
            Summon();
        }
        else
        {
            Dock();
        }
    }

    /// <summary>整表重载历史条目（历史服务的事件已归队 UI 线程）；重放当前查询。</summary>
    public void ReloadEntries(IReadOnlyList<Domain.History.HistoryEntry> entries)
    {
        _viewModel.Reload(entries);
        SyncSelectionToView(scroll: true);
    }

    /// <summary>显示焦点错误提示（文案与结果契约同源；呼出时清除，F14）。</summary>
    public void ShowStatus(string message) => _viewModel.StatusText = message;

    /// <summary>
    /// 呼出落地：光标所在屏工作区居中，尺寸按 F15 公式；光标屏放不下时主屏兜底；回读验证落地。
    /// 状态重置（F14 呼出重置）由协调器 Show() 的 panel:shown 事件先行完成。
    /// </summary>
    public void Summon()
    {
        var cursor = _screens.GetCursorScreen();
        var primary = _screens.GetPrimary();
        var screen = cursor ?? primary;

        var size = screen is null
            ? PanelGeometry.CalculateSize(FallbackMonitorHeightPx, FallbackDpiScale)
            : PanelGeometry.CalculateSize(screen.MonitorRect.Height, screen.DpiScale);
        Width = size.Width;
        Height = size.Height;

        var position = PanelGeometry.ResolveSummonPosition(
            size,
            cursor?.WorkAreaDip,
            primary?.WorkAreaDip ?? new WorkAreaDip(0, 0, 1920, 1040));

        _ = screen is { } metrics
            && WindowPlacer.PlaceSummonDip(Hwnd, metrics, position, size.Width, size.Height);

        _docked = false;
    }

    /// <summary>停靠：屏外驻留（工作区右缘外 20 DIP、y=工作区顶），窗口不销毁。</summary>
    public void Dock()
    {
        var screen = _screens.GetFromWindow(Hwnd) ?? _screens.GetPrimary();
        var position = PanelGeometry.ResolveDockPosition(screen?.WorkAreaDip);
        var widthDip = ActualWidth > 0 ? ActualWidth : Width;
        var heightDip = ActualHeight > 0 ? ActualHeight : Height;

        WindowPlacer.PlaceDip(Hwnd, screen, position, widthDip, heightDip);

        _docked = true;
    }

    // —— 协调器 panel:key 事件的渲染侧（动作名协议见 Domain.PanelModes） ——

    /// <summary>渲染模式状态机转发的动作（浏览态 Esc 已由宿主拦截为完整停靠流程，不经此）。</summary>
    public void HandlePanelKey(string action, string? noteEntryId)
    {
        switch (action)
        {
            case "search-enter":
                _viewModel.SearchActive = true;
                SearchBox.IsReadOnly = false;
                ResetQuery(); // 进搜索即回全量（与 legacy setDebouncedQuery('') 同义）
                _viewModel.SetFooterMode(PanelMode.Search);
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text.Length;
                break;
            case "search-exit":
                _viewModel.SearchActive = false;
                SearchBox.IsReadOnly = true;
                ResetQuery(); // 退出搜索立即回全量，不等防抖
                _viewModel.SetFooterMode(PanelMode.Browse);
                Keyboard.ClearFocus();
                break;
            case "up":
                _viewModel.MoveSelection(NavDirection.Up);
                SyncSelectionToView(scroll: true);
                break;
            case "down":
                _viewModel.MoveSelection(NavDirection.Down);
                SyncSelectionToView(scroll: true);
                break;
            case "enter":
                // 查询结果中选中项复制并粘贴（F11；空列表或越界 = 无动作）
                if (_viewModel.SelectedIndex >= 0
                    && SearchRules.EntryAt(_viewModel.Items, _viewModel.SelectedIndex) is { } card)
                {
                    CardPasteRequested?.Invoke(card.Id);
                }
                break;
            case "note-edit-enter":
                // 备注编辑占位：只落状态与焦点（Enter 保存/Esc 取消/失焦保存/长度规则归 T05）
                _viewModel.NoteEditingId = noteEntryId ?? _viewModel.SelectedItemId;
                _viewModel.SetFooterMode(PanelMode.NoteEdit);
                break;
            case "note-edit-exit":
                _viewModel.NoteEditingId = null;
                _viewModel.SetFooterMode(PanelMode.Browse);
                break;
            case "delete":
            case "pin":
                // Del 延迟删除与 Z 置顶的领域效果归 T05；键位让位矩阵本票已生效
                break;
        }
    }

    /// <summary>呼出重置（F14）：选中第一项、退搜索、清查询、清焦点错误与备注编辑。</summary>
    public void HandlePanelShown()
    {
        _viewModel.StatusText = string.Empty;
        _viewModel.SearchActive = false;
        SearchBox.IsReadOnly = true;
        ResetQuery();
        _viewModel.NoteEditingId = null;
        _viewModel.SetFooterMode(PanelMode.Browse);
        _viewModel.SelectedIndex = 0;
        SyncSelectionToView(scroll: true);
        Keyboard.ClearFocus();
    }

    // —— 搜索头 ——

    /// <summary>浏览态点击搜索井 = 进搜索（F22；搜索态的点击交给输入框，不拦截）。</summary>
    private void OnSearchWellPress(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.SearchActive)
        {
            SearchActivationRequested?.Invoke();
        }
    }

    /// <summary>清除按钮：清查询并保留输入焦点（F23）。MouseDown 即处理，避免焦点跳走。</summary>
    private void OnSearchClearPress(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ResetQuery();
        SearchBox.Focus();
        SearchBox.CaretIndex = 0;
    }

    /// <summary>查询归零的单一入口（防抖丢弃 + 输入框清空 + 立即回全量），四处重置共用。</summary>
    private void ResetQuery()
    {
        _searchDebouncer.Reset();
        SetQueryText(string.Empty);
        _viewModel.ApplySearchQuery(string.Empty);
    }

    private void SetQueryText(string text)
    {
        _suppressQueryEvents = true;
        try
        {
            SearchBox.Text = text;
        }
        finally
        {
            _suppressQueryEvents = false;
        }
        UpdateClearVisibility();
    }

    private void UpdateClearVisibility() =>
        SearchClear.Visibility = _viewModel.SearchActive && SearchBox.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>输入文本变化：立即进视图模型展示流，120ms 防抖后生效过滤（F22）。</summary>
    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateClearVisibility();
        if (_suppressQueryEvents)
        {
            return; // 重置/清除路径已显式应用查询，不重复进防抖
        }
        if (_viewModel.SearchActive)
        {
            _searchDebouncer.QueryChanged(SearchBox.Text);
        }
    }

    // —— IME composition（F21） ——

    /// <summary>
    /// composition 事件只由 TSF 输入（中文输入法等）触发：组合期间导航键全部让位，
    /// 组合结束恢复。Start/TextInput 对普通键入也会成对发出（合成 AutoComplete=true 的
    /// 立即完成组合），故 Start 侧以 AutoComplete=false 过滤出真正的 IME 组合。
    /// </summary>
    private void HookCompositionEvents()
    {
        SearchBox.AddHandler(
            System.Windows.Input.TextCompositionManager.TextInputStartEvent,
            new TextCompositionEventHandler(OnCompositionStart));
        SearchBox.AddHandler(
            System.Windows.Input.TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler(OnCompositionEnd));
    }

    private void OnCompositionStart(object sender, TextCompositionEventArgs e)
    {
        if (e.TextComposition.AutoComplete == TextCompositionAutoComplete.On)
        {
            return; // 普通键入的立即完成组合，不是 IME 组合
        }
        _composing = true;
        CompositionChanged?.Invoke(true);
    }

    private void OnCompositionEnd(object sender, TextCompositionEventArgs e)
    {
        if (!_composing)
        {
            return; // 普通键入的成对 TextInput，不是组合结束
        }
        _composing = false;
        CompositionChanged?.Invoke(false);
    }

    /// <summary>
    /// 组合取消兜底：Esc 丢弃候选等取消路径可能不发 TextInput（组合结束事件），
    /// 会在搜索态悬挂 composing、导航键持续让位。这里在 Esc 抵达输入框时强制恢复
    /// （导航键此时未注册，Esc 一定到达 WPF；不标记 handled，让输入法照常处理）。
    /// </summary>
    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_composing && e.Key == Key.Escape)
        {
            _composing = false;
            CompositionChanged?.Invoke(false);
        }
    }

    // —— 选中与列表 ——

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }
        _viewModel.SelectedIndex = HistoryList.SelectedIndex; // 鼠标点选回写视图模型（键盘导航基准）
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.SelectedIndex))
        {
            // 键盘移动/查询重置/夹紧后都要保持选中项完整可见
            SyncSelectionToView(scroll: true);
        }
    }

    /// <summary>视图模型选中索引 → 列表选中与滚动跟随（F19：选中项完整可见，即时跟随）。</summary>
    private void SyncSelectionToView(bool scroll)
    {
        _syncingSelection = true;
        try
        {
            var index = _viewModel.SelectedIndex;
            if (index >= 0 && index < HistoryList.Items.Count)
            {
                HistoryList.SelectedIndex = index;
                if (scroll)
                {
                    HistoryList.ScrollIntoView(HistoryList.SelectedItem);
                }
            }
            else
            {
                HistoryList.SelectedIndex = -1;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    // —— 卡片交互 ——

    // PreviewMouseDoubleClick（隧道）：MouseDoubleClick 是 Direct 路由事件，绑在 ListBox 上
    // 收不到卡片内部的双击（事件由最内层 ListBoxItem 触发、不冒泡）；隧道阶段第一次按下
    // 已完成选中，SelectedItem 即被双击的卡片
    private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is CardViewModel card)
        {
            CardPasteRequested?.Invoke(card.Id);
        }
    }

    /// <summary>当前选中条目 id（Enter 复制并粘贴的目标；无选中返回 null）。</summary>
    public string? SelectedCardId => (HistoryList.SelectedItem as CardViewModel)?.Id;

    // —— 备注编辑占位（T05 接管保存/取消/失焦规则） ——

    private void OnNoteBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && (bool)e.NewValue)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnNoteBoxKeyDown(object sender, KeyEventArgs e)
    {
        // F21：IME 组合中 Enter 走 ImeProcessed（确认候选），不能当保存/退出键
        if (e.Key is Key.ImeProcessed)
        {
            return;
        }
        if (e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            NoteEditExitRequested?.Invoke(); // 占位：只退态不落库（保存规则归 T05）
        }
    }

    /// <summary>渲染复制并粘贴结果（结果契约 message 单源；呼出时清除）。</summary>
    public void ShowResult(Domain.PasteChain.CopyResult result)
    {
        if (!result.Ok)
        {
            _viewModel.StatusText = result.Message;
        }
        // 成功文案「已复制并粘贴」不打扰：面板即刻停靠（F12），无额外提示需求
    }
}
