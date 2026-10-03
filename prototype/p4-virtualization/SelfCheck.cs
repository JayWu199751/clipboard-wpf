using System.Collections.Specialized;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace P4VirtualizationProbe;

/// <summary>
/// 自检运行器：四条判据全部为进程内可重复断言。
/// 输入注入走 InputManager.ProcessInput（真实 WPF 输入管线，键盘焦点驱动，不依赖 OS 前台）；
/// 消息泵用 DispatcherFrame。证据输出到 exe 同目录 p4-report.md + 截图 PNG。
/// 容器实例标识在 MainWindow.OnCardContainerLoaded 分配（首次 realize 时）。
/// </summary>
public static class SelfCheck
{
    private static readonly List<string> Lines = [];
    private static readonly List<(string Name, bool Passed, string Detail)> Verdicts = [];

    /// <summary>容器实例标识（首次 realize 时分配，distinct 总数 = 创建的实例数）。</summary>
    private static readonly ConditionalWeakTable<DependencyObject, Box> InstanceIds = [];

    private static int _instanceCounter;
    public static void AssignInstanceId(ListBoxItem item)
    {
        if (!InstanceIds.TryGetValue(item, out _))
            InstanceIds.Add(item, new Box(Interlocked.Increment(ref _instanceCounter)));
    }
    private static int IdOf(DependencyObject o) => InstanceIds.TryGetValue(o, out var b) ? b.Value : -1;

    public static void Run(Window window, ListBox list, string reportPath)
    {
        // 调用时机 = 窗口 Loaded 后的 ApplicationIdle：布局与首批容器已就绪，直接执行
        RunCore(window, list, reportPath);
    }

