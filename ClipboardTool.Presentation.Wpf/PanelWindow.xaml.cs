using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipboardTool.Domain.Geometry;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 呼出面板窗（本仓库 ADR-0002）：无边框透明圆角壳，常驻 WS_EX_TOOLWINDOW，
/// 浏览态常驻 WS_EX_NOACTIVATE 不抢前台；停靠=屏外驻留不销毁，同 HWND 再呼出落地。
/// 呼出几何按 F15/F16 由 PanelGeometry 计算，落地回读验证走 WindowPlacer（legacy landing_verdict）。
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
    private bool _docked = true;

    /// <summary>双击卡片：复制并粘贴该条目（F11 第二入口；单击仅选中）。</summary>
    public event Action<string>? CardPasteRequested;

    public PanelWindow()
    {
        InitializeComponent();
        DataContext = new PanelViewModel();

        SourceInitialized += (_, _) =>
        {
            FocusAdapter.EnsureToolWindow(this);
            FocusAdapter.SetNoActivate(this, on: true);
            Dock(); // 初始停靠：显示前先落位屏外，避免闪现
        };

        // Esc 停靠是浏览态全局键（F18，让位模型）：由 HotkeyPlan 差量注册，不在此处理窗口消息
    }

    /// <summary>停靠状态变化（true=已停靠）。键位计划按状态重算（呼出期含 Esc 停靠，停靠后让位）。</summary>
    public event Action<bool>? DockStateChanged;

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

    /// <summary>整表重载历史条目（历史服务的事件已归队 UI 线程）。</summary>
    public void ReloadEntries(IReadOnlyList<Domain.History.HistoryEntry> entries) =>
        ((PanelViewModel)DataContext).Reload(entries);

    /// <summary>显示焦点错误提示（文案与结果契约同源；呼出时清除，F14）。</summary>
    public void ShowStatus(string message) => ((PanelViewModel)DataContext).StatusText = message;

    /// <summary>呼出落地：光标所在屏工作区居中，尺寸按 F15 公式；光标屏放不下时主屏兜底；回读验证落地。</summary>
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

        OnSummoned();
        _docked = false;
        DockStateChanged?.Invoke(_docked);
    }

    /// <summary>呼出后置位（F14）：清结果/焦点错误提示、选中重置为第一项并滚动到位。</summary>
    private void OnSummoned()
    {
        var viewModel = (PanelViewModel)DataContext;
        viewModel.StatusText = string.Empty;
        if (HistoryList.Items.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
            HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        }
    }

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

    /// <summary>渲染复制并粘贴结果（结果契约 message 单源；呼出时清除）。</summary>
    public void ShowResult(Domain.PasteChain.CopyResult result)
    {
        if (!result.Ok)
        {
            ((PanelViewModel)DataContext).StatusText = result.Message;
        }
        // 成功文案「已复制并粘贴」不打扰：面板即刻停靠（F12），无额外提示需求
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
        DockStateChanged?.Invoke(_docked);
    }
}
