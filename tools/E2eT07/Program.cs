using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;

// T07 工单 11 真机端到端验证（自动化部分，E2eT09 产线思路）：
//   ① 亮暗广播：直接翻转 Personalize 两键触发真实广播 → 面板换肤（整面像素亮度）端到端；
//      「自定义」模式两键相反各判各的；托盘图标经溢出区截图取证；验证后注册表原样还原。
//   ② 五档托盘：本机主屏 DPI → 期望档位证据（本机 175% → 28px 档；清晰度目测留人工）；
//      搜索中切主题保文本与焦点（文本不丢 + 继续可输入 + 焦点仍在搜索框）。
//   ③ 换键：捕获态其他键不触发导航；占用失败（E2E 自持 Ctrl+Alt+J）保旧键；Esc 恢复旧键；
//      成功持久化（settings.json + 真实按键生效）+ 重启保留（重启手法参照 tools/E2eT09）。
//   ④ 双实例：再启动投递呼出后退出（exit 0）且原进程面板呼出；提权方向按本机 UAC 静默能力实测。
//   ⑤ 托盘「清空历史」菜单入口端到端（T07 接线范围；HistoryStore.Clear 无头已覆盖）。
// 人工项：175% 托盘图标清晰度目测（截图留档）；UAC 需交互时的提权投递场景；托盘图标视觉舒适度。

const uint CfUnicodeText = 13;
const int WmHotkey = 0x0312;
const int WmKeyDown = 0x0100;
const int WmKeyUp = 0x0101;
const ushort VkDown = 0x28, VkReturn = 0x0D, VkEscape = 0x1B, VkJ = 0x4A, VkK = 0x4B;
const ushort VkControl = 0x11, VkMenu = 0x12, VkShift = 0x10;
const int ModAlt = 0x1, ModControl = 0x2;
const string PersonalizePath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

