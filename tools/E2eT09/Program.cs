using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

// T09 工单 09 真机端到端验证（自动化部分，T02 E2E 产线思路）：
//   ① 真实屏幕截图（GDI CopyFromScreen → CF_DIB）与合成透明截图（圆角/半透明 → CF_DIBV5）
//      入库、落盘 PNG 像素逐一比对、缩略图渲染（面板截屏像素统计）、同图重复制去重不重写、
//      双击卡片复制后剪贴板 CF_DIBV5 回读保透明（Office/画图目测留人工）。
//   ② 205 条混合批量（含 6 张 4K DIB）入库触发 200 上限裁剪；4K 混合列表滚轮滚动：
//      容器诊断（CLIPBOARDTOOL_E2E_DIAG，P4 方法 distinct InstanceId + 换绑快照）+ 进程内存观察点。
//   ③ 裁剪联动删 PNG（文件系统断言）、Del 热键删条目联动删 PNG、逐条删除清空历史后 images/ 无残留。
// 人工项：粘贴到 Office/画图的目测、来源应用 meta、滚动手感主观观感。

const uint CfUnicodeText = 13;
const uint CfDib = 8;
const uint CfDibV5 = 17;

var failures = new List<string>();
var notes = new List<string>();
var runDir = Path.Combine(Path.GetTempPath(), "e2e-t09-run-" + DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(runDir);
var diagPath = Path.Combine(runDir, "containers.log");
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool");
var imagesDir = Path.Combine(dataDir, "images");
var backupDir = Path.Combine(runDir, "data-backup");

_ = Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

// ---------- 0) 前置：无运行实例；备份真实数据并清空（E2E 结束原样恢复） ----------
if (Process.GetProcessesByName("ClipboardTool").Length > 0)
{
    Console.WriteLine("已存在 ClipboardTool 运行实例，为不干扰真实使用中止 E2E。");
    return 2;
}
BackupDataDir(dataDir, backupDir);
WipeDataDir(dataDir);

var app = Process.Start(new ProcessStartInfo(FindAppExe())
{
    UseShellExecute = false,
    EnvironmentVariables = { ["CLIPBOARDTOOL_E2E_DIAG"] = diagPath },
})!;
Process? notepad = null;
var navCycle = 0; // 每进程内呼出轮次：呼出键恒 id=1，导航热键每轮重注册，第 k 轮 id = 8(k-1)+2..9
try
{
    var panelHwnd = WaitPanelWindow(app, TimeSpan.FromSeconds(10));
    Check(panelHwnd != IntPtr.Zero, "应用启动且面板窗口存在");

    notepad = LaunchNotepad();
    var notepadHwnd = WaitForNotepadWindow(notepad, TimeSpan.FromSeconds(10));
    Check(notepadHwnd != IntPtr.Zero, "粘贴目标记事本已启动");
    Thread.Sleep(300);
    ForceForeground(notepadHwnd); // 呼出快照落在记事本：双击粘贴链路只可能注入这里

    // ---------- 1) 呼出（第 1 轮） ----------
    Native.PostMessage(panelHwnd, 0x0312, (IntPtr)1, IntPtr.Zero); // WM_HOTKEY id=1 = 呼出键
    Thread.Sleep(800);
    navCycle = 1;
    Check(IsPanelOnScreen(app), "呼出落地（面板矩形回到屏内）");

    // ---------- 2) ①系统截图入库：GDI 屏幕捕获 → CF_DIB ----------
    var (shotW, shotH, shotRgba) = CaptureScreen(1024, 768);
    SetClipboardBytes(CfDib, BuildCfDib(shotRgba, shotW, shotH));
    var shot1 = WaitNewImageFile(null, TimeSpan.FromSeconds(8));
    Check(shot1 is not null, "系统截图（CF_DIB）入库生成 PNG");
    if (shot1 is { } shotFile)
    {
        var (ok, diff) = ComparePngWithRgba(shotFile, shotW, shotH, shotRgba);
        Check(ok, $"系统截图落盘 PNG 像素与截屏源一致（差异 {diff} 像素）");
        notes.Add($"截图 PNG：{Path.GetFileName(shotFile)}");
    }

    // ---------- 3) ①透明截图（圆角 alpha=0 + 半透明带 alpha=120）→ CF_DIBV5 ----------
    const int tw = 480, th = 320;
    var (transRgba, cornerDesc) = BuildTransparentRgba(tw, th);
    SetClipboardBytes(CfDibV5, BuildCfDibV5(transRgba, tw, th));
    var transFile = WaitNewImageFile(shot1, TimeSpan.FromSeconds(8));
    Check(transFile is not null, "透明截图（CF_DIBV5）入库生成 PNG");
    if (transFile is { } tf)
    {
        Check(ComparePngWithRgba(tf, tw, th, transRgba).Ok, "透明截图落盘 PNG 保真（圆角 alpha=0、半透明带 alpha≈120、正文不透明）");
        notes.Add($"透明 PNG：{Path.GetFileName(tf)}（{cornerDesc}）");

        // 缩略图渲染：图卡矩形截屏 → 像素统计（色彩数与标准差 = 棋盘格底 + 图已画）
        var card = FindCardByFileName(app, Path.GetFileName(tf));
        Check(card is not null, "图卡可经文件名定位（UIA）");
        if (card is not null)
        {
            Thread.Sleep(1200); // 解码回调归队渲染
            var stats = CaptureCardStats(card);
            Check(stats is { } s && s.DistinctColors >= 8 && s.StdDev >= 8,
                $"缩略图已渲染（distinct colors={stats?.DistinctColors ?? -1}, σ={stats?.StdDev ?? -1:0.0}）");
            if (stats is { } st)
            {
                notes.Add($"缩略图统计：colors={st.DistinctColors}, σ={st.StdDev:0.0}, mean=({st.MeanR:0},{st.MeanG:0},{st.MeanB:0})");
            }
        }
    }

    var imagesBefore = Directory.GetFiles(imagesDir).Length;
    var transBytesBefore = File.ReadAllBytes(transFile!);

    // ---------- 4) ①去重：同一透明图再次复制 → 不新增条目不重写文件 ----------
    SetClipboardBytes(CfDibV5, BuildCfDibV5(transRgba, tw, th));
    Thread.Sleep(1800);
    Check(Directory.GetFiles(imagesDir).Length == imagesBefore, "同图重复制不新增 PNG（身份哈希命中）");
    Check(FooterCount(app) == imagesBefore, "同图重复制不新增条目（页脚计数不变）");
    Check(File.ReadAllBytes(transFile!).SequenceEqual(transBytesBefore), "命中沿用原文件（字节未重写）");

    // ---------- 5) ①复制保透明：双击图卡 → 剪贴板 CF_DIBV5 回读 ----------
    var target = FindCardByFileName(app, Path.GetFileName(transFile!));
    if (target is null)
    {
        Check(false, "双击目标图卡存在");
    }
    else
    {
        ForceForeground(notepadHwnd);
        Thread.Sleep(200);
        var r = target.Current.BoundingRectangle;
        Native.DoubleClick((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        var pasted = WaitClipboardDibV5(TimeSpan.FromSeconds(8));
        Check(pasted is not null, "双击图卡：复制链路写出 CF_DIBV5");
        if (pasted is { } dib)
        {
            var (ok, msg) = CompareDibV5WithRgba(dib, tw, th, transRgba);
            Check(ok, $"复制链路位图内容保透明（{msg}）");
        }
        Check(WaitPanelDocked(app, TimeSpan.FromSeconds(5)), "粘贴成功后面板停靠（hide_after_paste）");
    }

    // ---------- 6) ②批量入库：3 小图 + 6 张 4K + 49 变尺寸图 + 145 文字 + 1 张置顶图 = 206 条触发裁剪 ----------
    // 条目序（最新在前）全程记账：上限裁剪恒删最旧普通条目 = 序列尾部；去重提升会搬动位置
    var entryOrder = new List<string>();
    if (shot1 is { } s1 && transFile is { } t1)
    {
        entryOrder.Add(Path.GetFileName(t1)); // 第 4 步同图重复制把透明图提升到普通块最前
        entryOrder.Add(Path.GetFileName(s1));
    }
    for (var i = 0; i < 3; i++)
    {
        var (fw, fh, frgba) = SmallRgba(256, 256, i * 17 + 1, opaque: true);
        SetClipboardBytes(CfDib, BuildCfDib(frgba, fw, fh));
        var f = WaitNewImageFileIncrement(TimeSpan.FromSeconds(8));
        Check(f is not null, $"占位小图 {i + 1}/3 入库");
        if (f is not null)
        {
            entryOrder.Insert(0, Path.GetFileName(f));
        }
    }
    var first4kName = string.Empty;
    for (var i = 0; i < 6; i++)
    {
        var (kw, kh, krgba) = Large4kRgba(i);
        var sw = Stopwatch.StartNew();
        SetClipboardBytes(CfDib, BuildCfDib(krgba, kw, kh));
        var f = WaitNewImageFileIncrement(TimeSpan.FromSeconds(20));
        Check(f is not null, $"4K 图 {i + 1}/6 入库（{sw.ElapsedMilliseconds} ms，含写剪贴板与解码编码）");
        if (f is not null)
        {
            if (i == 0)
            {
                first4kName = Path.GetFileName(f);
            }
            entryOrder.Insert(0, Path.GetFileName(f));
        }
        krgba = null;
        GC.Collect();
    }
    for (var i = 0; i < 49; i++)
    {
        var w = 200 + (i * 53) % 420;
        var h = 160 + (i * 31) % 460;
        var (vw, vh, vrgba) = SmallRgba(w, h, i * 7 + 3, opaque: i % 3 != 0);
        SetClipboardBytes(CfDibV5, BuildCfDibV5(vrgba, vw, vh));
        if (WaitNewImageFileIncrement(TimeSpan.FromSeconds(8)) is { } f)
        {
            entryOrder.Insert(0, Path.GetFileName(f));
        }
        else
        {
            Check(false, $"变尺寸图 {i + 1}/49 入库");
        }
    }
    var textFailures = 0;
    for (var i = 0; i < 145; i++)
    {
        SetClipboardText($"E2E-T09-文字-{i:D4}-" + new string('x', (i % 60) + 10));
        // 上限裁剪使页脚计数封顶 200：目标取 min(期望, 200)；连续失败才中止（防应用崩溃刷屏）
        if (!WaitFooterReaches(app, Math.Min(i + 61, 200), TimeSpan.FromSeconds(8)))
        {
            textFailures++;
            if (textFailures >= 5)
            {
                Check(false, $"文字 {i + 1}/145 入库连续失败（页脚 {FooterCount(app)}）");
                break;
            }
        }
        else
        {
            textFailures = 0;
        }
    }
    // 置顶删除目标图（最新条目，呼出即实现，无需深滚动）。
    // 注意「写盘 + 裁剪」原子执行：文件数 56 的瞬态不可观测，必须按文件集合差等待；
    // 页脚计数已封顶 200，文字段可能有入队积压，等待窗口放宽到 30s
    string? freshImage = null;
    {
        var (dw, dh, drgba) = SmallRgba(320, 240, 991, opaque: false);
        SetClipboardBytes(CfDibV5, BuildCfDibV5(drgba, dw, dh));
        var before = Directory.Exists(imagesDir) ? Directory.GetFiles(imagesDir).ToHashSet() : [];
        freshImage = WaitFor(() => Directory.Exists(imagesDir)
            ? Directory.GetFiles(imagesDir).FirstOrDefault(f => !before.Contains(f))
            : null, 30_000);
        if (freshImage is { } f)
        {
            entryOrder.Insert(0, Path.GetFileName(f));
        }
        else
        {
            Check(false, "置顶删除目标图入库");
        }
    }
    var finalCount = FooterCount(app);
    Check(finalCount == 200, $"206 条入库后页脚计数 = 200（实测 {finalCount}）");
    Check(WaitUntil(() => Directory.GetFiles(imagesDir).Length == 55, 20_000),
        $"上限裁剪后 images/ 文件数 = 55（实测 {Directory.GetFiles(imagesDir).Length}）");
    var imageFiles = Directory.GetFiles(imagesDir);
    var actualNames = imageFiles.Select(Path.GetFileName).ToHashSet();
    var trimmedExpected = entryOrder.TakeLast(6).ToList();
    var trimmedGone = trimmedExpected.Where(name => !actualNames.Contains(name)).ToList();
    Check(trimmedGone.Count == 6, $"裁剪的 6 张最旧 PNG 已从文件系统删除（{trimmedGone.Count}/6）");
    notes.Add($"裁剪删除的 PNG：{string.Join(", ", trimmedExpected)}");

    // ---------- 7) ②滚动：容器计数 + 换绑快照 + 内存观察点（进程 1 第 2 轮呼出） ----------
    Native.PostMessage(panelHwnd, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(800);
    navCycle = 2;
    Check(IsPanelOnScreen(app), "再次呼出落地");
    var downId = NavHotkeyId(navCycle, Nav.Down);
    Check(ProbeDownHotkey(app, panelHwnd, downId), $"导航热键 id 映射校验（Down = {downId}）");

    var memBefore = MemSnapshot(app);
    // 只统计滚动窗口的诊断（入库期 ReloadEntries 重建容器会污染全程 distinct 计数）
    var diagOffset = File.Exists(diagPath) ? new FileInfo(diagPath).Length : 0;
    var rect = PanelRect(app);
    var cx = (int)(rect.Left + rect.Width / 2);
    var cy = (int)(rect.Top + rect.Height * 0.45);
    var batchMs = new List<long>();
    for (var i = 0; i < 90; i++)
    {
        var sw = Stopwatch.StartNew();
        Native.Wheel(cx, cy, -1200); // 10 齿/批
        Thread.Sleep(90);
        batchMs.Add(sw.ElapsedMilliseconds);
    }
    Native.Wheel(cx, cy, 1200 * 120); // 快速回滚一段（证据取下行段）
    Thread.Sleep(600);
    var memAfter = MemSnapshot(app);
    var diag = ParseDiag(diagPath, diagOffset);
    Check(diag.MaxRealized > 0 && diag.MaxRealized <= 30,
        $"滚动期已实现容器峰值 = {diag.MaxRealized}（可视 + 有限缓存，≤30）");
    Check(diag.DistinctInstances > 0 && diag.DistinctInstances <= 30,
        $"滚动期 distinct InstanceId = {diag.DistinctInstances}（Recycling 复用，远小于 200）");
    Check(diag.MaxBoundIndex >= 50, $"滚动实际推进（最深绑定 index = {diag.MaxBoundIndex}）");
    Check(diag.RebindEvidence, "换绑快照：同一 InstanceId 先后绑定不同 index（诊断文件在案）");
    Check(batchMs.Average() < 500, $"滚动跟手（90 批滚轮平均 {batchMs.Average():0.0} ms/批，最大 {batchMs.Max()}）");
    notes.Add($"内存观察点：滚动前 WS={MiB(memBefore.WorkingSet):0.0}/Priv={MiB(memBefore.Private):0.0} MiB；" +
              $"滚动后 WS={MiB(memAfter.WorkingSet):0.0}/Priv={MiB(memAfter.Private):0.0} MiB（正式门槛推迟）");
    notes.Add($"容器诊断文件：{diagPath}（滚动段 realized 峰值 {diag.MaxRealized}，distinct {diag.DistinctInstances}）");

    // ---------- 8) 重启复核（存档重载 + PNG 复用；热键计数器随新进程重置） ----------
    app.Kill();
    app.WaitForExit(5000);
    app = Process.Start(new ProcessStartInfo(FindAppExe())
    {
        UseShellExecute = false,
        EnvironmentVariables = { ["CLIPBOARDTOOL_E2E_DIAG"] = diagPath + ".2" },
    })!;
    panelHwnd = WaitPanelWindow(app, TimeSpan.FromSeconds(10));
    Check(panelHwnd != IntPtr.Zero, "重启后面板窗口存在");
    Native.PostMessage(panelHwnd, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(800);
    navCycle = 1;
    Check(FooterCount(app) == 200, $"重启后存档重载 200 条（实测 {FooterCount(app)}）");

    // ---------- 9) ③Del 删条目联动删 PNG；逐条删除清空后 images/ 无残留 ----------
    var delId = NavHotkeyId(navCycle, Nav.Delete);
    Check(ProbeDownHotkey(app, panelHwnd, NavHotkeyId(navCycle, Nav.Down)),
        $"重启后导航热键映射校验（Down = {NavHotkeyId(navCycle, Nav.Down)}）");
    // 重启后剪贴板仍残留置顶图 → 首轮轮询重录制（去重提升）触发容器重建：等 1.5s 让重建尘埃落定
    Thread.Sleep(1500);
    var delTargetName = entryOrder[0];
    var delTargetPath = Path.Combine(imagesDir, delTargetName);
    Check(File.Exists(delTargetPath), $"待删目标 PNG 在盘（{delTargetName}）");
    var delDeleted = DeleteTopImageByDel(app, panelHwnd, delId, delTargetName, delTargetPath, 199);
    Check(delDeleted, $"Del 删条目生效并联动删 PNG（id={delId}，计数 → 199，撤销窗口后无该图）");

    // 清空历史：逐条「选中最高已实现卡 → Del」（全部走 UI 路径；托盘清空入口 T07 接线）。
    // run4 实测：持续删除时撤销窗口到期连锁引发容器持续重建，约 20 条并发待删后 Del 开始无效果——
    // 故分批推进：批内 12 条；Del 无效果则停靠排空全部撤销窗口（7.5s）→ 重新呼出（热键重映射）再续
    var cleared = true;
    var remaining = 198; // 第 9 步已删 1 条（计数 199）
    var drains = 0;
    var clearWatch = Stopwatch.StartNew();
    while (remaining >= 0 && clearWatch.Elapsed < TimeSpan.FromMinutes(10))
    {
        var ok = false;
        for (var attempt = 0; attempt < 3 && !ok; attempt++)
        {
            var card = TopCard(app);
            if (card is null || !SelectCard(card))
            {
                Thread.Sleep(250);
                continue;
            }
            Thread.Sleep(80);
            Native.PostMessage(panelHwnd, 0x0312, (IntPtr)delId, IntPtr.Zero);
            ok = WaitFooterReaches(app, remaining, TimeSpan.FromSeconds(3));
        }
        if (ok)
        {
            remaining--;
            drains = 0;
            continue;
        }
        drains++;
        if (drains > 3)
        {
            cleared = false;
            Check(false, $"清空循环删除失败（剩 {remaining + 1} 条，页脚 {FooterCount(app)}）");
            break;
        }
        Native.PostMessage(panelHwnd, 0x0312, (IntPtr)1, IntPtr.Zero); // 停靠
        Thread.Sleep(7500); // 全部撤销窗口到期真删（PendingDeletion 跨停靠计时）
        Native.PostMessage(panelHwnd, 0x0312, (IntPtr)1, IntPtr.Zero); // 重新呼出
        Thread.Sleep(1200);
        navCycle++;
        delId = NavHotkeyId(navCycle, Nav.Delete);
        Check(ProbeDownHotkey(app, panelHwnd, NavHotkeyId(navCycle, Nav.Down)),
            $"重新呼出后导航热键映射校验（Down = {NavHotkeyId(navCycle, Nav.Down)}）");
    }
    Check(cleared && remaining < 0 && FooterCount(app) == 0, "逐条删除清空历史（页脚 0 条）");
    Thread.Sleep(7200); // 最后一批撤销窗口到期真删
    Check(Directory.Exists(imagesDir) == false || Directory.GetFiles(imagesDir).Length == 0,
        "清空历史后 images/ 无残留（文件系统断言）");
}
catch (Exception ex)
{
    failures.Add("异常：" + ex.Message);
    Console.WriteLine(ex);
}
finally
{
    try { notepad?.CloseMainWindow(); } catch { }
    try { notepad?.Kill(); } catch { }
    try { app.CloseMainWindow(); } catch { }
    try { app.Kill(); } catch { }
    app.WaitForExit(5000);
    foreach (var p in Process.GetProcessesByName("ClipboardTool"))
    {
        try { p.Kill(); } catch { }
    }
    WipeDataDir(dataDir);
    RestoreDataDir(backupDir, dataDir);
}

Console.WriteLine();
foreach (var n in notes)
{
    Console.WriteLine("NOTE " + n);
}
Console.WriteLine(failures.Count == 0 ? "E2E 全部通过" : $"E2E 失败 {failures.Count} 项：");
foreach (var f in failures)
{
    Console.WriteLine("FAIL " + f);
}
return failures.Count == 0 ? 0 : 1;

// ============================ 断言与等待 ============================

void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok)
    {
        failures.Add(name);
    }
}

static bool WaitUntil(Func<bool> cond, int timeoutMs) =>
    WaitFor(() => cond() ? new object() : null, timeoutMs) is not null;

static T? WaitFor<T>(Func<T?> probe, int timeoutMs) where T : class
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (probe() is { } hit)
        {
            return hit;
        }
        Thread.Sleep(100);
    }
    return probe();
}

static bool WaitFooterReaches(Process app, int count, TimeSpan timeout) =>
    WaitUntil(() => FooterCount(app) == count, (int)timeout.TotalMilliseconds);

static string? WaitNewImageFile(string? previous, TimeSpan timeout)
{
    var images = ImagesDir();
    var before = Directory.Exists(images)
        ? Directory.GetFiles(images).ToHashSet()
        : [];
    return WaitFor(() => Directory.Exists(images)
        ? Directory.GetFiles(images).FirstOrDefault(f => !before.Contains(f))
        : null, (int)timeout.TotalMilliseconds);
}

static string? WaitNewImageFileIncrement(TimeSpan timeout)
{
    var images = ImagesDir();
    var before = Directory.Exists(images) ? Directory.GetFiles(images).Length : 0;
    return WaitFor(() =>
    {
        var now = Directory.Exists(images) ? Directory.GetFiles(images) : [];
        if (now.Length <= before)
        {
            return null;
        }
        return now.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }, (int)timeout.TotalMilliseconds);
}

static byte[]? WaitClipboardDibV5(TimeSpan timeout) =>
    WaitFor(() =>
    {
        var dib = ReadClipboardBytes(CfDibV5);
        return dib is not null && dib.Length > 124 + 16 ? dib : null;
    }, (int)timeout.TotalMilliseconds);

static string ImagesDir() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool", "images");

// ============================ 热键/卡片操作 ============================

static int NavHotkeyId(int cycle, Nav nav) => 8 * (cycle - 1) + (int)nav;

/// <summary>Down 热键行为校验：按下后选中卡改变（id 映射正确性的行为证据）。</summary>
static bool ProbeDownHotkey(Process app, IntPtr panelHwnd, int downId)
{
    var before = SelectedCard(app);
    Native.PostMessage(panelHwnd, 0x0312, (IntPtr)downId, IntPtr.Zero);
    Thread.Sleep(400);
    var after = SelectedCard(app);
    return before is not null && after is not null && !before.Equals(after);
}

static AutomationElement? SelectedCard(Process app)
{
    try
    {
        foreach (var item in ListItems(app))
        {
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) &&
                ((SelectionItemPattern)p).Current.IsSelected)
            {
                return item;
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

static AutomationElement? TopCard(Process app)
{
    AutomationElement? top = null;
    foreach (var item in ListItems(app))
    {
        try
        {
            // 虚拟化占位元素（VirtualizedItem）无矩形：跳过，只取已实现容器
            if (item.Current.BoundingRectangle.Height <= 0)
            {
                continue;
            }
            if (top is null || item.Current.BoundingRectangle.Top < top.Current.BoundingRectangle.Top)
            {
                top = item;
            }
        }
        catch (ElementNotAvailableException) { }
    }
    return top;
}

static bool SelectCard(AutomationElement card)
{
    try
    {
        if (card.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p))
        {
            ((SelectionItemPattern)p).Select();
            return true;
        }
    }
    catch (ElementNotAvailableException) { }
    return false;
}

/// <summary>选中指定文件名的图卡 → Del 热键 → 页脚计数落到 expected → 撤销窗口后 PNG 消失。
/// 删除请求落在容器重建竞态窗口会无效（run3 实测）：最多 3 次「重选中 + 重发 Del」。</summary>
static bool DeleteTopImageByDel(Process app, IntPtr panelHwnd, int delId, string fileName, string pngPath, int expectedCount)
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        var card = FindCardByFileName(app, fileName);
        if (card is null || !SelectCard(card))
        {
            Thread.Sleep(400);
            continue;
        }
        Thread.Sleep(150);
        Native.PostMessage(panelHwnd, 0x0312, (IntPtr)delId, IntPtr.Zero);
        if (WaitFooterReaches(app, expectedCount, TimeSpan.FromSeconds(3)) &&
            WaitUntil(() => !File.Exists(pngPath), 8000))
        {
            return true;
        }
    }
    return false;
}