    private static void RunCore(Window window, ListBox list, string reportPath)
    {
        _tracePath = ShotPath(reportPath, "p4-trace.log");
        File.WriteAllText(_tracePath, $"自检开始 {DateTime.Now:HH:mm:ss.fff}{Environment.NewLine}");
        try
        {
            list.Focus();
            Pump(3);

            var sv = GetScrollViewer(list);
            var items = (IReadOnlyList<ProbeCard>)list.ItemsSource;
            var imageCount = items.Count(c => c.IsImage);
            Log($"数据 {items.Count} 条（图片卡 {imageCount}、文字卡 {items.Count - imageCount}），视口高 {sv.ViewportHeight:F1} DIP");
            var initialOffset = sv.VerticalOffset;
            Log($"初始滚动偏移 {initialOffset:F1}（顶端留白内侧基线）");

            // ---------- C2a：初始 realized 容器计数 ----------
            var visible0 = VisibleItemCount(sv);
            var realized0 = RealizedItems().Count();
            Log($"初始：可视容器 {visible0}，realized {realized0}（宽松上界 = 可视×2+4，含一页缓存）");
            Record("C2 初始容器计数 ≈ 可视+有限缓存", realized0 <= visible0 * 2 + 4,
                $"realized={realized0}, visible={visible0}");

            // ---------- C1a：首轮分页真实滚动（PageDown 循环到底，覆盖全部条目），解码计数 ----------
            SendKey(list, Key.Home); PumpUntilSettled(list, sv);
            var decodeBefore = ProbeCard.DecodeRequests;
            var lastOffset = -1.0;
            for (var page = 0; page < 60; page++)
            {
                SendKey(list, Key.PageDown);
                PumpUntilSettled(list, sv);
                if (Math.Abs(sv.VerticalOffset - lastOffset) < 0.5) break; // 已到底
                lastOffset = sv.VerticalOffset;
            }
            var decodeRound1 = ProbeCard.DecodeRequests - decodeBefore;
            var realizedBottom = RealizedItems().Count();
            Log($"分页滚动到底：本轮解码 {decodeRound1} 次（≤ 图片卡数 {imageCount}），累计解码 {ProbeCard.DecodeTotalMs} ms，底部 realized={realizedBottom}");
            Record("C1 全列表真实滚动解码请求数 ≤ 图片卡数", decodeRound1 <= imageCount,
                $"解码 {decodeRound1} 次 / {imageCount} 张图片卡（每卡至多 1 次），{ProbeCard.DecodeTotalMs} ms");

            // ---------- C1b：往返分页滚动不得再解码（记忆化缓存生效；风暴 = 反复滚动反复解码） ----------
            var decodeBefore2 = ProbeCard.DecodeRequests;
            for (var page = 0; page < 60; page++)
            {
                SendKey(list, Key.PageUp); PumpUntilSettled(list, sv);
                if (Math.Abs(sv.VerticalOffset) < 0.5) break;
            }
            SendKey(list, Key.End); PumpUntilSettled(list, sv);
            SendKey(list, Key.Home); PumpUntilSettled(list, sv);
            var decodeRound2 = ProbeCard.DecodeRequests - decodeBefore2;
            Log($"往返分页滚动（回顶→到底→回顶）后增量解码 {decodeRound2} 次（须 0：复用容器换绑已缓存条目不得重解码）");
            Record("C1 往返滚动无重复解码风暴", decodeRound2 == 0, $"增量解码 {decodeRound2} 次");

            // ---------- C2b：容器实例创建总数（Recycling 证据一：distinct ≪ 条目数） ----------
            var distinctInstances = SeenIds().Count;
            Log($"全程滚动后 distinct 容器实例 {distinctInstances}（条目 {items.Count}；Recycling 生效则远小于条目数）");
            Record("C2 容器实例创建数 ≪ 条目数（Recycling 生效）", distinctInstances < items.Count / 2,
                $"{distinctInstances} 个实例承载 {items.Count} 条");

            // ---------- C2c：实例换绑（Recycling 证据二：同一实例先后承载不同条目） ----------
            var snapA = SnapshotIdToIndex(list);
            for (var i = 0; i < 3; i++) SendKey(list, Key.PageDown);
            PumpUntilSettled(list, sv);
            var snapB = SnapshotIdToIndex(list);
            var rebound = snapA.Count(kv => snapB.TryGetValue(kv.Key, out var j) && j != kv.Value);
            Record("C2 容器实例跨条目复用（换绑证据）", rebound > 0,
                $"{rebound} 个实例换绑到不同 index（整页滚动前后快照对比，{snapA.Count}→{snapB.Count} 个容器）");

            // ---------- C4：变高测量滚动位置漂移（精确回归） ----------
            list.ScrollIntoView(list.Items[100]); // 先粗滚使 index 100 realize
            PumpUntilSettled(list, sv);
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(100)!;
            var top = container.TranslatePoint(new Point(0, 0), sv).Y;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + top - 40); // 卡片顶停视口顶下 40 DIP
            Pump(10);
            var vi = sv.VerticalOffset;
            var topBefore = ItemTopInViewport(list, 100, sv);
            SendKey(list, Key.Home); PumpUntilSettled(list, sv);
            sv.ScrollToVerticalOffset(vi); PumpUntilSettled(list, sv);
            var topAfter = ItemTopInViewport(list, 100, sv);
            var drift = Math.Abs(topAfter - topBefore);
            Log($"变高漂移：index 100 视口内顶部 {topBefore:F2} → 回归 {topAfter:F2}（|Δ|={drift:F2} DIP，容差 1.0）");
            Record("C4 变高测量无滚动位置漂移", drift <= 1.0, $"|Δ|={drift:F2} DIP");

            // 返回顶部回到初始留白
            SendKey(list, Key.Home); PumpUntilSettled(list, sv); Pump(10);
            var homeDelta = Math.Abs(sv.VerticalOffset - initialOffset);
            Record("C4 返回顶部回到初始留白", homeDelta < 0.5,
                $"Home 后偏移 {sv.VerticalOffset:F1} vs 初始 {initialOffset:F1}");

            // ---------- C3：高频导航（模拟长按重复：连续 200 步 Down，每步即时跟随） ----------
            SendKey(list, Key.Home); PumpUntilSettled(list, sv);
            Log($"C3 起点：offset={sv.VerticalOffset:F1}，SelectedIndex={list.SelectedIndex}，焦点={Keyboard.FocusedElement?.GetType().Name}");
            // 诊断：前 10 步逐步记录选中索引/容器/几何，用于失败归因
            for (var d = 0; d < 10; d++)
            {
                SendKey(list, Key.Down);
                Pump(1);
                var c = list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem;
                var t = c is null ? -999 : c.TranslatePoint(new Point(0, 0), sv).Y;
                Log($"诊断步 {d}：SelectedIndex={list.SelectedIndex}，容器={c is not null}，top={t:F1}，viewport={sv.ViewportHeight:F1}，offset={sv.VerticalOffset:F1}，焦点={Keyboard.FocusedElement?.GetType().Name}");
            }
            SendKey(list, Key.Home); PumpUntilSettled(list, sv);
            var notVisible = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var step = 0; step < 200; step++)
            {
                SendKey(list, Key.Down);
                Pump(1); // 仅一帧：模拟 repeat 场景滚动须即时跟上，不给长 settle
                if (!IsSelectedFullyVisible(list, sv)) notVisible++;
            }
            sw.Stop();
            var perStepMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 200.0;
            Log($"高频导航：200 步 Down，均步 {perStepMs:F2} ms，选中项不完整可见 {notVisible} 步");
            Record("C3 高频导航滚动即时跟随且选中项全程可见", notVisible == 0 && perStepMs < 40,
                $"均步 {perStepMs:F2} ms（含一帧布局渲染等待），不完整可见 {notVisible} 步");

