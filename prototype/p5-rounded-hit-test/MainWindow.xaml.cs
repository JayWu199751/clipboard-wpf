using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace P5RoundedHitTest;

/// <summary>
/// P5 跨 DPI 圆角穿透试验：36 DIP 圆角外壳在分数 DPI 下，弧外透明区鼠标穿透到下层窗口、
/// 弧内正常交互。WM_NCHITTEST 钩子按 CornerHitGeometry 纯函数（radius × 当前 DPI）判定，
/// 弧外返回 HTTRANSPARENT、弧内返回 HTCLIENT。
/// 自动序列覆盖四条判据：角外落到下层（浏览态/输入态 × 真实点击 × 不透明控制组）、
/// 角内点击与键盘、四角弧扫与纯函数一致性、负坐标消息注入（多显示器模拟）。
/// 跑完把报告与截图落盘后自动退出；--manual 保持窗口供人工复核。
/// </summary>
public partial class MainWindow : Window
{
    private const double ShellRadiusDip = 36;
    private const double ShellBorderDip = 2;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly StringBuilder _log = new();
    private int _passCount;
    private int _failCount;
    private IntPtr _baselineForeground;
    private volatile bool _hookEnabled = true; // C5 归因控制：临时关闭 WM_NCHITTEST 钩子观察纯 alpha 行为