static AutomationElement? FindCardByFileName(Process app, string fileName)
{
    try
    {
        var root = PanelRoot(app);
        if (root is null)
        {
            return null;
        }
        var texts = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        foreach (AutomationElement text in texts)
        {
            if (text.Current.Name == fileName)
            {
                var walker = TreeWalker.ControlViewWalker;
                var node = text;
                while (node is not null)
                {
                    if (node.Current.ControlType == ControlType.ListItem)
                    {
                        return node;
                    }
                    node = walker.GetParent(node);
                }
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

static IReadOnlyList<AutomationElement> ListItems(Process app)
{
    try
    {
        var root = PanelRoot(app);
        if (root is null)
        {
            return [];
        }
        var items = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        var list = new List<AutomationElement>(items.Count);
        foreach (AutomationElement item in items)
        {
            list.Add(item);
        }
        return list;
    }
    catch (ElementNotAvailableException)
    {
        return [];
    }
}

static AutomationElement? PanelRoot(Process app)
{
    try
    {
        return AutomationElement.RootElement.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
    }
    catch (ElementNotAvailableException)
    {
        return null;
    }
}

static Rect PanelRect(Process app) => PanelRoot(app)?.Current.BoundingRectangle ?? default;

static bool IsPanelOnScreen(Process app)
{
    var r = PanelRect(app);
    return r.Width > 0 && r.Left >= 0 && r.Left < 2000;
}

static bool WaitPanelDocked(Process app, TimeSpan timeout) =>
    WaitUntil(() =>
    {
        var r = PanelRect(app);
        return r.Width == 0 || r.Left >= 2000;
    }, (int)timeout.TotalMilliseconds);

/// <summary>页脚计数「N 条」（CountText 单源）。</summary>
static int FooterCount(Process app)
{
    try
    {
        var root = PanelRoot(app);
        if (root is null)
        {
            return -1;
        }
        var texts = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        foreach (AutomationElement text in texts)
        {
            var m = Regex.Match(text.Current.Name, @"^(\d+) 条$");
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value);
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return -1;
}

// ============================ 容器诊断解析（P4 方法证据） ============================

static (int MaxRealized, int DistinctInstances, int MaxBoundIndex, bool RebindEvidence) ParseDiag(string path, long fromOffset = 0)
{
    var instances = new HashSet<string>();
    var seenIndex = new Dictionary<string, HashSet<int>>();
    var maxRealized = 0;
    var maxIndex = -1;
    if (!File.Exists(path))
    {
        return (0, 0, -1, false);
    }
    var bytes = File.ReadAllBytes(path);
    var text = Encoding.UTF8.GetString(bytes, (int)Math.Min(fromOffset, bytes.Length), (int)(bytes.Length - Math.Min(fromOffset, bytes.Length)));
    foreach (var line in text.Split('\n'))
    {
        var m = Regex.Match(line, @"realized=(\d+) bind=(.*)$");
        if (!m.Success)
        {
            continue;
        }
        maxRealized = Math.Max(maxRealized, int.Parse(m.Groups[1].Value));
        foreach (var pair in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split(':');
            if (kv.Length != 2 || !int.TryParse(kv[1], out var idx))
            {
                continue;
            }
            instances.Add(kv[0]);
            maxIndex = Math.Max(maxIndex, idx);
            if (!seenIndex.TryGetValue(kv[0], out var set))
            {
                set = [];
                seenIndex[kv[0]] = set;
            }
            set.Add(idx);
        }
    }
    var rebind = seenIndex.Values.Any(set => set.Count >= 2);
    return (maxRealized, instances.Count, maxIndex, rebind);
}

// ============================ 像素/图像 ============================

static (int W, int H, byte[] Rgba) CaptureScreen(int w, int h)
{
    using var bmp = new Bitmap(w, h);
    using var g = Graphics.FromImage(bmp);
    g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h));
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var bgra = new byte[w * h * 4];
    Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
    bmp.UnlockBits(data);
    var rgba = new byte[bgra.Length];
    for (var i = 0; i < bgra.Length; i += 4)
    {
        rgba[i] = bgra[i + 2];
        rgba[i + 1] = bgra[i + 1];
        rgba[i + 2] = bgra[i];
        rgba[i + 3] = 255; // GDI 屏幕捕获无 alpha
    }
    return (w, h, rgba);
}

/// <summary>合成透明截图：圆角外 alpha=0、y∈[100,140) 半透明带 alpha=120、正文不透明图案。</summary>
static (byte[] Rgba, string Desc) BuildTransparentRgba(int w, int h)
{
    var rgba = new byte[w * h * 4];
    const int radius = 48;
    const int bandTop = 100, bandBottom = 140;
    const byte bandAlpha = 120;
    for (var y = 0; y < h; y++)
    {
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            byte a;
            if (InCorner(x, y, radius) || InCorner(w - 1 - x, y, radius) ||
                InCorner(x, h - 1 - y, radius) || InCorner(w - 1 - x, h - 1 - y, radius))
            {
                a = 0; // 圆角外全透明
            }
            else if (y >= bandTop && y < bandBottom)
            {
                a = bandAlpha; // 半透明带
            }
            else
            {
                a = 255;
            }
            rgba[i] = (byte)((x * 7 + y * 3) % 256);
            rgba[i + 1] = (byte)((x * 2 + y * 11) % 256);
            rgba[i + 2] = (byte)((x + y) % 256);
            rgba[i + 3] = a;
        }
    }
    return (rgba, $"四角({radius}px)圆角透明 + 半透明带(alpha={bandAlpha})");

    static bool InCorner(int x, int y, int r) =>
        x < r && y < r && (x - r) * (x - r) + (y - r) * (y - r) > r * r;
}

static (int W, int H, byte[] Rgba) SmallRgba(int w, int h, int seed, bool opaque)
{
    var rgba = new byte[w * h * 4];
    for (var y = 0; y < h; y++)
    {
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            rgba[i] = (byte)((x + seed) % 256);
            rgba[i + 1] = (byte)((y + seed * 3) % 256);
            rgba[i + 2] = (byte)((x * y + seed) % 256);
            rgba[i + 3] = (opaque || (x + y) % 8 != 0) ? (byte)255 : (byte)64;
        }
    }
    return (w, h, rgba);
}

/// <summary>4K 渐变 + 条带（PNG 可压缩，重点在链路而非压缩率）。</summary>
static (int W, int H, byte[] Rgba) Large4kRgba(int seed)
{
    const int w = 3840, h = 2160;
    var rgba = new byte[w * h * 4];
    for (var y = 0; y < h; y++)
    {
        var stripe = (byte)(y % 16 < 8 ? 12 : 0);
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            rgba[i] = (byte)(x / 16 + seed * 30 + stripe);
            rgba[i + 1] = (byte)(y / 12 + seed * 20);
            rgba[i + 2] = (byte)((x + y) / 24 + seed * 10);
            rgba[i + 3] = 255;
        }
    }
    return (w, h, rgba);
}