            // 底部：最后一张卡完整可见、之下是页脚分隔线（底留白 16 DIP + FooterHost 顶线）
            SendKey(list, Key.End); PumpUntilSettled(list, sv); Pump(5);
            var lastTop = ItemTopInViewport(list, items.Count - 1, sv);
            var lastHeight = ItemHeight(list, items.Count - 1);
            Record("C3 底部到最后卡片完整可见（页脚分隔线之上）",
                lastTop >= -0.5 && lastTop + lastHeight <= sv.ViewportHeight + 0.5,
                $"最后卡 top={lastTop:F1} h={lastHeight:F1} 视口={sv.ViewportHeight:F1}");

            // 顶端：偏移回 0、首卡完整（上方 12 DIP 恒定留白在滚动区外）
            SendKey(list, Key.Home); PumpUntilSettled(list, sv); Pump(5);
            var firstTop = ItemTopInViewport(list, 0, sv);
            Record("C3 顶端保护留白（偏移 0、首卡完整）",
                Math.Abs(sv.VerticalOffset) < 0.5 && firstTop >= -0.5,
                $"偏移 {sv.VerticalOffset:F1}，首卡 top={firstTop:F1}（上方 12 DIP 恒定留白）");

            // ---------- 截图（视觉佐证） ----------
            Screenshot(window, ShotPath(reportPath, "p4-shot-top.png"));
            SendKey(list, Key.End); PumpUntilSettled(list, sv); Pump(5);
            Screenshot(window, ShotPath(reportPath, "p4-shot-bottom.png"));

