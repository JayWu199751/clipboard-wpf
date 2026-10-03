using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// 16 号工单（F17 单击外部停靠与点击时间窗）真机端到端验证（E2eT07 手法：真实 SendInput +
// UIA 面板可见性回读）：
//   判据A 呼出后点击面板外 → 面板停靠（晚于呼出的外部点击正常收起，正向对照）；
//   判据B 呼出瞬间连点外部 → 不误收（时间窗防护：点击先注入、呼出键紧随其后零间隔，
//         若「Show 先于点击处理」时序命中且无时间窗防护，面板刚显形就会被收起；
//         时序命中为概率性，跑 8 轮增强覆盖；确定性语义证据在 Domain.ExternalClickRules 单测）；
//   判据C 搜索态点击面板外 → 停靠（输入态不豁免，legacy hide 统一路径）。
// 前置：无运行实例；settings.json 备份/还原（呼出键口径固定）。

const int WmHotkey = 0x0312;
const ushort VkControl = 0x11, VkMenu = 0x12, VkShift = 0x10, VkShiftV = 0x56;
// 点击点：屏内左侧 (150, 300)。呼出面板在工作区居中（宽≈高/2），左缘远大于 150，必在面板外；
// 不碰任务栏（底部）与托盘（右下）。
const int ClickX = 150, ClickY = 300;

var failures = new List<string>();
var notes = new List<string>();

// 提权运行时新窗口的 stdout 无人看到：全程输出同步落文件（相对仓库根，WorkingDirectory 指定）
var logPath = Path.Combine(Directory.GetCurrentDirectory(), "tools", "E2eT17", "run-output.txt");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
Console.SetOut(logWriter);
Console.WriteLine($"E2eT17 运行于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool");
var settingsPath = Path.Combine(dataDir, "settings.json");
var settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;

_ = Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

if (Process.GetProcessesByName("ClipboardTool").Length > 0)
{
    Console.WriteLine("已存在 ClipboardTool 运行实例，为不干扰真实使用中止 E2E。");
    return 2;
}

// 预写已知设置：默认呼出键，不依赖机器残留偏好
Directory.CreateDirectory(dataDir);
File.WriteAllText(settingsPath,
    "{\"autoStart\":false,\"shortcut\":\"Control+Shift+V\",\"theme\":\"system\"}");