/// <summary>DIB（BITMAPINFOHEADER 40，BI_RGB，32bpp，自下而上）——真实截图工具的 CF_DIB 形状。</summary>
static byte[] BuildCfDib(byte[] rgba, int w, int h)
{
    var dib = new byte[40 + w * h * 4];
    WriteU32(dib, 0, 40);
    WriteU32(dib, 4, (uint)w);
    WriteI32(dib, 8, h); // 正高度 = 自下而上
    WriteU16(dib, 12, 1);
    WriteU16(dib, 14, 32);
    WriteU32(dib, 16, 0); // BI_RGB
    WriteU32(dib, 20, (uint)(w * h * 4));
    for (var y = 0; y < h; y++)
    {
        var src = (h - 1 - y) * w * 4;
        var dst = 40 + y * w * 4;
        for (var x = 0; x < w; x++)
        {
            dib[dst] = rgba[src + 2];     // B
            dib[dst + 1] = rgba[src + 1]; // G
            dib[dst + 2] = rgba[src];     // R
            dib[dst + 3] = 0;             // 40 头 alpha 无语义（legacy 规则按 255）
            src += 4;
            dst += 4;
        }
    }
    return dib;
}

/// <summary>DIBV5（BITMAPV5HEADER 124，BI_BITFIELDS + alpha 掩码，自下而上）——透明截图形状。</summary>
static byte[] BuildCfDibV5(byte[] rgba, int w, int h)
{
    var dib = new byte[124 + w * h * 4];
    WriteU32(dib, 0, 124);
    WriteU32(dib, 4, (uint)w);
    WriteI32(dib, 8, h);
    WriteU16(dib, 12, 1);
    WriteU16(dib, 14, 32);
    WriteU32(dib, 16, 3); // BI_BITFIELDS
    WriteU32(dib, 20, (uint)(w * h * 4));
    WriteU32(dib, 40, 0x00ff0000); // R
    WriteU32(dib, 44, 0x0000ff00); // G
    WriteU32(dib, 48, 0x000000ff); // B
    WriteU32(dib, 52, 0xff000000); // A
    for (var y = 0; y < h; y++)
    {
        var src = (h - 1 - y) * w * 4;
        var dst = 124 + y * w * 4;
        for (var x = 0; x < w; x++)
        {
            dib[dst] = rgba[src + 2];
            dib[dst + 1] = rgba[src + 1];
            dib[dst + 2] = rgba[src];
            dib[dst + 3] = rgba[src + 3];
            src += 4;
            dst += 4;
        }
    }
    return dib;
}

