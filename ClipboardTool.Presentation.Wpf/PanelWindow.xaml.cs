using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipboardTool.Application;
using ClipboardTool.Domain.Geometry;
using ClipboardTool.Domain.Hotkeys;
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

    /// <summary>toast 时长（F46）：普通 2600ms、入场 240ms、离场 160ms；撤销 toast 6000ms 见 PendingDeletionService.UndoToastMs。
    /// 窄窗页脚收紧阈值 ≤340 DIP（04-界面还原规格 §窄窗）。</summary>
    private const int ToastLifeMs = 2600;
    private const int ToastLeaveMs = 160;
    private const double FooterCompactWidthDip = 340;

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
    private string _noteDraftInitial = string.Empty; // 进入备注编辑时的原文快照（保存差异判断）
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

    /// <summary>Z 置顶切换（F24；选中项 id，置顶效果经 Application 落库）。</summary>
    public event Action<string>? PinRequested;

    /// <summary>Del 延迟删除（F25；选中项 id，摘除与撤销窗口由 Application 服务管理）。</summary>
    public event Action<string>? DeleteRequested;

    /// <summary>备注保存（F07；Enter/失焦/强退差异路径共用，raw 草稿由调用方 trim）。</summary>
    public event Action<string, string>? NoteSaveRequested;

    /// <summary>备注取消（Esc：不保存直接退态）。</summary>
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
        // 捕获覆盖层（F31）：窗口级隧道拦截，覆盖层可见时吃掉全部按键（捕获态无全局键）
        PreviewKeyDown += OnCaptureOverlayPreviewKeyDown;
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

    /// <summary>整表重载历史条目并注入延迟删除遮罩（历史/删除服务的事件已归队 UI 线程）；重放当前查询。</summary>
    public void ReloadEntries(IReadOnlyList<Domain.History.HistoryEntry> entries, IReadOnlyCollection<string>? hiddenIds = null)
    {
        if (hiddenIds is not null)
        {
            _viewModel.SetHiddenIds(hiddenIds);
        }
        _viewModel.Reload(entries);
        SyncSelectionToView(scroll: true);
    }

    /// <summary>页脚右侧焦点错误（F45 覆盖普通键位组；呼出时清除，F14）。</summary>
    public void ShowStatus(string message) => _viewModel.FooterStatus = message;

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
                // 进入内联备注编辑（F07）：草稿初值=编辑目标的当前备注（已有非空备注进入时全选由可见事件完成）
                var noteTarget = noteEntryId ?? _viewModel.SelectedItemId;
                if (noteTarget is null)
                {
                    break; // 空列表无编辑目标
                }
                _noteDraftInitial = _viewModel.Items
                    .FirstOrDefault(card => card.Id == noteTarget)?.Note ?? string.Empty;
                _viewModel.NoteDraft = _noteDraftInitial;
                _viewModel.NoteEditingId = noteTarget;
                _viewModel.SetFooterMode(PanelMode.NoteEdit);
                break;
            case "note-edit-exit":
                // 状态机强退（进别的输入态/停靠）：legacy handleNoteEditExit——按差异保存后清态
                CommitNoteDraftIfChanged();
                _viewModel.NoteEditingId = null;
                _viewModel.NoteDraft = string.Empty;
                _viewModel.SetFooterMode(PanelMode.Browse);
                break;
            case "delete":
                // Del 延迟删除（F25）：立即摘除可见列表 + 6 秒撤销，摘除与真删由 Application 服务管理
                if (_viewModel.SelectedItemId is { } deleteId)
                {
                    DeleteRequested?.Invoke(deleteId);
                }
                break;
            case "pin":
                // Z 置顶切换（F24）：置顶移置顶块首、取消移普通块首（取消保留原 pinnedAt）
                if (_viewModel.SelectedItemId is { } pinId)
                {
                    PinRequested?.Invoke(pinId);
                }
                break;
        }
    }

    /// <summary>
    /// 放弃指向指定条目的备注编辑（删除流程的 hide 语义，legacy App.tsx hide 分支清 noteEdit）：
    /// 只清渲染态不保存不退态（退态由调用方编排）。编辑目标不匹配则无操作。
    /// </summary>
    public void AbandonNoteEditIfEditing(string entryId)
    {
        if (_viewModel.NoteEditingId != entryId)
        {
            return;
        }
        _viewModel.NoteEditingId = null;
        _viewModel.NoteDraft = string.Empty;
        _viewModel.SetFooterMode(PanelMode.Browse);
    }

    /// <summary>呼出重置（F14）：选中第一项、退搜索、清查询、清焦点错误与备注编辑（不保存差异）。</summary>
    public void HandlePanelShown()
    {
        _viewModel.FooterStatus = string.Empty;
        _viewModel.SearchActive = false;
        SearchBox.IsReadOnly = true;
        ResetQuery();
        _viewModel.NoteEditingId = null;
        _viewModel.NoteDraft = string.Empty;
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

    // —— 内联备注编辑（F07/F47；legacy ClipCard note-input + App.tsx noteEdit） ——

    private void OnNoteBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && (bool)e.NewValue)
        {
            // 焦点时序：IsVisibleChanged 在 NoteEditingId 绑定传播时同步触发，早于窗口激活完成
            // （EnterInput 的 FocusPanel → window.Activate 的 WM_ACTIVATE 异步派发）；立即 Focus
            // 会被激活处理时的焦点重评估覆盖——排到 Input 优先级，激活尘埃落定后再聚焦。
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () =>
                {
                    if (box.IsVisible) // 期间被 Esc/强退收起则放弃
                    {
                        box.Focus();
                        box.SelectAll(); // 已有非空备注进入时全选（F07）
                    }
                });
        }
    }

    private void OnNoteBoxKeyDown(object sender, KeyEventArgs e)
    {
        // F21：IME 组合中 Enter 走 ImeProcessed（确认候选），不能当保存/退出键
        if (e.Key is Key.ImeProcessed)
        {
            return;
        }
        if (e.Key is Key.Enter)
        {
            e.Handled = true;
            SaveNoteFromEditor();
        }
        else if (e.Key is Key.Escape)
        {
            // 取消：不保存直接退态（legacy finishNoteEditing(true) 一律不保存）。
            // 先清 NoteEditingId 再退态：协调器随后发的 note-edit-exit 会因编辑态已清而短路，
            // 不会走强退差异保存路径（两态意图分离，避免取消被静默改成保存）
            e.Handled = true;
            _viewModel.NoteEditingId = null;
            _viewModel.NoteDraft = string.Empty;
            _viewModel.SetFooterMode(PanelMode.Browse);
            NoteEditExitRequested?.Invoke();
        }
    }

    /// <summary>失焦保存（F07）：编辑仍指向本卡时按保存路径退态；取消/保存路径退态后的 blur 直接跳过。</summary>
    private void OnNoteBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_viewModel.NoteEditingId is not null)
        {
            SaveNoteFromEditor();
        }
    }

    /// <summary>
    /// 保存路径：先清编辑态再发保存事件——App 侧退态发出的 note-edit-exit 会因编辑态已清而短路，
    /// 不产生重入双发（legacy 用 noteSavePendingRef 互斥，此处以「清态先行」达成同一保证）。
    /// </summary>
    private void SaveNoteFromEditor()
    {
        var id = _viewModel.NoteEditingId;
        if (id is null)
        {
            return;
        }
        var draft = _viewModel.NoteDraft;
        _viewModel.NoteEditingId = null;
        _viewModel.NoteDraft = string.Empty;
        _viewModel.SetFooterMode(PanelMode.Browse);
        NoteSaveRequested?.Invoke(id, draft);
    }

    /// <summary>强退差异保存（legacy handleNoteEditExit）：草稿与进入时原文有差异才发保存事件。</summary>
    private void CommitNoteDraftIfChanged()
    {
        if (_viewModel.NoteEditingId is null)
        {
            return;
        }
        if (!string.Equals(_viewModel.NoteDraft.Trim(), _noteDraftInitial.Trim(), StringComparison.Ordinal))
        {
            NoteSaveRequested?.Invoke(_viewModel.NoteEditingId, _viewModel.NoteDraft);
        }
    }

    // —— toast 栈（F46；legacy ToastStack + pushToast） ——

    /// <summary>
    /// 入栈一条 toast：普通 2600ms、带动作（撤销）6000ms；入场淡入 240ms、离场淡出 160ms 后移除。
    /// 动作点击立即离场（DismissToast 另行排程移除，重复排程对已移除元素无操作）。
    /// </summary>
    public void ShowToast(string message, string? dim, bool isError, string? actionLabel, Action? onAction)
    {
        var toast = new ToastViewModel
        {
            Message = message,
            Dim = dim,
            IsError = isError,
            ActionLabel = actionLabel,
            OnAction = onAction,
        };
        _viewModel.Toasts.Add(toast);
        var life = actionLabel is not null ? PendingDeletionService.UndoToastMs : ToastLifeMs;
        ScheduleToastRemoval(toast, life);
        // 布局就绪后再触发入场淡入（Entering=True → False 的转换动画在 XAML 触发）
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (_viewModel.Toasts.Contains(toast))
            {
                toast.Entering = false;
            }
        });
    }

    /// <summary>动作按钮（撤销）：执行回调并立即离场（160ms 淡出后移除）。</summary>
    private void OnToastAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastViewModel { } toast)
        {
            toast.OnAction?.Invoke();
            DismissToast(toast);
        }
    }

    private void DismissToast(ToastViewModel toast)
    {
        if (toast.Leaving)
        {
            return;
        }
        toast.Leaving = true;
        RemoveToastLater(toast, ToastLeaveMs);
    }

    private void ScheduleToastRemoval(ToastViewModel toast, int lifeMs)
    {
        var leave = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(lifeMs),
        };
        leave.Tick += (_, _) =>
        {
            leave.Stop();
            DismissToast(toast);
        };
        leave.Start();
    }

    private void RemoveToastLater(ToastViewModel toast, int delayMs)
    {
        var remove = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };
        remove.Tick += (_, _) =>
        {
            remove.Stop();
            _viewModel.Toasts.Remove(toast);
        };
        remove.Start();
    }

    /// <summary>页脚紧凑态（F47 窄档）：页脚实际宽度 ≤340 DIP 时收紧（左右 10、字 9.5、组距 3）。</summary>
    private void OnFooterSizeChanged(object sender, SizeChangedEventArgs e) =>
        _viewModel.FooterCompact = e.NewSize.Width <= FooterCompactWidthDip;

    /// <summary>渲染复制并粘贴结果（结果契约 message 单源；呼出时清除）。失败红叉 toast；
    /// 成功绿勾 toast（F46；面板即刻停靠 F12，toast 随停靠不可见——与 legacy 同为停靠窗口内的
    /// 短暂残留，2.6s 内再呼出可见）。</summary>
    public void ShowResult(Domain.PasteChain.CopyResult result)
    {
        if (result.Ok)
        {
            ShowToast("已复制并粘贴", null, isError: false, actionLabel: null, onAction: null);
        }
        else
        {
            ShowToast(result.Message, null, isError: true, actionLabel: null, onAction: null);
        }
    }

    // —— 捕获覆盖层（F31；legacy shortcutCapture 渲染态的移植） ——

    /// <summary>捕获层录入到的新组合（校验通过的主键 + 修饰键；Esc 走取消事件不经此）。</summary>
    public event Action<HotkeyCombo>? CaptureAttempted;

    /// <summary>捕获取消（Esc）：状态机退出捕获态并发 capture-end，宿主据此收层。</summary>
    public event Action? CaptureCancelled;

    /// <summary>捕获覆盖层是否正在显示。</summary>
    public bool IsCaptureOverlayVisible => CaptureOverlay.Visibility == Visibility.Visible;

    /// <summary>显示捕获覆盖层（进入捕获态并呼出面板之后调用；焦点由宿主先聚到面板）。</summary>
    public void ShowCaptureOverlay()
    {
        CaptureStatusText.Text = string.Empty;
        CaptureOverlay.Visibility = Visibility.Visible;
        Focus(); // 覆盖层不进 tab 序，窗口级 PreviewKeyDown 收键（宿主已清 NOACTIVATE）
    }

    /// <summary>收起捕获覆盖层（capture-end 事件与成功后延迟收层共用）。</summary>
    public void HideCaptureOverlay() => CaptureOverlay.Visibility = Visibility.Collapsed;

    /// <summary>状态行：失败（占用/无效/缺修饰）红叉色，成功默认色。</summary>
    public void SetCaptureStatus(string text, bool ok)
    {
        CaptureStatusText.Text = text;
        CaptureStatusText.Foreground = ok
            ? (Brush)FindResource("Brush.Text.Primary")
            : (Brush)FindResource("Brush.Error");
    }

    /// <summary>
    /// 覆盖层可见时窗口级吃键：修饰键自身忽略（等主键）；Esc 取消；其余主键经 WPF Key →
    /// 虚拟键码构造组合交给捕获判定（占用/无效/成功全部由状态机与宿主决定，本层只转换）。
    /// </summary>
    private void OnCaptureOverlayPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsCaptureOverlayVisible)
        {
            return;
        }
        e.Handled = true;
        switch (e.Key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return; // 修饰键按下本身不算录入，等主键
            case Key.Escape:
                CaptureCancelled?.Invoke();
                return;
            case Key.System:
                break; // Alt 组合：真实键在 SystemKey
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None)
        {
            return;
        }
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var combo = new HotkeyCombo(HotkeyModifiersOf(Keyboard.Modifiers), virtualKey);
        CaptureAttempted?.Invoke(combo);
    }

    private static HotkeyModifiers HotkeyModifiersOf(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;
        return result;
    }
}
