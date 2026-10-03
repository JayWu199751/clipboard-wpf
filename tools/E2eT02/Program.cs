using ClipboardTool.Domain.PasteChain;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

// T02 真机端到端验证（工单 03 完成判据的自动化部分）：
//   复制（真实剪贴板写入）→ 应用监听记录 → 呼出面板出现卡片 →
//   焦点在记事本 → WM_HOTKEY 直投呼出（合成输入不触发 RegisterHotKey）→
//   UIA 定位卡片 + 真实鼠标合成双击 → 六步粘贴链路 → 记事本收到内容（WM_GETTEXT 回读）。
// 不覆盖（人工项）：真实键盘 Enter、目标窗口关闭失败路径、光标原位观感。

const string Payload1 = "E2E-T02-第一条-1701";
const string Payload2 = "E2E-T02-第二条-1702+suffix";

// DPI 感知：UIA 矩形与 SendInput 归一化都必须基于物理像素（本机 175%，不声明则被虚拟化到 96 DPI）
_ = Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

var failures = new List<string>();

// ---------- 1) 启动被测应用 ----------
var app = Process.Start(new ProcessStartInfo(FindAppExe()) { UseShellExecute = false })!;
Process notepad = null!;
try
{
    var panelHwnd = WaitPanelWindow(app, TimeSpan.FromSeconds(10));
    Check(panelHwnd != IntPtr.Zero, "应用启动且面板窗口存在");

    // ---------- 2) 复制两条：监听→记录→卡片 ----------
    SetClipboardText(Payload1);
    Check(WaitCard(app, Payload1, TimeSpan.FromSeconds(5)), $"剪贴板监听记录第一条（{Payload1}）");

    SetClipboardText(Payload2);
    Check(WaitCard(app, Payload2, TimeSpan.FromSeconds(5)), $"剪贴板监听记录第二条（{Payload2}）");

    // ---------- 3) 起记事本并置前台（快照应落在记事本） ----------
    notepad = LaunchNotepad();
    var notepadHwnd = WaitForNotepadWindow(notepad, TimeSpan.FromSeconds(10));
    Check(notepadHwnd != IntPtr.Zero, "普通记事本已启动");
    Thread.Sleep(300);
    ForceForeground(notepadHwnd);
    Thread.Sleep(300);

    // ---------- 4) 呼出面板（WM_HOTKEY id=1 = 首个注册的呼出键） ----------
    _ = Native.PostMessage(panelHwnd, 0x0312, (IntPtr)1, IntPtr.Zero); // WM_HOTKEY
    Thread.Sleep(800);
    Check(CountCards(app) == 2, "呼出后恰有两条卡片（连续复制无重复条目）");

    // ---------- 5) 双击第二条卡片（真实鼠标合成输入） ----------
    // 抗抖动：呼出/寻卡的几秒里用户并行操作可能切走前台，而快照恢复目标=呼出时的记事本；
    // 双击前重新置前台并验证，把「注入落进别的窗口」的竞争窗口压到最小
    ForceForeground(notepadHwnd);
    var fresh = WaitCardElement(app, Payload2, TimeSpan.FromSeconds(5))
        ?? throw new InvalidOperationException("呼出后找不到第二条卡片");
    var rect = fresh.Current.BoundingRectangle;
    Check(rect.Width > 0, "卡片可命中（UIA 矩形有效）");
    Native.DoubleClick((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));

    // ---------- 6) 记事本回读（失败时读面板状态提示辅助定位） ----------
    var landed = WaitForNotepadText(notepadHwnd, Payload2, TimeSpan.FromSeconds(8));
    if (!landed)
    {
        var status = ReadStatusText(app);
        Console.WriteLine($"诊断：面板状态提示 = {status ?? "（无）"}");
        Console.WriteLine($"诊断：面板矩形 = {DescribePanelRect(app)}（x>2560≈屏外停靠=hide_after_paste 已执行）");
        Console.WriteLine($"诊断：剪贴板当前文本 = {Native.ReadClipboardText() ?? "（读不到）"}");
        Console.WriteLine($"诊断：卡片数 = {CountCards(app)}");
    }
    Check(landed, "粘贴链路落靶：记事本收到第二条内容");
    Check(FindCard(app, Payload1), "粘贴后第一条仍在列表（列表反映复制）");
}
catch (Exception ex)
{
    failures.Add("异常：" + ex.Message);
    Console.WriteLine(ex);
}
finally
{
    try { notepad.CloseMainWindow(); } catch { }
    try { notepad.Kill(); } catch { }
    try { app.CloseMainWindow(); } catch { }
    try { app.Kill(); } catch { }
}

Console.WriteLine(failures.Count == 0 ? "E2E 全部通过" : $"E2E 失败 {failures.Count} 项");
return failures.Count == 0 ? 0 : 1;

void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok)
    {
        failures.Add(name);
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
                return full;
            }
        }
    }
    throw new FileNotFoundException("未找到被测应用 exe（先 dotnet build -c Release）");
}

static IntPtr WaitPanelWindow(Process app, TimeSpan timeout)
{
    // 面板初始停靠屏外，Process.MainWindowHandle 不可靠：经 UIA 取进程顶层窗口的原生句柄
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

static bool WaitCard(Process app, string payload, TimeSpan timeout)
{
    var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        if (FindCard(app, payload))
        {
            return true;
        }
        Thread.Sleep(200);
    }
    return false;
}