/// <summary>剪贴板 CF_DIBV5 回读解码（V5 头 + 自下而上 BGRA）→ 逐通道与源比对。</summary>
static (bool Ok, string Msg) CompareDibV5WithRgba(byte[] dib, int w, int h, byte[] expected)
{
    if (ReadU32(dib, 0) != 124)
    {
        return (false, "头尺寸非 V5");
    }
    var width = (int)ReadU32(dib, 4);
    var height = Math.Abs((int)ReadU32(dib, 8));
    if (width != w || height != h)
    {
        return (false, $"尺寸 {width}x{height} ≠ {w}x{h}");
    }
    var diff = 0;
    var cornerAlpha = -1;
    for (var y = 0; y < h; y++)
    {
        var src = 124 + (h - 1 - y) * w * 4;
        for (var x = 0; x < w; x++)
        {
            var d = (y * w + x) * 4;
            int b = dib[src], g = dib[src + 1], r = dib[src + 2], a = dib[src + 3];
            if (x == 0 && y == 0)
            {
                cornerAlpha = a;
            }
            if (r != expected[d] || g != expected[d + 1] || b != expected[d + 2] || a != expected[d + 3])
            {
                diff++;
            }
            src += 4;
        }
    }
    return diff == 0
        ? (true, $"角点 alpha={cornerAlpha}，{w * h} 像素零差异")
        : (false, $"{diff} 像素差异");
}