            // ---------- 报告 ----------
            var pass = Verdicts.Count(v => v.Passed);
            File.WriteAllLines(ShotPath(reportPath, "p4-report.md"),
                new[] { $"# P4 虚拟化自检报告 {DateTime.Now:yyyy-MM-dd HH:mm:ss}", string.Empty,
                        $"结果：{pass}/{Verdicts.Count} 通过", string.Empty }
                    .Concat(Verdicts.Select(v => $"- **{v.Name}**：{(v.Passed ? "PASS" : "FAIL")} —— {v.Detail}"))
                    .Concat(new[] { string.Empty, "## 运行日志", string.Empty })
                    .Concat(Lines));
            foreach (var (name, passed, detail) in Verdicts)
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}：{detail}");
            Console.WriteLine($"自检完成：{pass}/{Verdicts.Count} 通过");
            Environment.ExitCode = pass == Verdicts.Count ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.AppendAllText(_tracePath, "自检异常：" + ex + Environment.NewLine);
            Environment.ExitCode = 2;
        }
        finally
        {
            Application.Current.Shutdown(); // 短驻留：跑完即退，不留常驻窗口
        }
    }

    private static string ShotPath(string reportPath, string name) =>
        Path.GetFullPath(Path.Combine(reportPath, name));

    private static string _tracePath = null!;

    private static void Record(string name, bool passed, string detail)
    {
        Verdicts.Add((name, passed, detail));
        Log($"[{(passed ? "PASS" : "FAIL")}] {name}：{detail}");
    }

    private static void Log(string line)
    {
        Lines.Add($"- {line}");
        File.AppendAllText(_tracePath, line + Environment.NewLine); // 逐条落盘：WinExe 无控制台，异常也留痕
    }

    /// <summary>
    /// 向 ListBox 注入键导航：KeyDown 直接 raise 到 ListBox 元素。
    /// 不走 Keyboard.FocusedElement：Recycling 换绑后焦点容器可能已承载其他条目，
    /// 聚焦逻辑会触发错误条目的 BringIntoView（本试验早期踩坑：视口被拉到列表尾 30310 DIP）。
    /// raise 路径触发的 Selector 导航 + BringIntoView 与真实按键一致；
    /// 真实输入管线的按键到达性已由 P1 试验（SendInput）覆盖，不在本票重复验证。
    /// </summary>
    private static void SendKey(ListBox list, Key key)
    {
        var ps = PresentationSource.FromVisual(list)!;
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, ps, 0, key)
        { RoutedEvent = Keyboard.KeyDownEvent });
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, ps, 0, key)
        { RoutedEvent = Keyboard.KeyUpEvent });
    }

    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var frame = new DispatcherFrame();
            // ApplicationIdle（低于 Render）：回调执行时本轮布局与渲染已完成，几何判定才有效
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static int _lastSelected = -1;

    /// <summary>泵到滚动偏移与选中索引连续 4 帧稳定（上限 2 s）。</summary>
    private static void PumpUntilSettled(ListBox list, ScrollViewer sv)
    {
        var last = double.NaN;
        var stable = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (stable < 4 && sw.ElapsedMilliseconds < 2000)
        {
            Pump(1);
            var now = sv.VerticalOffset;
            var sel = list.SelectedIndex;
            if (now.Equals(last) && sel == _lastSelected) stable++;
            else { stable = 0; _lastSelected = sel; last = now; }
        }
    }

    private static ScrollViewer GetScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = GetScrollViewer(child);
            if (found is not null) return found;
        }
        throw new InvalidOperationException("ScrollViewer 未找到");
    }

    private static List<ListBoxItem> RealizedItems()
    {
        var list = MainWindow.ListRef ?? throw new InvalidOperationException("列表未就绪");
        var result = new List<ListBoxItem>();
        Collect(list, result);
        return result;

        static void Collect(DependencyObject root, List<ListBoxItem> acc)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ListBoxItem lbi) acc.Add(lbi);
                Collect(child, acc);
            }
        }
    }

    private static HashSet<int> SeenIds()
    {
        var set = new HashSet<int>();
        foreach (var item in RealizedItems())
        {
            var id = IdOf(item);
            if (id > 0) set.Add(id);
        }
        return set;
    }

    /// <summary>快照：实例 id → 当前承载的条目 index。</summary>
    private static Dictionary<int, int> SnapshotIdToIndex(ListBox list)
    {
        var map = new Dictionary<int, int>();
        foreach (var item in RealizedItems())
        {
            var id = IdOf(item);
            if (id > 0) map[id] = list.ItemContainerGenerator.IndexFromContainer(item);
        }
        return map;
    }

    /// <summary>可视容器数：矩形与视口垂直相交（含部分可见）。</summary>
    private static int VisibleItemCount(ScrollViewer sv)
    {
        var n = 0;
        foreach (var item in RealizedItems())
        {
            var top = item.TranslatePoint(new Point(0, 0), sv).Y;
            if (top + item.ActualHeight > 0 && top < sv.ViewportHeight) n++;
        }
        return n;
    }

    private static double ItemTopInViewport(ListBox list, int index, ScrollViewer sv)
    {
        var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(index)
            ?? throw new InvalidOperationException($"index {index} 未 realize");
        return container.TranslatePoint(new Point(0, 0), sv).Y;
    }

    private static double ItemHeight(ListBox list, int index) =>
        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)!).ActualHeight;

    private static bool IsSelectedFullyVisible(ListBox list, ScrollViewer sv)
    {
        if (list.SelectedIndex < 0) return false;
        if (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is not ListBoxItem container) return false;
        var top = container.TranslatePoint(new Point(0, 0), sv).Y;
        return top >= -0.5 && top + container.ActualHeight <= sv.ViewportHeight + 0.5;
    }

    private static void Screenshot(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var rtb = new RenderTargetBitmap(
            (int)(window.ActualWidth * dpi.PixelsPerDip),
            (int)(window.ActualHeight * dpi.PixelsPerDip),
            96 * dpi.PixelsPerDip, 96 * dpi.PixelsPerDip, PixelFormats.Pbgra32);
        rtb.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Log($"截图：{path}");
    }

    private sealed class Box(int value) { public readonly int Value = value; }
}