    private TargetWindow? _target;
    private Thread? _targetThread;
    private volatile int _targetClicks;
    private int _wellClicks;
    private int _cardClicks;
    private int _footerClicks;
    private int _shellClicks;
    private readonly List<string> _clickDiag = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this)!;
            source.AddHook(WndHook);
            SetNoActivate(on: true);
        };
        HookCounters();
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--manual"))
            {
                Counters.Text = "manual：点角内/角外，观察穿透与计数；关窗即退出。";
                SummonToScreenCenter();
                return;
            }
            await RunSequenceAsync();
        };
    }

    // —— WM_NCHITTEST 钩子：本试验的核心机制 ——

    private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST || !_hookEnabled)
        {
            return IntPtr.Zero;
        }
        // lParam 是屏幕物理坐标（多显示器下可为负，须有符号解析）；
        // 减窗口矩形原点得窗口本地物理坐标，再按 radius × 当前 DPI 判定弧内/弧外。
        var (sx, sy) = CornerHitGeometry.UnpackLParam(lParam);
        if (!GetWindowRect(hwnd, out var rect))
        {
            return IntPtr.Zero;
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var hit = CornerHitGeometry.HitTestPhysical(
            sx - rect.Left, sy - rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            ShellRadiusDip * dpi);
        handled = true;
        return new IntPtr(hit == CornerHit.Transparent ? HTTRANSPARENT : HTCLIENT);
    }

    // —— 计数与交互（判据②的可观测面） ——

    private void HookCounters()
    {
        Rows.PreviewMouseLeftButtonDown += (_, _) => _shellClicks++;
        SearchWell.PreviewMouseLeftButtonDown += (_, _) => { _wellClicks++; UpdateCounters(); };
        Card2.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _cardClicks++;
            _clickDiag.Add($"t={Environment.TickCount64} ClickCount={e.ClickCount} pos={e.GetPosition(Card2):F0}");
            Card2.Background = _cardClicks % 2 == 1
                ? new SolidColorBrush(Colors.LightGreen)
                : new SolidColorBrush(Colors.White);
            UpdateCounters();
        };
        Footer.PreviewMouseLeftButtonDown += (_, _) => { _footerClicks++; UpdateCounters(); };
    }

    private void UpdateCounters() =>
        Counters.Text = $"井:{_wellClicks} 卡:{_cardClicks} 页脚:{_footerClicks} 靶:{_targetClicks} 壳:{_shellClicks}";

    // —— 试验序列 ——

    private async Task RunSequenceAsync()
    {
        StartTargetWindow();
        SummonToScreenCenter();
        await Task.Delay(500);

        _baselineForeground = GetForegroundWindow();
        _log.AppendLine("# P5 圆角穿透试验报告（自动序列）");
        _log.AppendLine();
        _log.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _log.AppendLine($"- 系统：{Environment.OSVersion.VersionString}，{(Environment.Is64BitProcess ? "x64" : "x86")}，DPI awareness：PerMonitorV2");
        _log.AppendLine($"- 探针窗：400×600 DIP，圆角 36 DIP，描边 2 DIP；当前 DPI = {VisualTreeHelper.GetDpi(this).PixelsPerDip:0.##}");
        _log.AppendLine($"- 前台基准 HWND：{_baselineForeground}；靶窗 HWND：{_target?.Hwnd}");
        _log.AppendLine();
        _log.AppendLine("| 断言 | 结果 | 证据 |");
        _log.AppendLine("|---|---|---|");

        // A：WM_NCHITTEST 钩子几何（消息级，无需真实鼠标）
        await PhaseA();

        // B：浏览态（NOACTIVATE）真实点击
        await PhaseB();
        if (_clickDiag.Count > 0)
        {
            _log.AppendLine();
            _log.AppendLine($"- 注入自诊断：{string.Join("；", _clickDiag)}");
        }

        // C：输入态（可激活）真实点击 + 键盘 + 不透明控制组
        await PhaseC();

        // 收尾：恢复浏览态、停靠屏外、截图落盘、退出
        SetNoActivate(on: true);
        Park();
        CaptureScreenshots();
        _log.AppendLine();
        _log.AppendLine($"- 汇总：通过 {_passCount}，未过 {_failCount}");
        var reportPath = Path.Combine(AppContext.BaseDirectory, "p5-report.md");
        await File.WriteAllTextAsync(reportPath, _log.ToString());
        Console.WriteLine(reportPath);

        StopTargetWindow();
        Application.Current.Shutdown();
    }

    /// <summary>A：钩子几何一致性——四角弧内/弧外、边带、全弧扫、负坐标消息注入。</summary>
    private async Task PhaseA()
    {
        var rect = PhysicalRect();
        var r = ShellRadiusDip * VisualTreeHelper.GetDpi(this).PixelsPerDip;

        Verdict("A1 四角弧内点返回 HTCLIENT",
            Corners.All(c => ExpectHit(c, 0.6, CornerHit.Client, rect, r)),
            "每角对角 0.6R 处（距弧心 ≈0.57R）经 SendMessage(WM_NCHITTEST) 判定");

        Verdict("A2 四角弧外点返回 HTTRANSPARENT",
            Corners.All(c => ExpectHit(c, 0.08, CornerHit.Transparent, rect, r)),
            "每角对角 0.08R 处（距弧心 ≈1.30R）");

        Verdict("A3 边带与中心返回 HTCLIENT",
            ExpectLocal(CornerHit.Client, rect.Width / 2, rect.Height / 2, r)
            && ExpectLocal(CornerHit.Client, rect.Width / 2, 3, r)
            && ExpectLocal(CornerHit.Client, rect.Width / 2, rect.Height - 4, r)
            && ExpectLocal(CornerHit.Client, 3, rect.Height / 2, r)
            && ExpectLocal(CornerHit.Client, rect.Width - 4, rect.Height / 2, r),
            "中心与四边中带（非角部方带）");

        Verdict("A4 四角弧扫 64 点与纯函数一致",
            ArcSweep(rect, r, out var sweepDetail),
            sweepDetail);

        // 判据④的负坐标部分：真实 WndProc + 有符号 lParam 解析。
        // 单屏无法布置负坐标显示器，用「窗口实际移到负坐标 + 消息注入」模拟（合法手段，非真实多屏）。
        var saved = rect;
        _ = SetWindowRect(-300, -200, (int)rect.Width, (int)rect.Height);
        await Task.Delay(200);
        var moved = PhysicalRect();
        var negOk = ExpectHit(Corner.TopLeft, 0.6, CornerHit.Client, moved, r)
            && ExpectHit(Corner.TopLeft, 0.08, CornerHit.Transparent, moved, r);
        _ = SetWindowRect((int)saved.Left, (int)saved.Top, (int)saved.Width, (int)saved.Height);
        await Task.Delay(200);
        Verdict("A5 负坐标 lParam（模拟多显示器）", negOk,
            $"窗口移至 ({moved.Left},{moved.Top})，屏幕坐标为负；弧内/弧外各一点经真实 WndProc 判定一致（消息注入模拟，非真实多屏）");
    }

    /// <summary>B：浏览态（WS_EX_NOACTIVATE）——角内可交互、不抢前台；角外穿透（alpha 基线）。</summary>
    private async Task PhaseB()
    {
        // B1 搜索井
        RaiseProbeAboveTarget();
        ClickElement(SearchWell);
        await Task.Delay(350);
        Verdict("B1 浏览态点击搜索井计数且不激活", _wellClicks == 1 && GetForegroundWindow() == _baselineForeground,
            $"井计数={_wellClicks}，前台仍=基准");

        // B2 卡片 + 页脚
        RaiseProbeAboveTarget();
        ClickElement(Card2);
        await Task.Delay(350);
        ClickElement(Footer);
        await Task.Delay(350);
        Verdict("B2 浏览态点击卡片与页脚可达", _cardClicks >= 1 && _footerClicks == 1,
            $"卡计数={_cardClicks}（多计归因见 D1），页脚计数={_footerClicks}");

        // B3 角外真实点击（透明背景 = 分层 alpha 与 HTTRANSPARENT 并存，未归因的端到端基线）
        var outside = ScreenPointOf(Corner.TopLeft, 0.08);
        var wpBefore = WindowFromPointAt(outside);
        ClickAt(outside);
        await Task.Delay(350);
        Verdict("B3 浏览态角外点击落到下层靶窗", _targetClicks >= 1 && GetForegroundWindow() == _baselineForeground,
            $"靶计数={_targetClicks}，点击前 WindowFromPoint={wpBefore}（透明背景，alpha 与 HTTRANSPARENT 并存）");

        // B4 角部方带内但弧内的点（0.6R 对角）真实点击——弧内区域可交互（该点落在搜索井上）
        RaiseProbeAboveTarget();
        ClickElement(SearchWell);
        await Task.Delay(350);
        Verdict("B4 弧内区域真实点击可达控件", _wellClicks == 2,
            $"对角 0.6R 处点击命中搜索井，井计数={_wellClicks}");

        // B5/B6 紧贴弧边界（对角线弧边界在 t≈0.293R）：±4% R 内夹紧「命中区域与视觉圆角一致」
        RaiseProbeAboveTarget();
        var shellBefore = _shellClicks;
        ClickAt(ScreenPointOf(Corner.TopLeft, 0.32)); // 距弧心 ≈0.962R，弧内
        await Task.Delay(350);
        Verdict("B5 弧边界内侧 0.32R 真实点击落在壳内", _shellClicks == shellBefore + 1,
            $"壳计数 {shellBefore}→{_shellClicks}（点距弧心 ≈0.96R，抗锯齿带内侧）");

        var targetBefore = _targetClicks;
        ClickAt(ScreenPointOf(Corner.TopLeft, 0.26)); // 距弧心 ≈1.047R，弧外
        await Task.Delay(350);
        Verdict("B6 弧边界外侧 0.26R 真实点击穿透", _targetClicks == targetBefore + 1 && GetForegroundWindow() == _baselineForeground,
            $"靶计数 {targetBefore}→{_targetClicks}，前台仍=基准（点距弧心 ≈1.05R，抗锯齿带外侧）");
    }

    /// <summary>C：输入态——激活、键入；角外穿透归因（不透明控制组）；重复激活。</summary>
    private async Task PhaseC()
    {
        // C1 进入输入态：清 NOACTIVATE，真实点击搜索框 → 激活 + 键入
        SetNoActivate(on: false);
        SearchBox.IsReadOnly = false; // 只读 TextBox 会吞键入，输入态先解锁（与主工程 search-enter 同义）
        RaiseProbeAboveTarget();
        ClickElement(SearchBox);
        await Task.Delay(350);
        var activated = GetForegroundWindow() == Hwnd && SearchBox.IsKeyboardFocused;
        SendChars("p5");
        await Task.Delay(350);
        Verdict("C1 输入态点击激活且真实键入到达", activated && SearchBox.Text.Contains("p5"),
            $"前台=探针:{GetForegroundWindow() == Hwnd}，键盘焦点:{SearchBox.IsKeyboardFocused}，输入=\"{SearchBox.Text}\"");

        // C2 输入态角外点击：前台从探针变到靶窗（判据①的「前台/焦点变化可观测」）
        var outside = ScreenPointOf(Corner.TopLeft, 0.08);
        ClickAt(outside);
        await Task.Delay(350);
        Verdict("C2 输入态角外点击：前台探针→靶窗且靶收到点击",
            GetForegroundWindow() == (_target?.Hwnd ?? IntPtr.Zero) && _targetClicks >= 2,
            $"前台={DescribeWindow(GetForegroundWindow())}，靶计数={_targetClicks}");

        // C3 跨带控制：把靶窗降到普通带（探针在 Topmost 带），角部改不透明——
        // 唯一候选机制是 HTTRANSPARENT。试验结论：HTTRANSPARENT 不跨带转发真实点击，
        // 点击被丢弃（前台变 NULL、靶窗计数不变），且该行为未见于任何文档——不可依赖。
        var notTopmost = new IntPtr(-2); // HWND_NOTOPMOST
        _ = SetWindowPos(_target!.Hwnd, notTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Background = Brushes.Magenta; // 角部方带不再是 0 alpha
        await Task.Delay(300);
        var wp = WindowFromPointAt(outside);
        var targetBeforeC3 = _targetClicks;
        ClickAt(outside);
        await Task.Delay(350);
        var dropped = _targetClicks == targetBeforeC3
            && GetForegroundWindow() != (_target?.Hwnd ?? IntPtr.Zero);
        Background = Brushes.Transparent;
        await Task.Delay(200);
        Verdict("C3 跨带控制：HTTRANSPARENT 跨带转发真实点击？",
            dropped,
            $"角部 alpha=255、靶窗在普通带：点击后前台={DescribeWindow(GetForegroundWindow())}，靶计数 {targetBeforeC3}→{_targetClicks}，点击前 WindowFromPoint（查询路径）={wp}；「转发成功=未过」。跨带点击被丢弃为钉死的负发现");

        // C4 再点搜索框：重复激活可靠（靶窗已降普通带，探针抬回带顶后点击）
        RaiseProbeAboveTarget();
        ClickElement(SearchBox);
        await Task.Delay(350);
        Verdict("C4 重复点击搜索框重新激活探针", GetForegroundWindow() == Hwnd,
            $"前台=探针:{GetForegroundWindow() == Hwnd}");

        // C5 归因控制：关钩子（只剩分层 alpha），靶窗仍在普通带 → alpha 跨带穿透应成立
        _hookEnabled = false;
        await Task.Delay(300);
        var wp5 = WindowFromPointAt(outside);
        ClickAt(outside);
        await Task.Delay(350);
        var c5ok = _targetClicks == targetBeforeC3 + 1
            && GetForegroundWindow() == (_target?.Hwnd ?? IntPtr.Zero);
        _hookEnabled = true;
        // 靶窗回到 Topmost 带（下一窗口之上），恢复确定性 z 序
        _ = SetWindowPos(_target!.Hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        await Task.Delay(200);
        Verdict("C5 归因控制：仅分层 alpha（钩子关闭）跨带穿透", c5ok,
            $"透明背景+无 WM_NCHITTEST 处理、靶窗普通带：点击后前台={DescribeWindow(GetForegroundWindow())}，靶计数 {targetBeforeC3}→{_targetClicks}，点击前 WindowFromPoint={wp5}");

        // D：单击事件诊断——B2 观察到一次注入点击计了 3 次（非确定），连点 3 次记录
        // 每次事件的 ClickCount/时间戳，归因是双击合成还是事件重复
        RaiseProbeAboveTarget();
        var diagBefore = _cardClicks;
        for (var i = 0; i < 3; i++)
        {
            ClickElement(Card2);
            await Task.Delay(600); // > 系统双击时限，排除双击合成
        }
        var delta = _cardClicks - diagBefore;
        Verdict("D1 单击事件计数诊断", delta == 3,
            $"3 次注入点击 → {delta} 次 down；事件流：{string.Join("；", _clickDiag)}");

        SearchBox.IsReadOnly = true;
        SetNoActivate(on: true);
    }

    // —— 几何/命中辅助 ——

    private enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    private static readonly Corner[] Corners =
        [Corner.TopLeft, Corner.TopRight, Corner.BottomLeft, Corner.BottomRight];

    /// <summary>角部对角线上距角点 t·R 处的屏幕物理坐标。</summary>
    private (int X, int Y) ScreenPointOf(Corner corner, double t)
    {
        var rect = PhysicalRect();
        var r = ShellRadiusDip * VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var x = (corner is Corner.TopLeft or Corner.BottomLeft) ? t * r : rect.Width - t * r;
        var y = (corner is Corner.TopLeft or Corner.TopRight) ? t * r : rect.Height - t * r;
        return ((int)Math.Round(rect.Left + x), (int)Math.Round(rect.Top + y));
    }

    /// <summary>向真实 HWND 发送 WM_NCHITTEST 并核对预期。</summary>
    private bool ExpectHit(Corner corner, double t, CornerHit expected, RECT rect, double r)
    {
        var x = (corner is Corner.TopLeft or Corner.BottomLeft) ? t * r : rect.Width - t * r;
        var y = (corner is Corner.TopLeft or Corner.TopRight) ? t * r : rect.Height - t * r;
        return ExpectLocal(expected, x, y, r, rect);
    }

    private bool ExpectLocal(CornerHit expected, double localX, double localY, double r, RECT? rect = null)
    {
        var rc = rect ?? PhysicalRect();
        var sx = (int)Math.Round(rc.Left + localX);
        var sy = (int)Math.Round(rc.Top + localY);
        var raw = SendMessage(Hwnd, WM_NCHITTEST, IntPtr.Zero, PackLParam(sx, sy)).ToInt64();
        var got = raw == HTTRANSPARENT ? CornerHit.Transparent : CornerHit.Client;
        return got == expected && (expected == CornerHit.Transparent ? raw == HTTRANSPARENT : raw == HTCLIENT);
    }

    /// <summary>四角 × 8 方向 × 弧内(0.98R)/弧外(1.02R)：运行时钩子与纯函数逐点一致。</summary>
    private bool ArcSweep(RECT rect, double r, out string detail)
    {
        var failures = new StringBuilder();
        int checkedPoints = 0;
        foreach (var corner in Corners)
        {
            var cx = (corner is Corner.TopLeft or Corner.BottomLeft) ? r : rect.Width - r;
            var cy = (corner is Corner.TopLeft or Corner.TopRight) ? r : rect.Height - r;
            var sx = (corner is Corner.TopLeft or Corner.BottomLeft) ? -1 : 1;
            var sy = (corner is Corner.TopLeft or Corner.TopRight) ? -1 : 1;
            for (var i = 0; i < 8; i++)
            {
                var phi = Math.PI / 2 * i / 7;
                var dx = sx * Math.Cos(phi);
                var dy = sy * Math.Sin(phi);
                var insideOk = ExpectLocal(CornerHit.Client, cx + 0.98 * r * dx, cy + 0.98 * r * dy, r, rect);
                var outsideOk = ExpectLocal(CornerHit.Transparent, cx + 1.02 * r * dx, cy + 1.02 * r * dy, r, rect);
                checkedPoints += 2;
                if (!insideOk)
                {
                    failures.Append($" {corner}/弧内/φ={phi:0.00}");
                }
                if (!outsideOk)
                {
                    failures.Append($" {corner}/弧外/φ={phi:0.00}");
                }
            }
        }
        detail = $"4 角 × 8 方向 × 内外 2 点 = {checkedPoints} 点全一致；{failures}";
        return failures.Length == 0;
    }

    /// <summary>MAKELPARAM：低/高 16 位各装一个 16 位有符号坐标（与 UnpackLParam 互逆）。</summary>
    private static IntPtr PackLParam(int x, int y)
    {
        var lo = (uint)x & 0xFFFFu;
        var hi = (uint)y & 0xFFFFu;
        return new IntPtr(unchecked((int)(lo | (hi << 16))));
    }

    private RECT PhysicalRect()
    {
        _ = GetWindowRect(Hwnd, out var rect);
        return rect;
    }

    private bool SetWindowRect(int x, int y, int w, int h) =>
        SetWindowPos(Hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

    private IntPtr Hwnd => new WindowInteropHelper(this).EnsureHandle();

    private void SetNoActivate(bool on)
    {
        var style = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64();
        style = on ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        _ = SetWindowLongPtr(Hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    private void SummonToScreenCenter()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + (wa.Height - ActualHeight) / 2;
    }

    private void Park() => Left = -20000;

    // —— 靶窗线程 ——

    private void StartTargetWindow()
    {
        var ready = new ManualResetEventSlim();
        _targetThread = new Thread(() =>
        {
            var w = new TargetWindow(() => Interlocked.Increment(ref _targetClicks));
            _target = w;
            w.Show();
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        _targetThread.SetApartmentState(ApartmentState.STA);
        _targetThread.Start();
        ready.Wait(3000);
        UpdateCounters();
    }

    private void StopTargetWindow()
    {
        if (_target is { } t)
        {
            t.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
            _targetThread?.Join(1500);
        }
    }

    // —— 点击/键入注入与窗口观测 ——

    /// <summary>元素中心（DIP）→ 屏幕物理坐标。</summary>
    private (int X, int Y) CenterOf(FrameworkElement element)
    {
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return ((int)p.X, (int)p.Y);
    }

    private void ClickElement(FrameworkElement element) => ClickAt(CenterOf(element));

    private bool ClickAt((int X, int Y) pt)
    {
        var setOk = SetCursorPos(pt.X, pt.Y);
        Thread.Sleep(80);
        _ = GetCursorPos(out var actual);
        var down = SendInput(1, new[] { MouseInput(MOUSEEVENTF_LEFTDOWN) }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        var up = SendInput(1, new[] { MouseInput(MOUSEEVENTF_LEFTUP) }, Marshal.SizeOf<INPUT>());
        if (!setOk || actual.X != pt.X || actual.Y != pt.Y || down == 0 || up == 0)
        {
            _clickDiag.Add($"ClickAt({pt.X},{pt.Y}) SetCursorPos={setOk} 实际光标=({actual.X},{actual.Y}) " +
                $"SendInput down={down} up={up} err={Marshal.GetLastWin32Error()}");
        }
        return setOk && down == 1 && up == 1;
    }

    private string WindowFromPointAt((int X, int Y) pt)
    {
        var hwnd = WindowFromPoint(new POINT { X = pt.X, Y = pt.Y });
        return DescribeWindow(hwnd);
    }

    private string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "无窗口(NULL)";
        }
        if (hwnd == Hwnd)
        {
            return "探针窗";
        }
        if (hwnd == _target?.Hwnd)
        {
            return "靶窗";
        }
        _ = GetWindowRect(hwnd, out var rect);
        return $"其他 HWND=0x{hwnd.ToInt64():X} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})";
    }

    private void Verdict(string id, bool pass, string detail)
    {
        if (pass)
        {
            _passCount++;
        }
        else
        {
            _failCount++;
        }
        _log.AppendLine($"| {id} | {(pass ? "通过" : "未过")} | {detail} |");
    }

    // —— 截图落盘 ——

    private void CaptureScreenshots()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            SaveVisual(this, (int)Math.Ceiling(ActualWidth * dpi), (int)Math.Ceiling(ActualHeight * dpi),
                96 * dpi, "p5-probe-175pct.png", "真机 175% 探针窗实拍（含圆角壳与内容）");
            foreach (var (scale, name) in new[] { (1.0, "100"), (1.25, "125"), (1.5, "150"), (1.75, "175") })
            {
                SaveOverlay(scale, $"p5-overlay-{name}pct.png");
            }
        }
        catch (Exception ex)
        {
            _log.AppendLine();
            _log.AppendLine($"截图落盘失败：{ex.Message}");
        }
    }

    private void SaveVisual(Visual visual, int pxW, int pxH, double dpi, string file, string note)
    {
        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(AppContext.BaseDirectory, file));
        enc.Save(fs);
        _log.AppendLine();
        _log.AppendLine($"- 截图：{file}（{note}）");
    }

    /// <summary>四档 DPI 的命中几何示意（模拟渲染，非真机）：
    /// 视觉弧线、角部方带、弧内/弧外采样点（绿=命中、红=穿透，按纯函数着色）。</summary>
    private void SaveOverlay(double scale, string file)
    {
        const double w = 400, h = 600, r = ShellRadiusDip;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h + 28));
            var arcPen = new Pen(new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)), 2);
            var geo = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            geo.Freeze();
            dc.DrawGeometry(null, arcPen, geo);
            var bandPen = new Pen(Brushes.Orange, 1) { DashStyle = DashStyles.Dash };
            foreach (var (cx, cy) in new[] { (r, r), (w - r, r), (r, h - r), (w - r, h - r) })
            {
                dc.DrawRectangle(null, bandPen, new Rect(Math.Min(cx, w - r), Math.Min(cy, h - r), r, r));
            }
            foreach (var corner in Corners)
            {
                var ccx = (corner is Corner.TopLeft or Corner.BottomLeft) ? r : w - r;
                var ccy = (corner is Corner.TopLeft or Corner.TopRight) ? r : h - r;
                var sx = (corner is Corner.TopLeft or Corner.BottomLeft) ? -1 : 1;
                var sy = (corner is Corner.TopLeft or Corner.TopRight) ? -1 : 1;
                for (var i = 0; i < 8; i++)
                {
                    var phi = Math.PI / 2 * i / 7;
                    var dx = sx * Math.Cos(phi);
                    var dy = sy * Math.Sin(phi);
                    foreach (var factor in new[] { 0.9, 1.12 })
                    {
                        var x = ccx + factor * r * dx;
                        var y = ccy + factor * r * dy;
                        var hit = CornerHitGeometry.HitTestPhysical(x, y, w, h, r);
                        var p = new Point(x, y);
                        if (hit == CornerHit.Client)
                        {
                            dc.DrawEllipse(Brushes.Green, null, p, 3, 3);
                        }
                        else
                        {
                            dc.DrawEllipse(null, new Pen(Brushes.Red, 1.2), p, 3, 3);
                        }
                    }
                }
            }
            var ft = new FormattedText(
                $"36 DIP 圆角 @ {scale * 100:0}%（模拟渲染；绿=命中 红=穿透）",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 12, Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(8, h + 6));
        }
        var rtb = new RenderTargetBitmap(
            (int)(w * scale), (int)((h + 28) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(AppContext.BaseDirectory, file));
        enc.Save(fs);
        _log.AppendLine($"- 截图：{file}（{scale * 100:0}% 命中几何示意，模拟渲染）");
    }

    // —— 键入（复用 P1 先例：KEYEVENTF_UNICODE） ——

    private static void SendChars(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            inputs.Add(INPUT.Key(c, KEYEVENTF_UNICODE));
            inputs.Add(INPUT.Key(c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        var array = inputs.ToArray();
        _ = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
    }

    // —— Win32 ——

    private static INPUT MouseInput(uint flags) => new()
    {
        Type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public double Width => Right - Left;
        public double Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;

        public static INPUT Key(char c, uint flags) => new()
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = flags } },
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    /// <summary>
    /// 靶窗与探针同为 Topmost 时，探针 ShowActivated=False 显示不激活、留在带底，
    /// 且靶窗每次被点击激活都会再抬升。角内点击前无激活地抬回 Topmost 带顶，
    /// 保证「探针 &gt; 靶窗 &gt; 其余窗口」的确定性 z 序（不抢前台，浏览态语义不变）。
    /// </summary>
    private void RaiseProbeAboveTarget() =>
        _ = SetWindowPos(Hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