static (bool Ok, int DiffPixels) ComparePngWithRgba(string path, int w, int h, byte[] expected)
{
    var rgba = DecodePngToRgba(path);
    if (rgba is null || rgba.Length != w * h * 4)
    {
        return (false, -1);
    }
    var diff = 0;
    for (var i = 0; i < rgba.Length; i++)
    {
        if (rgba[i] != expected[i])
        {
            diff++;
        }
    }
    return (diff == 0, diff);
}

/// <summary>PNG → 规范化 RGBA（Bgra32 非预乘，与 DibDecoder.DecodePngPixels 同口径）。</summary>
static byte[]? DecodePngToRgba(string path)
{
    try
    {
        var decoder = BitmapDecoder.Create(
            new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault();
        if (frame is null)
        {
            return null;
        }
        BitmapSource source = frame.Format == System.Windows.Media.PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var bgra = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(bgra, source.PixelWidth * 4, 0);
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }
    catch (Exception)
    {
        return null;
    }
}

/// <summary>图卡矩形屏上截取 → 像素统计（渲染证据：色彩数与标准差）。</summary>
static (int DistinctColors, double StdDev, double MeanR, double MeanG, double MeanB)? CaptureCardStats(AutomationElement card)
{
    var r = card.Current.BoundingRectangle;
    if (r.Width <= 0)
    {
        return null;
    }
    var w = (int)r.Width;
    var h = Math.Max(1, (int)(r.Height * 0.45)); // 顶部 ≈ 缩略图区（150 DIP）
    using var bmp = new Bitmap(w, h);
    using var g = Graphics.FromImage(bmp);
    g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, new System.Drawing.Size(w, h));
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var bgra = new byte[w * h * 4];
    Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
    bmp.UnlockBits(data);
    var colors = new HashSet<uint>();
    double sum = 0, sumSq = 0, sr = 0, sg = 0, sb = 0;
    var n = w * h;
    for (var i = 0; i < bgra.Length; i += 4)
    {
        colors.Add((uint)(bgra[i] << 16 | bgra[i + 1] << 8 | bgra[i + 2]));
        var lum = 0.299 * bgra[i + 2] + 0.587 * bgra[i + 1] + 0.114 * bgra[i];
        sum += lum;
        sumSq += lum * lum;
        sr += bgra[i + 2];
        sg += bgra[i + 1];
        sb += bgra[i];
    }
    var mean = sum / n;
    return (colors.Count, Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)), sr / n, sg / n, sb / n);
}