static string? ReadStatusText(Process app)
{
    try
    {
        var root = AutomationElement.RootElement.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
        if (root is null)
        {
            return null;
        }
        var texts = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        foreach (AutomationElement text in texts)
        {
            var name = text.Current.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            if (name.Contains(PasteChain.EntryUnavailable, StringComparison.Ordinal) ||
                name.Contains(PasteChain.RestoreFailedMessage, StringComparison.Ordinal) ||
                name.Contains(PasteChain.PasteFailedMessage, StringComparison.Ordinal) ||
                name.Contains(PasteChain.PastedOk, StringComparison.Ordinal))
            {
                return name;
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

static string DescribePanelRect(Process app)
{
    try
    {
        var root = AutomationElement.RootElement.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
        return root is null ? "（窗口不存在）" : root.Current.BoundingRectangle.ToString();
    }
    catch (ElementNotAvailableException)
    {
        return "（窗口不可用）";
    }
}

static AutomationElement? WaitCardElement(Process app, string payload, TimeSpan timeout)
{
    var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        var element = FindCardElement(app, payload);
        if (element is not null)
        {
            return element;
        }
        Thread.Sleep(200);
    }
    return null;
}

static bool FindCard(Process app, string payload) => FindCardElement(app, payload) is not null;

static int CountCards(Process app)
{
    try
    {
        var root = AutomationElement.RootElement.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
        if (root is null)
        {
            return -1;
        }
        var cards = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        return cards.Count;
    }
    catch (ElementNotAvailableException)
    {
        return -1;
    }
}

static AutomationElement? FindCardElement(Process app, string payload)
{
    try
    {
        var root = AutomationElement.RootElement.FindFirst(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
        if (root is null)
        {
            return null;
        }
        var cards = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        foreach (AutomationElement card in cards)
        {
            if (card.Current.Name.Contains(payload, StringComparison.Ordinal))
            {
                return card;
            }
        }
    }
    catch (ElementNotAvailableException) { }
    return null;
}

static Process LaunchNotepad()
{
    var before = new HashSet<int>(Process.GetProcessesByName("Notepad").Select(p => p.Id));
    var launcher = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"))
    {
        UseShellExecute = false,
    })!;
    var deadline = Environment.TickCount64 + 12_000;
    while (Environment.TickCount64 < deadline)
    {
        var candidate = Process.GetProcessesByName("Notepad")
            .FirstOrDefault(p => !before.Contains(p.Id) && p.Id != launcher.Id && p.MainWindowHandle != IntPtr.Zero);
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

static bool WaitForNotepadText(IntPtr hwnd, string payload, TimeSpan timeout)
{
    var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    while (Environment.TickCount64 < deadline)
    {
        var text = Native.ReadEditorText(hwnd);
        if (text is not null && text.Contains(payload, StringComparison.Ordinal))
        {
            return true;
        }
        Thread.Sleep(200);
    }
    return false;
}

/// <summary>Alt 合成键授予前台权后置前台并回读验证（合成 SetForegroundWindow 需要输入权）。</summary>
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
    throw new InvalidOperationException("无法把记事本置为前台（回读失败）");
}

static void SetClipboardText(string text)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            WriteClipboardWin32(text);
            return;
        }
        catch (Exception) when (attempt < 10)
        {
            Thread.Sleep(50);
        }
    }
}

static void WriteClipboardWin32(string text)
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
        _ = Native.SetClipboardData(13, handle);
    }
    finally
    {
        _ = Native.CloseClipboard();
    }
}

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
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr hwnd, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder text);
    [DllImport("user32.dll")] internal static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    private const uint WmGetTextLength = 0x000E;
    private const uint WmGetText = 0x000D;
    private const ushort VkMenu = 0x12;
    private const uint KeyUp = 0x0002;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;

    /// <summary>合成一次 Alt 按下/抬起：授予调用进程 SetForegroundWindow 的输入权（legacy 手法）。</summary>
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
        type = 1, // INPUT_KEYBOARD
        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    // INPUT 联合体必须含 MOUSEINPUT，否则 x64 上 Marshal.SizeOf 少 8 字节，SendInput 以 ERROR_INVALID_PARAMETER 拒收
    // （布局照抄 prototype/p2-elevated-focus/Win32.cs：键盘成员 wVk 在 offset 8、dwFlags 在 offset 12）
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

    internal static void DoubleClick(int x, int y)
    {
        // 绝对坐标按主屏尺寸归一化到 0..65535
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

    private static INPUT Mouse(uint flags, int x, int y) => new()
    {
        type = 0, // INPUT_MOUSE
        mi = new MOUSEINPUT { dx = x, dy = y, mouseData = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    /// <summary>读当前剪贴板 CF_UNICODETEXT 文本（诊断用）。</summary>
    internal static string? ReadClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }
        try
        {
            var handle = GetClipboardData(13);
            if (handle == IntPtr.Zero)
            {
                return null;
            }
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    /// <summary>读取记事本编辑器内容（新记事本编辑器类 RichEditD2DPT；WM_GETTEXT 跨进程系统封送）。</summary>
    internal static string? ReadEditorText(IntPtr notepadHwnd)
    {
        string? best = null;
        EnumChildWindows(notepadHwnd, (child, lParam) =>
        {
            var className = new StringBuilder(256);
            _ = GetClassNameW(child, className, 256);
            var name = className.ToString();
            if (name.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Edit", StringComparison.OrdinalIgnoreCase))
            {
                var length = (int)SendMessageW(child, WmGetTextLength, IntPtr.Zero, IntPtr.Zero);
                if (length > 0)
                {
                    var sb = new StringBuilder(length + 1);
                    _ = SendMessageW(child, WmGetText, (IntPtr)(length + 1), sb);
                    best = sb.ToString();
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