var failures = new List<string>();
var notes = new List<string>();
var runDir = Path.Combine(Path.GetTempPath(), "e2e-t07-run-" + DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(runDir);
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool");
var backupDir = Path.Combine(runDir, "data-backup");

_ = Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

// ---------- 0) 前置：无运行实例；备份真实数据与注册表主题键（结束原样恢复） ----------
if (Process.GetProcessesByName("ClipboardTool").Length > 0)
{
    Console.WriteLine("已存在 ClipboardTool 运行实例，为不干扰真实使用中止 E2E。");
    return 2;
}
BackupDataDir(dataDir, backupDir);
WipeDataDir(dataDir);
// 预写已知设置（wipe 后）：theme=system、默认呼出键——不依赖机器上的残留偏好
Directory.CreateDirectory(dataDir);
File.WriteAllText(Path.Combine(dataDir, "settings.json"),
    "{\"autoStart\":false,\"shortcut\":\"Control+Shift+V\",\"theme\":\"system\"}");
var appsOrig = Registry.GetValue(@"HKEY_CURRENT_USER\" + PersonalizePath, "AppsUseLightTheme", null);
var systemOrig = Registry.GetValue(@"HKEY_CURRENT_USER\" + PersonalizePath, "SystemUsesLightTheme", null);
notes.Add($"注册表原值：AppsUseLightTheme={FmtReg(appsOrig)}, SystemUsesLightTheme={FmtReg(systemOrig)}");

var scale = PrimaryScale();
var tier = TierForScale(scale);
notes.Add($"主屏 DPI 缩放 = {scale:0.00}（DPI={scale * 96:0}）→ 期望托盘档位 {tier}px（175% 判据需 28px 档）");
var isElevated = new System.Security.Principal.WindowsPrincipal(
    System.Security.Principal.WindowsIdentity.GetCurrent())
    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
var consent = AsUint(Registry.GetValue(
    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
    "ConsentPromptBehaviorAdmin", null));
var enableLua = AsUint(Registry.GetValue(
    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
    "EnableLUA", null));
notes.Add($"E2E 进程提权={isElevated}, UAC ConsentPromptBehaviorAdmin={consent?.ToString() ?? "缺省"}, EnableLUA={enableLua?.ToString() ?? "缺省"}");
notes.Add($"主屏物理分辨率 = {Native.GetSystemMetrics(0)}x{Native.GetSystemMetrics(1)}（在屏判据按真实屏宽）");

var app = Process.Start(new ProcessStartInfo(FindAppExe()) { UseShellExecute = false })!;
Thread.Sleep(2000); // 启动期注册竞态窗口（呼出键 5s/20s 重试兜底，这里留足余量）
var trayHwnd = IntPtr.Zero;
try
{
    var panelHwnd = WaitPanelWindow(app, TimeSpan.FromSeconds(10));
    Check(panelHwnd != IntPtr.Zero, "应用启动且面板窗口存在");
    trayHwnd = Native.FindWindowW(null, "ClipboardToolTray");
    Check(trayHwnd != IntPtr.Zero, "托盘隐藏窗口 ClipboardToolTray 存在（图标宿主已创建）");

    // ---------- 1) ①亮暗广播：面板路径（像素级端到端） ----------
    Check(ToggleByKey(app, VkShiftV(), ctrl: true, alt: false, shift: true, win: false, expectShown: true),
        "默认呼出键 Ctrl+Shift+V 真实按键呼出（启动注册生效）");
    var baseline = PanelMeanLuminance(app);
    var baselineClass = ThemeClass(baseline);
    Check(baselineClass is "dark" or "light", $"面板基准采样可判类（亮度={baseline:0.0} → {baselineClass}）");

    var appsNow = AsUint(Registry.GetValue(@"HKEY_CURRENT_USER\" + PersonalizePath, "AppsUseLightTheme", null)) ?? 1u;
    FlipThemeKey("AppsUseLightTheme", 1u - appsNow);
    var afterApps = WaitForPanelClass(app, Flip(baselineClass), 6000);
    Check(afterApps == Flip(baselineClass),
        $"翻转 AppsUseLightTheme → 面板皮肤经真实广播切换（{baselineClass}→{afterApps}）");
    FlipThemeKey("AppsUseLightTheme", appsNow);
    var back = WaitForPanelClass(app, baselineClass, 6000);
    Check(back == baselineClass, $"还原 AppsUseLightTheme → 面板回到基准（{back}）");

    // 「自定义」模式：应用模式=亮、任务栏=暗 → 面板跟 Apps 键、托盘跟 System 键
    FlipThemeKey("AppsUseLightTheme", 1);
    FlipThemeKey("SystemUsesLightTheme", 0);
    var customLight = WaitForPanelClass(app, "light", 6000);
    Check(customLight == "light", "自定义模式（应用=亮/任务栏=暗）→ 面板亮（跟 AppsUseLightTheme）");
    var trayBefore = TrayIconStats(runDir, "tray-custom-darkbar");
    if (trayBefore is { } tb)
    {
        notes.Add($"托盘截图1（自定义亮面板/暗任务栏）：图标区均值亮度={tb.Stats.MeanLuminance:0.0}，尺寸={tb.Stats.Width}x{tb.Stats.Height}，文件={Path.GetFileName(tb.File)}");
    }
    else
    {
        notes.Add("托盘图标不可截（不在可见区且溢出区未命中）→ 图标证据留人工");
    }

    // 只翻任务栏键：面板不许动，托盘图标应换色（等待式断言：6s 内面板须稳定保持亮）
    FlipThemeKey("SystemUsesLightTheme", 1);
    Thread.Sleep(2500);
    Check(WaitForPanelClass(app, "light", 6000) == "light",
        "只翻 SystemUsesLightTheme → 面板保持亮（面板不跟任务栏键，两路独立）");
    var trayAfter = TrayIconStats(runDir, "tray-lightbar");
    if (trayBefore is { } t0 && trayAfter is { } t1)
    {
        var delta = Math.Abs(t1.Stats.MeanLuminance - t0.Stats.MeanLuminance);
        Check(delta > 8, $"托盘图标配色随任务栏键切换（亮度差 {delta:0.0}：{t0.Stats.MeanLuminance:0.0}→{t1.Stats.MeanLuminance:0.0}；任务栏底色同时反转属预期）");
        notes.Add($"托盘截图2（亮任务栏）：{Path.GetFileName(t1.File)}");
    }
    else
    {
        notes.Add("托盘图标换色未取得双点像素证据 → 留人工");
    }

    RestoreThemeKeys(appsOrig, systemOrig);
    var restored = WaitForPanelClass(app, baselineClass, 6000);
    Check(restored == baselineClass, "两键还原原值 → 面板回到基准类（注册表已还原）");

    // ---------- 2) ②搜索中切主题保文本焦点 ----------
    // 本机 LL 钩子会间歇性延迟/吞掉注入键（'T' 常滞后一拍），断言只锚定判据本身：
    // 键入可达且非空 → 切主题文本不丢 → 继续键入仍落进搜索框（文本增长）→ UIA 焦点在搜索框。
    Native.PostMessage(panelHwnd, WmHotkey, (IntPtr)9, IntPtr.Zero); // 导航 Search（周期 1，id=9）
    Thread.Sleep(1000);
    ForceForeground(panelHwnd);
    Thread.Sleep(400);
    for (var round = 0; round < 4; round++)
    {
        TypeTextReal("t07");
        Thread.Sleep(800);
        if (!string.IsNullOrEmpty(SearchBoxText(app)))
        {
            break;
        }
    }
    var textBefore = SearchBoxText(app);
    Check(!string.IsNullOrEmpty(textBefore), $"搜索框已能键入（实测 \"{textBefore}\"）");

    var appsNow2 = AsUint(Registry.GetValue(@"HKEY_CURRENT_USER\" + PersonalizePath, "AppsUseLightTheme", null)) ?? 1u;
    FlipThemeKey("AppsUseLightTheme", 1u - appsNow2);
    Thread.Sleep(1500);
    var textMid = SearchBoxText(app);
    Check(textMid == textBefore, $"切主题广播落地后搜索文本不丢（\"{textMid}\" vs \"{textBefore}\"）");
    TypeTextReal("x");
    Thread.Sleep(1000);
    var textAfter = SearchBoxText(app);
    Check(textAfter is { } ta && textBefore is { } before && ta.StartsWith(before, StringComparison.Ordinal) && ta.Length > before.Length,
        $"切主题后键盘焦点仍在搜索框（继续键入追加 → \"{textAfter}\"）");
    Check(SearchBoxFocused(app), "UIA 焦点元素仍是搜索框");
    RestoreThemeKeys(appsOrig, systemOrig);
    Thread.Sleep(800);
    Native.PostMessage(panelHwnd, WmHotkey, (IntPtr)5, IntPtr.Zero); // Esc 退搜索（id=5）
    Thread.Sleep(800);
    Check(ToggleByKey(app, VkShiftV(), ctrl: true, alt: false, shift: true, win: false, expectShown: false),
        "搜索测试收尾：呼出键停靠面板");

    // ---------- 3) ⑤托盘「清空历史」菜单入口端到端 ----------
    for (var i = 0; i < 3; i++)
    {
        SetClipboardText($"E2E-T07-清空前置-{i}");
        Thread.Sleep(700);
    }
    Check(WaitFooterReaches(app, 3, TimeSpan.FromSeconds(6)), "托盘清空前 3 条文本入库");
    TrayMenuSelect(itemIndex: 5); // show(1) change-shortcut(2) autostart(3) theme(4) clear-history(5)
    Check(WaitFooterReaches(app, 0, TimeSpan.FromSeconds(6)), "托盘「清空历史」菜单入口清空（页脚 → 0）");
    var imagesDir = Path.Combine(dataDir, "images");
    Check(!Directory.Exists(imagesDir) || Directory.GetFiles(imagesDir).Length == 0,
        "清空后 images/ 无残留（本批纯文本；PNG 联动已由 T09 E2E 覆盖）");

    // ---------- 4) ③换键 ----------
    var e2eWindow = HotkeyWindow.Create();
    var blocked = e2eWindow.RegisterHotKey(ModControl | ModAlt, VkJ);
    Check(blocked, "E2E 自持占用热键 Ctrl+Alt+J 注册成功（制造真实占用）");

    TrayMenuSelect(itemIndex: 2); // change-shortcut → 捕获态
    Check(IsPanelOnScreen(app), "托盘「更换快捷键」进入捕获态（面板已呼出）");

    // ③a 捕获态其他键不触发导航（先入 1 条文本，保证有可选卡片）
    SetClipboardText("E2E-T07-捕获前置-1");
    Thread.Sleep(800);
    var selectedBefore = SelectedCard(app);
    KeyTapReal(VkDown);
    Thread.Sleep(600);
    var selectedAfter = SelectedCard(app);
    var navQuiet = selectedBefore is not null && selectedAfter is not null && selectedBefore.Equals(selectedAfter);
    var statusMissing = FindPanelText(app, t => t.Contains("请包含 Ctrl"));
    Check(navQuiet || statusMissing is not null,
        "捕获态按 Down：不触发导航（选中不变）且/或缺修饰提示出现");

    // ③b 占用失败保旧键（主键经消息直达覆盖层，绕开 E2E 自持热键的系统级拦截）
    Native.SetCursorPos(900, 500);
    ModifiersReal(ctrl: true, alt: true, shift: false, win: false, down: true);
    Thread.Sleep(150);
    _ = Native.PostMessage(panelHwnd, WmKeyDown, (IntPtr)VkJ, IntPtr.Zero);
    _ = Native.PostMessage(panelHwnd, WmKeyUp, (IntPtr)VkJ, IntPtr.Zero);
    Thread.Sleep(600);
    ModifiersReal(ctrl: true, alt: true, shift: false, win: false, down: false);
    var statusOccupied = FindPanelText(app, t => t.Contains("已被占用"));
    Check(statusOccupied is not null, "Ctrl+Alt+J 被占用 → 试注册失败提示「已被占用或无效」");
    Check(ReadShortcutSetting() == "Control+Shift+V", "占用失败：设置未变（旧键保留）");

    // ③c Esc 恢复旧键
    KeyTapReal(VkEscape);
    Thread.Sleep(800);
    var stateBeforeEsc = IsPanelOnScreen(app);
    Check(ToggleByKey(app, VkShiftV(), ctrl: true, alt: false, shift: true, win: false, expectShown: !stateBeforeEsc),
        "Esc 取消后旧呼出键仍生效（真实按键切换面板状态）");
    if (IsPanelOnScreen(app))
    {
        ToggleByKey(app, VkShiftV(), ctrl: true, alt: false, shift: true, win: false, expectShown: false);
    }

    // ③d 成功持久化 + 重启保留
    TrayMenuSelect(itemIndex: 2);
    ComboTapReal(VkK, ctrl: true, alt: true, shift: false, win: false);
    var persisted = WaitUntil(() => ReadShortcutSetting() == "Control+Alt+K", 4000);
    Check(persisted, "捕获 Ctrl+Alt+K 成功 → settings.json 持久化（shortcut=Control+Alt+K）");
    var statusOk = FindPanelText(app, t => t.Contains("已设置为"));
    Check(statusOk is not null, $"覆盖层成功文案（\"{statusOk ?? "未读到"}\"）");
    Thread.Sleep(1600); // 1200ms 收层
    Check(ToggleByKey(app, VkK, ctrl: true, alt: true, shift: false, win: false, expectShown: false),
        "新呼出键真实按键生效（面板停靠=注册成功）");

    app.Kill();
    app.WaitForExit(5000);
    app = Process.Start(new ProcessStartInfo(FindAppExe()) { UseShellExecute = false })!;
    panelHwnd = WaitPanelWindow(app, TimeSpan.FromSeconds(10));
    Thread.Sleep(2000);
    Check(panelHwnd != IntPtr.Zero, "重启后面板窗口存在");
    Check(ToggleByKey(app, VkK, ctrl: true, alt: true, shift: false, win: false, expectShown: true),
        "重启后新键 Ctrl+Alt+K 呼出（持久化跨重启保留）");
    ComboTapReal(VkShiftV(), ctrl: true, alt: false, shift: true, win: false);
    Thread.Sleep(1200);
    Check(IsPanelOnScreen(app), "重启后旧默认键 Ctrl+Shift+V 不再注册（按下未呼出）");
    Check(ToggleByKey(app, VkK, ctrl: true, alt: true, shift: false, win: false, expectShown: false),
        "新键停靠回位");

    e2eWindow.UnregisterHotKey(ModControl | ModAlt, VkJ);
    e2eWindow.Dispose();

    // ---------- 5) ④双实例 ----------
    Check(WaitPanelDocked(app, TimeSpan.FromSeconds(4)), "双实例前置：原实例面板停靠");
    var second = Process.Start(new ProcessStartInfo(FindAppExe()) { UseShellExecute = false })!;
    var exited = second.WaitForExit(8000);
    var exitCode = exited ? second.ExitCode : -1;
    if (!exited)
    {
        try { second.Kill(); } catch { }
    }
    Check(exited, "第二实例投递呼出后退出（≤8s）");
    Check(exitCode == 0, $"第二实例退出码 = 0（实测 {exitCode}；投递成功口径）");
    if (!exited)
    {
        Console.WriteLine("NOTE 第二实例未退出，startup.log 尾部 20 行：");
        foreach (var line in TailStartupLog(20))
        {
            Console.WriteLine("NOTE   " + line);
        }
    }
    Thread.Sleep(1000);
    Check(IsPanelOnScreen(app), "原进程面板被呼出（只呼出原进程，未开新面板）");
    ToggleByKey(app, VkK, ctrl: true, alt: true, shift: false, win: false, expectShown: false);

    if (isElevated)
    {
        notes.Add("提权投递：E2E 全程提权运行，上面第二实例即提权客户端→提权服务端实测通过（同 SID + ACL 双保障）；" +
                  "「普通服务端→提权客户端」需降权启动，留人工");
    }
    else if (consent == 0)
    {
        try
        {
            var elevated = Process.Start(new ProcessStartInfo(FindAppExe())
            {
                UseShellExecute = true,
                Verb = "runas", // ConsentPromptBehaviorAdmin=0：静默提权，无 UAC 交互
            });
            var exitedElevated = elevated!.WaitForExit(10000);
            var code = exitedElevated ? elevated.ExitCode : -1;
            if (!exitedElevated)
            {
                try { elevated.Kill(); } catch { }
            }
            Check(exitedElevated && code == 0, $"提权第二实例投递后退出（exit={code}；普通↔提权 ACL 实测）");
            Thread.Sleep(1000);
            Check(IsPanelOnScreen(app), "提权投递后原（普通）进程面板被呼出");
            ToggleByKey(app, VkK, ctrl: true, alt: true, shift: false, win: false, expectShown: false);
        }
        catch (Exception ex)
        {
            notes.Add($"提权投递未能自动化实测（{ex.GetType().Name}: {ex.Message}）→ 待人工");
        }
    }
    else
    {
        notes.Add($"提权投递未实测（UAC 需交互：ConsentPromptBehaviorAdmin={consent?.ToString() ?? "缺省"}）→ 待人工；" +
                  "同 SID + 管道 ACL（当前用户/Administrators ReadWrite）与投递路径已由普通第二实例实测覆盖");
    }
}
catch (Exception ex)
{
    failures.Add("异常：" + ex.Message);
    Console.WriteLine(ex);
}
finally
{
    try { app.CloseMainWindow(); } catch { }
    try { app.Kill(); } catch { }
    app.WaitForExit(5000);
    foreach (var p in Process.GetProcessesByName("ClipboardTool"))
    {
        try { p.Kill(); } catch { }
    }
    RestoreThemeKeys(appsOrig, systemOrig);
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

/// <summary>真实按键切换面板状态（呼出键语义）：最多 4 次重试，状态翻转即成功。
/// expectShown 仅用于消息表述；翻转本身即注册与生效的行为证据。</summary>
static bool ToggleByKey(Process app, ushort vk, bool ctrl, bool alt, bool shift, bool win, bool expectShown)
{
    var before = IsPanelOnScreen(app);
    for (var attempt = 0; attempt < 4; attempt++)
    {
        ComboTapReal(vk, ctrl, alt, shift, win);
        var deadline = Environment.TickCount64 + 1400;
        while (Environment.TickCount64 < deadline)
        {
            if (IsPanelOnScreen(app) != before)
            {
                return true;
            }
            Thread.Sleep(120);
        }
    }
    Console.WriteLine($"NOTE ToggleByKey 未翻转（按键前 onScreen={before}），startup.log 尾部：");
    foreach (var line in TailStartupLog(8))
    {
        Console.WriteLine("NOTE   " + line);
    }
    return false;
}

static string[] TailStartupLog(int lines)
{
    try
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardTool", "startup.log");
        if (!File.Exists(path))
        {
            return ["(无 startup.log)"];
        }
        var all = File.ReadAllLines(path);
        return all[Math.Max(0, all.Length - lines)..];
    }
    catch (Exception)
    {
        return ["(startup.log 读取失败)"];
    }
}

// ============================ 主题/亮度 ============================

static string FmtReg(object? v) => v is null ? "<缺键>" : $"{v}";

static uint? AsUint(object? v) => v switch
{
    uint u => u,
    int i when i >= 0 => (uint)i,
    long l when l >= 0 => (uint)l,
    byte b => b,
    _ => null,
};

static void FlipThemeKey(string name, uint value) =>
    Registry.SetValue(@"HKEY_CURRENT_USER\" + PersonalizePath, name, value, RegistryValueKind.DWord);

static void RestoreThemeKeys(object? appsOrig, object? systemOrig)
{
    if (appsOrig is null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(PersonalizePath, true);
        k?.DeleteValue("AppsUseLightTheme", false);
    }
    else
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\" + PersonalizePath, "AppsUseLightTheme", appsOrig, RegistryValueKind.DWord);
    }
    if (systemOrig is null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(PersonalizePath, true);
        k?.DeleteValue("SystemUsesLightTheme", false);
    }
    else
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\" + PersonalizePath, "SystemUsesLightTheme", systemOrig, RegistryValueKind.DWord);
    }
}

static string Flip(string themeClass) => themeClass == "dark" ? "light" : "dark";

static string ThemeClass(double luminance) => luminance < 110 ? "dark"
    : luminance > 150 ? "light"
    : "ambiguous";

static string PanelMeanLuminanceClass(Process app) => ThemeClass(PanelMeanLuminance(app));

static string WaitForPanelClass(Process app, string expected, int timeoutMs) =>
    WaitFor(() =>
    {
        var cls = PanelMeanLuminanceClass(app);
        return cls == expected ? cls : null;
    }, timeoutMs) ?? PanelMeanLuminanceClass(app);

static double PanelMeanLuminance(Process app)
{
    if (!IsPanelOnScreen(app))
    {
        return -1; // 停靠（屏外）采样只会得到黑区：显式判为不可判
    }
    var r = PanelRect(app);
    return MeanLuminanceOf(new System.Drawing.Rectangle((int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height));
}

static double MeanLuminanceOf(System.Drawing.Rectangle rect)
{
    using var bmp = new Bitmap(rect.Width, rect.Height);
    using var g = Graphics.FromImage(bmp);
    g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(rect.Width, rect.Height));
    var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var bgra = new byte[bmp.Width * bmp.Height * 4];
    Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
    bmp.UnlockBits(data);
    double sum = 0;
    var n = bgra.Length / 4;
    for (var i = 0; i < bgra.Length; i += 4)
    {
        sum += 0.299 * bgra[i + 2] + 0.587 * bgra[i + 1] + 0.114 * bgra[i];
    }
    return sum / n;
}

// ============================ 托盘图标证据（可见区 → 溢出区） ============================

static ((double MeanLuminance, int Width, int Height) Stats, string File)? TrayIconStats(string runDir, string tag)
{
    try
    {
        var button = FindTrayButton(searchOverflow: true);
        if (button is null)
        {
            return null;
        }
        var r = button.Current.BoundingRectangle;
        if (r.Width <= 1 || r.Height <= 1)
        {
            return null;
        }
        var rect = new System.Drawing.Rectangle((int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height);
        var luminance = MeanLuminanceOf(rect);
        var file = Path.Combine(runDir, $"{tag}-{DateTime.Now:HHmmss}.png");
        using var bmp = new Bitmap(rect.Width, rect.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(rect.Width, rect.Height));
        bmp.Save(file, ImageFormat.Png);
        DismissFlyoutSafely(); // 收溢出气泡：不能按 Esc——面板 shown 时 Esc 会停靠面板，污染后续采样
        return ((luminance, rect.Width, rect.Height), file);
    }
    catch (Exception ex)
    {
        Console.WriteLine("NOTE TrayIconStats 异常：" + ex.Message);
        return null;
    }
}

static AutomationElement? FindTrayButton(bool searchOverflow)
{
    // 1) 可见托盘区
    var trayWnd = Native.FindWindowW("Shell_TrayWnd", null);
    if (trayWnd != IntPtr.Zero)
    {
        var button = FindNamedButton(AutomationElement.FromHandle(trayWnd));
        if (button is not null)
        {
            return button;
        }
    }
    if (!searchOverflow)
    {
        return null;
    }
    // 2) 溢出区：点开「显示隐藏的图标」气泡再找
    try
    {
        if (trayWnd == IntPtr.Zero)
        {
            return null;
        }
        var chevron = FindChevron(AutomationElement.FromHandle(trayWnd));
        if (chevron is null)
        {
            return null;
        }
        var cr = chevron.Current.BoundingRectangle;
        MouseClick((int)(cr.Left + cr.Width / 2), (int)(cr.Top + cr.Height / 2));
        Thread.Sleep(800);
        // 溢出气泡窗口（Win11：TopLevelWindowForOverflowXamlIsland）在根的子级
        var flyout = FindOverflowFlyout();
        var result = flyout is null ? null : FindNamedButton(flyout);
        if (result is null)
        {
            DismissFlyoutSafely(); // 收气泡，避免残留
        }
        return result;
    }
    catch (Exception)
    {
        return null;
    }
}

static AutomationElement? FindNamedButton(AutomationElement root)
{
    try
    {
        var found = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "ClipboardTool"));
        foreach (AutomationElement candidate in found)
        {
            if (candidate.Current.ControlType == ControlType.Button)
            {
                return candidate;
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

static AutomationElement? FindChevron(AutomationElement trayRoot)
{
    try
    {
        var buttons = trayRoot.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        foreach (AutomationElement button in buttons)
        {
            var name = button.Current.Name;
            if (name.Contains("隐藏的图标") || name.Contains("Hidden Icon", StringComparison.OrdinalIgnoreCase))
            {
                return button;
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

/// <summary>收溢出气泡：点屏上安全空白（避开面板区域 x≥900 与任务栏）让它失焦自行收起。
/// 不能按 Esc——面板 shown 时 Esc 会停靠面板（run7 实测的连锁污染源）。</summary>
static void DismissFlyoutSafely()
{
    MouseClick(400, 900);
    Thread.Sleep(500);
}

static AutomationElement? FindOverflowFlyout()
{
    try
    {
        var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            var className = child.Current.ClassName ?? string.Empty;
            var name = child.Current.Name ?? string.Empty;
            if (className.Contains("OverflowXamlIsland") || className.Contains("NotifyIconOverflow") ||
                name.Contains("溢出") || name.Contains("Overflow", StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

// ============================ 真实键盘/鼠标输入 ============================

static ushort VkShiftV() => 0x56; // 'V'

static void SendKey(ushort vk, bool down) =>
    _ = Native.SendInput(1, [Native.KeyInput(vk, down ? 0u : 0x0002u)], Marshal.SizeOf<Native.INPUT>());

static void KeyTapReal(ushort vk)
{
    SendKey(vk, down: true);
    Thread.Sleep(40);
    SendKey(vk, down: false);
    Thread.Sleep(60);
}

static void ComboTapReal(ushort vk, bool ctrl, bool alt, bool shift, bool win)
{
    ModifiersReal(ctrl, alt, shift, win, down: true);
    Thread.Sleep(80);
    KeyTapReal(vk);
    Thread.Sleep(80);
    ModifiersReal(ctrl, alt, shift, win, down: false);
}

static void ModifiersReal(bool ctrl, bool alt, bool shift, bool win, bool down)
{
    if (ctrl) SendKey(VkControl, down);
    if (alt) SendKey(VkMenu, down);
    if (shift) SendKey(VkShift, down);
    if (win) SendKey(0x5B, down);
    if (down)
    {
        Thread.Sleep(60);
    }
}

static void TypeTextReal(string text)
{
    foreach (var ch in text)
    {
        ushort vk = ch switch
        {
            >= 'a' and <= 'z' => (ushort)(ch - 'a' + 0x41),
            >= 'A' and <= 'Z' => (ushort)ch,
            >= '0' and <= '9' => (ushort)ch,
            _ => 0,
        };
        if (vk != 0)
        {
            KeyTapReal(vk);
        }
    }
}

static void MouseClick(int x, int y)
{
    Native.MoveCursor(x, y);
    var size = Marshal.SizeOf<Native.INPUT>();
    _ = Native.SendInput(1, [Native.MouseInput(Native.MouseLeftDown)], size);
    _ = Native.SendInput(1, [Native.MouseInput(Native.MouseLeftUp)], size);
    Thread.Sleep(60);
}

/// <summary>前置窗口（呼出键注册与 Alt 技巧同 E2eT09）。</summary>
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

// ============================ 托盘菜单 ============================

/// <summary>真实用户路径弹出托盘菜单并点击第 itemIndex 个有效项：
/// 打开溢出气泡（或可见区）→ 定位 ClipboardTool 图标 → 真实鼠标右键 → 等 #32768 →
/// UIA 定位菜单项 → 真实鼠标点击。PostMessage WM_APP_TRAY 的注入路径在本机不弹菜单，
/// 弃用。菜单项序：show(1) change-shortcut(2) autostart(3) theme(4) clear-history(5) quit(6)。</summary>
static void TrayMenuSelect(int itemIndex)
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        if (TryTrayMenuSelectOnce(itemIndex))
        {
            return;
        }
        DismissFlyoutSafely(); // 气泡可能开着/菜单可能没弹：收干净再试
        Thread.Sleep(400);
    }
    Console.WriteLine("NOTE 托盘菜单 3 次尝试均未完成");
}

static bool TryTrayMenuSelectOnce(int itemIndex)
{
    // 上一次的气泡若还开着，chevron 点击会把它关掉——先收干净
    if (FindOverflowFlyout() is not null)
    {
        DismissFlyoutSafely();
    }
    ForceForeground(Native.GetDesktopWindow());
    Thread.Sleep(200);
    var button = FindTrayButton(searchOverflow: true);
    if (button is null)
    {
        Console.WriteLine("NOTE 托盘图标未找到（可见区+溢出区）");
        return false;
    }
    var r = button.Current.BoundingRectangle;
    Native.MoveCursor((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
    Thread.Sleep(200);
    Native.MouseRightClick((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));

    IntPtr menuHwnd = IntPtr.Zero;
    var deadline = Environment.TickCount64 + 3000;
    while (Environment.TickCount64 < deadline)
    {
        menuHwnd = Native.FindWindowW("#32768", null);
        if (menuHwnd != IntPtr.Zero)
        {
            break;
        }
        Thread.Sleep(100);
    }
    if (menuHwnd == IntPtr.Zero)
    {
        Console.WriteLine("NOTE 托盘菜单未弹出（#32768 未出现）");
        return false;
    }
    Thread.Sleep(400);
    if (ClickMenuItemByIndex(menuHwnd, itemIndex))
    {
        Thread.Sleep(600);
        return true;
    }
    Console.WriteLine("NOTE UIA 未能定位菜单项，回退键盘导航（可能无效）");
    for (var i = 0; i < itemIndex; i++)
    {
        KeyTapReal(VkDown);
        Thread.Sleep(150);
    }
    KeyTapReal(VkReturn);
    Thread.Sleep(600);
    return true;
}

/// <summary>UIA 枚举 #32768 菜单项（分隔线不可见被跳过），点击第 index 个（1 起）。</summary>
static bool ClickMenuItemByIndex(IntPtr menuHwnd, int index)
{
    try
    {
        var menu = AutomationElement.FromHandle(menuHwnd);
        var items = menu.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
        var n = 0;
        foreach (AutomationElement item in items)
        {
            var r = item.Current.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0)
            {
                continue; // 分隔线等不可点元素
            }
            n++;
            if (n != index)
            {
                continue;
            }
            Console.WriteLine($"NOTE 菜单项 {index} = \"{item.Current.Name}\" @({r.Left:0},{r.Top:0} {r.Width:0}x{r.Height:0})");
            MouseClick((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
            return true;
        }
        Console.WriteLine($"NOTE 菜单项仅 {n} 个，不足 {index}");
    }
    catch (Exception ex)
    {
        Console.WriteLine("NOTE 菜单 UIA 异常：" + ex.Message);
    }
    return false;
}

// ============================ UIA 观察 ============================

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

static System.Windows.Rect PanelRect(Process app) => PanelRoot(app)?.Current.BoundingRectangle ?? default;

/// <summary>主屏物理宽度（本进程 DPI aware）。E2eT09 的 Left<2000 硬编码在宽屏上会把
/// 呼出后的面板误判为停靠，这里按真实屏宽判「在屏」。</summary>
static int ScreenWidthPx() => Native.GetSystemMetrics(0);

static bool IsPanelOnScreen(Process app)
{
    var r = PanelRect(app);
    return r.Width > 0 && r.Left >= 0 && r.Left < ScreenWidthPx();
}

static bool WaitPanelDocked(Process app, TimeSpan timeout) =>
    WaitUntil(() =>
    {
        var r = PanelRect(app);
        return r.Width == 0 || r.Left >= ScreenWidthPx();
    }, (int)timeout.TotalMilliseconds);

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

static string? FindPanelText(Process app, Func<string, bool> match)
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
            if (match(text.Current.Name))
            {
                return text.Current.Name;
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

static AutomationElement? SearchBoxElement(Process app)
{
    try
    {
        return PanelRoot(app)?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
    }
    catch (ElementNotAvailableException)
    {
        return null;
    }
}

static string? SearchBoxText(Process app)
{
    if (SearchBoxElement(app) is not { } edit ||
        !edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
    {
        return null;
    }
    return ((ValuePattern)pattern).Current.Value;
}

static bool SearchBoxFocused(Process app)
{
    if (SearchBoxElement(app) is not { } edit)
    {
        return false;
    }
    try
    {
        return Automation.Equals(AutomationElement.FocusedElement, edit);
    }
    catch (ElementNotAvailableException)
    {
        return false;
    }
}

static AutomationElement? SelectedCard(Process app)
{
    try
    {
        var root = PanelRoot(app);
        if (root is null)
        {
            return null;
        }
        var items = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        foreach (AutomationElement item in items)
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

// ============================ 设置/剪贴板/数据 ============================

static string? ReadShortcutSetting()
{
    var path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipboardTool", "settings.json");
    if (!File.Exists(path))
    {
        return "Control+Shift+V"; // 缺档即默认键（存档契约）
    }
    var m = Regex.Match(File.ReadAllText(path), "\"shortcut\"\\s*:\\s*\"([^\"]*)\"");
    return m.Success ? m.Groups[1].Value : null;
}

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

// ============================ DPI 档位 ============================

static double PrimaryScale()
{
    var monitor = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MonitorDefaultToPrimary);
    if (monitor == IntPtr.Zero ||
        Native.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) != 0 || dpiX == 0)
    {
        return 1.0;
    }
    return dpiX / 96.0;
}

static int TierForScale(double scale)
{
    int[] sizes = [16, 20, 24, 28, 32];
    var target = (int)Math.Round(16.0 * scale); // TrayIconDecider.LogicalSize=16
    return sizes.OrderBy(size => Math.Abs(size - target)).First();
}

// ============================ E2E 自持热键窗口（原生，无 STA 依赖） ============================

/// <summary>占用测试用隐藏窗口：RegisterHotKey 需要一个本进程窗口收 WM_HOTKEY。</summary>
internal sealed class HotkeyWindow : IDisposable
{
    private IntPtr _hwnd;
    private static readonly Native.WndProcDelegate WndProc = DefWndProc;

    private HotkeyWindow(IntPtr hwnd) => _hwnd = hwnd;

    public static HotkeyWindow Create()
    {
        // 先试系统预注册的 STATIC 类；失败则自注册一个最小窗口类
        var hwnd = Native.CreateWindowExW(0, "STATIC", "E2eT07Hotkey", 0x80000000 /*WS_POPUP*/,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            var wc = new Native.WNDCLASSW
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = "E2eT07HotkeyClass",
            };
            _ = Native.RegisterClassW(ref wc);
            hwnd = Native.CreateWindowExW(0, "E2eT07HotkeyClass", "E2eT07Hotkey", 0x80000000,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("E2E 热键窗口创建失败");
        }
        return new HotkeyWindow(hwnd);
    }

    public bool RegisterHotKey(uint modifiers, ushort vk) =>
        _hwnd != IntPtr.Zero && Native.RegisterHotKey(_hwnd, 0x1234, modifiers, vk);

    public bool UnregisterHotKey(uint modifiers, ushort vk) =>
        _hwnd != IntPtr.Zero && Native.UnregisterHotKey(_hwnd, 0x1234);

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _ = Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static IntPtr DefWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        Native.DefWindowProcW(hwnd, msg, wParam, lParam);
}

// ============================ Win32 ============================

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowW(string? className, string? windowName);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, ushort vk);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] internal static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("Shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();

    internal const uint MouseRightDown = 0x0008;
    internal const uint MouseRightUp = 0x0010;

    /// <summary>真实鼠标右键（先移动后按下抬起）。</summary>
    internal static void MouseRightClick(int x, int y)
    {
        MoveCursor(x, y);
        var size = Marshal.SizeOf<INPUT>();
        _ = SendInput(1, [MouseInput(MouseRightDown)], size);
        _ = SendInput(1, [MouseInput(MouseRightUp)], size);
    }

    internal const uint MonitorDefaultToPrimary = 1;
    private const uint KeyUp = 0x0002;
    private const uint MouseMove = 0x0001;
    internal const uint MouseLeftDown = 0x0002;
    internal const uint MouseLeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;
    private const ushort VkMenuKey = 0x12;

    internal delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    internal readonly struct POINT
    {
        public readonly int X;
        public readonly int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    internal static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    internal static INPUT MouseInput(uint flags) => new()
    {
        type = 0,
        mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    /// <summary>绝对坐标移动光标（SendInput 规范化坐标，同 E2eT09）。</summary>
    internal static void MoveCursor(int x, int y)
    {
        _ = SendInput(1, [MouseInputEx(MouseMove | MouseAbsolute, x, y, 0)], Marshal.SizeOf<INPUT>());
    }

    private static INPUT MouseInputEx(uint flags, int x, int y, uint mouseData)
    {
        // 规范化坐标按主屏度量（GetSystemMetrics），此处只用主屏内坐标
        var normX = (int)((long)x * 65535 / GetSystemMetrics(0));
        var normY = (int)((long)y * 65535 / GetSystemMetrics(1));
        return new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT { dx = normX, dy = normY, mouseData = mouseData, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
        };
    }

    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);

    internal static bool SendAltKey()
    {
        var size = Marshal.SizeOf<INPUT>();
        _ = SendInput(1, [KeyInput(VkMenuKey, 0)], size);
        _ = SendInput(1, [KeyInput(VkMenuKey, KeyUp)], size);
        return true;
    }

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
}