static (long WorkingSet, long Private) MemSnapshot(Process p)
{
    p.Refresh();
    return (p.WorkingSet64, p.PrivateMemorySize64);
}

static double MiB(long bytes) => bytes / 1024.0 / 1024.0;

// ============================ 剪贴板写 ============================

static void SetClipboardText(string text)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            if (!Native.OpenClipboard(IntPtr.Zero))
            {
                throw new InvalidOperationException("OpenClipboard failed");
            }
            try
            {
                _ = Native.EmptyClipboard();
                var handle = Native.GlobalAlloc(0x0002, (UIntPtr)((text.Length + 1) * 2));
                var ptr = Native.GlobalLock(handle);
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
                _ = Native.GlobalUnlock(handle);
                _ = Native.SetClipboardData(CfUnicodeText, handle);
            }
            finally
            {
                _ = Native.CloseClipboard();
            }
            return;
        }
        catch (Exception) when (attempt < 10)
        {
            Thread.Sleep(50);
        }
    }
}

static void SetClipboardBytes(uint format, byte[] bytes)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            if (!Native.OpenClipboard(IntPtr.Zero))
            {
                throw new InvalidOperationException("OpenClipboard failed");
            }
            try
            {
                _ = Native.EmptyClipboard();
                var handle = Native.GlobalAlloc(0x0002, (UIntPtr)bytes.Length);
                var ptr = Native.GlobalLock(handle);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                _ = Native.GlobalUnlock(handle);
                _ = Native.SetClipboardData(format, handle);
            }
            finally
            {
                _ = Native.CloseClipboard();
            }
            return;
        }
        catch (Exception) when (attempt < 10)
        {
            Thread.Sleep(50);
        }
    }
}