var app = Process.Start(new ProcessStartInfo(FindAppExe()) { UseShellExecute = false })!;
Thread.Sleep(2000);
try
{
    var panelHwnd = WaitPanelWindow(app, TimeSpan.FromSeconds(10));
    Check(panelHwnd != IntPtr.Zero, "应用启动且面板窗口存在");
    notes.Add($"主屏物理分辨率 = {Native.GetSystemMetrics(0)}x{Native.GetSystemMetrics(1)}，点击点 ({ClickX},{ClickY})");

    // ---------- 判据A：呼出后点击面板外 → 停靠 ----------
    Check(ToggleSummon(app, expectShown: true), "呼出键呼出面板（前置）");
    Thread.Sleep(400);
    Check(IsPanelOnScreen(app), "面板已在屏（前置确认）");
    Native.MouseClick(ClickX, ClickY);
    Check(WaitPanelDocked(app, 3000), "判据A：点击面板外 → 面板停靠");

    // ---------- 判据B：呼出瞬间连点外部不误收（时间窗） ----------
    // 注入序：Ctrl/Shift 按下 → 鼠标点击 down/up（钩子记录 T_click）→ V down（零间隔，
    // WM_HOTKEY 立即投递）→ 修饰抬起。「Show 先于点击处理」命中的轮次里：
    // 无时间窗防护的面板会被这次点击收起；有防护则保持显示。
    var timeWindowRounds = 8;
    var keptRounds = 0;
    for (var round = 0; round < timeWindowRounds; round++)
    {
        Native.SendKey(VkControl, down: true);
        Native.SendKey(VkShift, down: true);
        Native.MoveCursor(ClickX, ClickY);
        var size = Marshal.SizeOf<Native.INPUT>();
        _ = Native.SendInput(1, [Native.MouseInput(Native.MouseLeftDown)], size);
        _ = Native.SendInput(1, [Native.MouseInput(Native.MouseLeftUp)], size);
        Native.SendKey(VkShiftV, down: true); // Ctrl+Shift 已按下 → 热键触发
        Native.SendKey(VkShiftV, down: false);
        Native.SendKey(VkShift, down: false);
        Native.SendKey(VkControl, down: false);

        // 面板应呼出并保持显示（时间窗防护生效）；若无防护且时序命中，这里会被点击收起
        var summoned = WaitUntil(() => IsPanelOnScreen(app), 2000);
        var stayed = summoned && WaitStaysOnScreen(app, 1500);
        if (summoned && stayed)
        {
            keptRounds++;
            Console.WriteLine($"PASS 判据B 第 {round + 1}/{timeWindowRounds} 轮：呼出瞬间连点外部，面板保持显示");
        }
        else
        {
            Console.WriteLine($"FAIL 判据B 第 {round + 1}/{timeWindowRounds} 轮：呼出后 2s 内面板消失" +
                $"（呼出={summoned}，保持={stayed}）——时间窗防护缺失或时序命中");
            failures.Add($"判据B 第 {round + 1} 轮：呼出瞬间连点外部面板被误收");
        }
        // 收尾进下一轮：呼出键停靠（面板在屏时）
        if (IsPanelOnScreen(app))
        {
            ToggleSummon(app, expectShown: false);
            Thread.Sleep(300);
        }
        else
        {
            ToggleSummon(app, expectShown: true); // 重建在屏态，下一轮从显示态起步走 Toggle
            Thread.Sleep(400);
            ToggleSummon(app, expectShown: false);
            Thread.Sleep(300);
        }
    }
    Check(keptRounds == timeWindowRounds, $"判据B 汇总：{keptRounds}/{timeWindowRounds} 轮呼出瞬间连点不误收");

    // ---------- 判据C：搜索态点击面板外 → 停靠（输入态不豁免） ----------
    Check(ToggleSummon(app, expectShown: true), "呼出键呼出面板（判据C 前置）");
    Thread.Sleep(400);
    _ = Native.PostMessage(panelHwnd, WmHotkey, (IntPtr)9, IntPtr.Zero); // 导航 Search（周期 1，id=9）
    Thread.Sleep(800);
    Check(IsPanelOnScreen(app), "搜索态已进入（面板保持显示）");
    Native.MouseClick(ClickX, ClickY);
    Check(WaitPanelDocked(app, 3000), "判据C：搜索态点击面板外 → 面板停靠");

    // ---------- 收尾 ----------
    if (IsPanelOnScreen(app))
    {
        ToggleSummon(app, expectShown: false);
    }
}
finally
{
    // 还原设置并确保无残留进程
    try
    {
        if (settingsBackup is not null)
        {
            File.WriteAllText(settingsPath, settingsBackup);
        }
        else if (File.Exists(settingsPath))
        {
            File.Delete(settingsPath);
        }
    }
    catch (Exception ex)
    {
        notes.Add("settings.json 还原失败：" + ex.Message);
    }
    foreach (var p in Process.GetProcessesByName("ClipboardTool"))
    {
        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            notes.Add("残留 ClipboardTool 进程已清理");
        }
        catch (Exception ex)
        {
            notes.Add("进程清理失败：" + ex.Message);
        }
    }
}

foreach (var note in notes)
{
    Console.WriteLine("NOTE " + note);
}
Console.WriteLine(failures.Count == 0
    ? "E2eT17 全部判据通过"
    : $"E2eT17 失败 {failures.Count} 项");
Console.WriteLine($"日志：{logPath}");
logWriter.Dispose();
return failures.Count == 0 ? 0 : 1;

// ============================ 判定与注入 ============================

void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok)
    {
        failures.Add(name);
    }
}

static bool WaitUntil(Func<bool> cond, int timeoutMs)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (cond())
        {
            return true;
        }
        Thread.Sleep(100);
    }
    return cond();
}