static byte[]? ReadClipboardBytes(uint format)
{
    if (!Native.OpenClipboard(IntPtr.Zero))
    {
        return null;
    }
    try
    {
        var handle = Native.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        var size = Native.GlobalSize(handle);
        var ptr = Native.GlobalLock(handle);
        if (ptr == IntPtr.Zero || size == UIntPtr.Zero)
        {
            return null;
        }
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            _ = Native.GlobalUnlock(handle);
        }
    }
    finally
    {
        _ = Native.CloseClipboard();
    }
}

static void WriteU32(byte[] b, int off, uint v)
{
    b[off] = (byte)v;
    b[off + 1] = (byte)(v >> 8);
    b[off + 2] = (byte)(v >> 16);
    b[off + 3] = (byte)(v >> 24);
}

static void WriteI32(byte[] b, int off, int v) => WriteU32(b, off, unchecked((uint)v));

static void WriteU16(byte[] b, int off, ushort v)
{
    b[off] = (byte)v;
    b[off + 1] = (byte)(v >> 8);
}

static uint ReadU32(byte[] b, int off) =>
    b[off] | (uint)b[off + 1] << 8 | (uint)b[off + 2] << 16 | (uint)b[off + 3] << 24;

// ============================ 进程/数据 ============================

static string FindAppExe()
{
    var candidates = new[]
    {
        "ClipboardTool.Presentation.Wpf/bin/Release/net10.0-windows/ClipboardTool.exe",
        "ClipboardTool.Presentation.Wpf/bin/Debug/net10.0-windows/ClipboardTool.exe",
    };
    for (var dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
    {
        foreach (var candidate in candidates)
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full))
            {
                return Path.GetFullPath(full);
            }
        }
    }
    throw new FileNotFoundException("未找到被测应用 exe（先 dotnet build -c Release）");
}

static IntPtr WaitPanelWindow(Process app, TimeSpan timeout)
{
    var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        try
        {
            var root = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
            var handle = root?.Current.NativeWindowHandle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }
        catch (ElementNotAvailableException) { }
        Thread.Sleep(100);
    }
    return IntPtr.Zero;
}

static Process LaunchNotepad()
{
    var before = new HashSet<int>(Process.GetProcessesByName("Notepad").Select(p => p.Id));
    _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"))
    {
        UseShellExecute = false,
    });
    var deadline = Environment.TickCount64 + 12_000;
    while (Environment.TickCount64 < deadline)
    {
        var candidate = Process.GetProcessesByName("Notepad")
            .FirstOrDefault(p => !before.Contains(p.Id) && p.MainWindowHandle != IntPtr.Zero);
        if (candidate is not null)
        {
            return candidate;
        }
        Thread.Sleep(100);
    }
    throw new InvalidOperationException("notepad 窗口未出现");
}

static IntPtr WaitForNotepadWindow(Process notepad, TimeSpan timeout)
{
    var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        notepad.Refresh();
        if (notepad.MainWindowHandle != IntPtr.Zero)
        {
            return notepad.MainWindowHandle;
        }
        Thread.Sleep(100);
    }
    return IntPtr.Zero;
}

static void ForceForeground(IntPtr hwnd)
{
    _ = Native.SendAltKey();
    _ = Native.SetForegroundWindow(hwnd);
    var deadline = Environment.TickCount64 + 2000;
    while (Environment.TickCount64 < deadline)
    {
        if (Native.GetForegroundWindow() == hwnd)
        {
            return;
        }
        _ = Native.SendAltKey();
        _ = Native.SetForegroundWindow(hwnd);
        Thread.Sleep(120);
    }
}

static void BackupDataDir(string dataDir, string backupDir)
{
    if (!Directory.Exists(dataDir))
    {
        return;
    }
    Directory.CreateDirectory(backupDir);
    foreach (var file in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(dataDir, file);
        var target = Path.Combine(backupDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

static void WipeDataDir(string dataDir)
{
    if (!Directory.Exists(dataDir))
    {
        return;
    }
    foreach (var dir in Directory.GetDirectories(dataDir))
    {
        Directory.Delete(dir, recursive: true);
    }
    foreach (var file in Directory.GetFiles(dataDir))
    {
        File.Delete(file);
    }
}

static void RestoreDataDir(string backupDir, string dataDir)
{
    if (!Directory.Exists(backupDir))
    {
        return; // 备份前无真实数据：保持清空后的干净态
    }
    WipeDataDir(dataDir);
    Directory.CreateDirectory(dataDir);
    foreach (var file in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(backupDir, file);
        var target = Path.Combine(dataDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

// ============================ Win32 ============================

/// <summary>浏览态导航热键：呼出键恒 id=1，导航键按注册表顺序每轮占 8 个 id。</summary>
internal enum Nav { Up = 2, Down = 3, Enter = 4, Escape = 5, Delete = 6, Pin = 7, Note = 8, Search = 9 }

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

    private const ushort VkMenu = 0x12;
    private const uint KeyUp = 0x0002;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseWheel = 0x0800;

    internal static bool SendAltKey()
    {
        var keyDown = KeyInput(VkMenu, 0);
        var keyUp = KeyInput(VkMenu, KeyUp);
        var size = Marshal.SizeOf<INPUT>();
        _ = SendInput(1, [keyDown], size);
        _ = SendInput(1, [keyUp], size);
        return true;
    }

    private static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    // INPUT 联合体必须含 MOUSEINPUT（x64 布局），同 E2eT02/prototype/p2 的踩坑注记
    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public HARDWAREINPUT hi;
    }

    private static INPUT Mouse(uint flags, int x, int y, uint mouseData = 0) => new()
    {
        type = 0,
        mi = new MOUSEINPUT { dx = x, dy = y, mouseData = mouseData, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    private static void MoveCursor(int x, int y)
    {
        var size = Marshal.SizeOf<INPUT>();
        var normX = x * 65535 / GetSystemMetrics(0);
        var normY = y * 65535 / GetSystemMetrics(1);
        _ = SendInput(1, [Mouse(MouseMove | MouseAbsolute, normX, normY)], size);
        Thread.Sleep(40);
    }

    internal static void Wheel(int x, int y, int delta)
    {
        MoveCursor(x, y);
        var size = Marshal.SizeOf<INPUT>();
        _ = SendInput(1, [Mouse(MouseWheel, 0, 0, unchecked((uint)delta))], size);
    }

    internal static void DoubleClick(int x, int y)
    {
        var size = Marshal.SizeOf<INPUT>();
        var normX = x * 65535 / GetSystemMetrics(0);
        var normY = y * 65535 / GetSystemMetrics(1);

        void Send(uint flags) => _ = SendInput(1, [Mouse(flags, normX, normY)], size);

        Send(MouseMove | MouseAbsolute);
        Thread.Sleep(60);
        Send(MouseLeftDown | MouseAbsolute);
        Send(MouseLeftUp | MouseAbsolute);
        Thread.Sleep(60);
        Send(MouseLeftDown | MouseAbsolute);
        Send(MouseLeftUp | MouseAbsolute);
    }
}