/// <summary>面板呼出后持续保持在屏（时间窗判据的「保持」侧：采样到任何不在屏时刻即失败）。</summary>
static bool WaitStaysOnScreen(Process app, int durationMs)
{
    var deadline = Environment.TickCount64 + durationMs;
    while (Environment.TickCount64 < deadline)
    {
        if (!IsPanelOnScreen(app))
        {
            return false;
        }
        Thread.Sleep(80);
    }
    return IsPanelOnScreen(app);
}

/// <summary>呼出键切换面板状态（最多 4 次重试，状态翻转即成功）。</summary>
static bool ToggleSummon(Process app, bool expectShown)
{
    var before = IsPanelOnScreen(app);
    for (var attempt = 0; attempt < 4; attempt++)
    {
        ComboTapReal(VkShiftV, ctrl: true, alt: false, shift: true, win: false);
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
    Console.WriteLine($"NOTE ToggleSummon 未翻转（按键前 onScreen={before}，期望 shown={expectShown}）");
    return false;
}

static void ComboTapReal(ushort vk, bool ctrl, bool alt, bool shift, bool win)
{
    ModifiersReal(ctrl, alt, shift, win, down: true);
    Thread.Sleep(80);
    Native.SendKey(vk, down: true);
    Thread.Sleep(40);
    Native.SendKey(vk, down: false);
    Thread.Sleep(60);
    ModifiersReal(ctrl, alt, shift, win, down: false);
}

static void ModifiersReal(bool ctrl, bool alt, bool shift, bool win, bool down)
{
    if (ctrl) Native.SendKey(VkControl, down);
    if (alt) Native.SendKey(VkMenu, down);
    if (shift) Native.SendKey(VkShift, down);
    if (win) Native.SendKey(0x5B, down);
    if (down)
    {
        Thread.Sleep(60);
    }
}

// ============================ UIA 观察 ============================

static IntPtr WaitPanelWindow(Process app, TimeSpan timeout)
{
    var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        try
        {
            var root = PanelRoot(app);
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

static System.Windows.Rect PanelRect(Process app) =>
    PanelRoot(app)?.Current.BoundingRectangle ?? default;

/// <summary>主屏物理宽度（本进程 DPI aware）；判「在屏」= 面板矩形有效且左缘落在屏内。</summary>
static int ScreenWidthPx() => Native.GetSystemMetrics(0);

static bool IsPanelOnScreen(Process app)
{
    var r = PanelRect(app);
    return r.Width > 0 && r.Left >= 0 && r.Left < ScreenWidthPx();
}

static bool WaitPanelDocked(Process app, int timeoutMs) =>
    WaitUntil(() =>
    {
        var r = PanelRect(app);
        return r.Width == 0 || r.Left >= ScreenWidthPx();
    }, timeoutMs);

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

// ============================ Win32 ============================

internal static class Native
{
    internal const uint MouseLeftDown = 0x0002;
    internal const uint MouseLeftUp = 0x0004;
    internal const uint MouseMove = 0x0001;
    internal const uint MouseAbsolute = 0x8000;
    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] internal static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);

    internal static void SendKey(ushort vk, bool down) =>
        _ = SendInput(1, [KeyInput(vk, down ? 0 : KeyEventKeyUp)], Marshal.SizeOf<INPUT>());

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

    /// <summary>真实左键点击（绝对坐标移动 + 按下抬起）。</summary>
    internal static void MouseClick(int x, int y)
    {
        MoveCursor(x, y);
        var size = Marshal.SizeOf<INPUT>();
        _ = SendInput(1, [MouseInput(MouseLeftDown)], size);
        _ = SendInput(1, [MouseInput(MouseLeftUp)], size);
        Thread.Sleep(60);
    }

    /// <summary>绝对坐标移动光标（SendInput 规范化坐标按主屏度量，同 E2eT07）。</summary>
    internal static void MoveCursor(int x, int y)
    {
        var normX = (int)((long)x * 65535 / GetSystemMetrics(0));
        var normY = (int)((long)y * 65535 / GetSystemMetrics(1));
        _ = SendInput(1, [MouseInputEx(MouseMove | MouseAbsolute, normX, normY)], Marshal.SizeOf<INPUT>());
    }

    private static INPUT MouseInputEx(uint flags, int normX, int normY) => new()
    {
        type = 0,
        mi = new MOUSEINPUT { dx = normX, dy = normY, mouseData = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

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
