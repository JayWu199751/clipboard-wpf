using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace P1FocusProbe;

/// <summary>
/// P1 焦点试验：验证 WPF 无边框面板可以做到——
/// A 显示/呼出不抢前台（浏览态）；B 输入态可激活且真实键盘输入到达；C Esc 退出输入态归还前台；
/// D 停靠=屏外可见不销毁、再呼出落地且仍不抢焦点。
/// 自动序列跑完把判据写入 p1-report.md 后退出；人工复核用 --manual 保持窗口。
/// </summary>
public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_ESCAPE = 0x1B;

    private IntPtr _baselineForeground;
    private string _inputAtExit = string.Empty;
    private readonly StringBuilder _log = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNoActivate(true);
        Loaded += async (_, _) =>
        {
            if (Args.Contains("--manual"))
            {
                StateText.Text = "manual：点击面板/打字/按 Esc，观察前台变化；关窗即退出。";
                Summon();
                return;
            }
            await RunSequenceAsync();
        };
    }

    private static string[] Args => Environment.GetCommandLineArgs();

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private void ApplyNoActivate(bool on)
    {
        var style = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64();
        if (on) style |= WS_EX_NOACTIVATE; else style &= ~WS_EX_NOACTIVATE;
        _ = SetWindowLongPtr(Hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    private void Park()
    {
        // 旧版停靠是工作区右缘外 20 DIP；试验简化为远屏外，判据只看「屏外且仍可见」。
        Left = -20000;
        Top = 0;
        StateText.Text = "停靠中（屏外）";
    }

    private void Summon()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + (wa.Height - ActualHeight) / 2;
        StateText.Text = "浏览态（不抢焦点）";
    }

    private (int left, int top, int right, int bottom) PhysicalRect()
    {
        _ = GetWindowRect(Hwnd, out var r);
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    private bool ActivatePanel()
    {
        Activate();
        if (GetForegroundWindow() == Hwnd) return true;
        // AttachThreadInput 级联（legacy focus_paste.rs 同思路；完整恢复链归 P2 试验）
        var fore = GetForegroundWindow();
        uint foreTid = GetWindowThreadProcessId(fore, out _);
        uint curTid = GetCurrentThreadId();
        bool attached = foreTid != 0 && foreTid != curTid && AttachThreadInput(curTid, foreTid, true);
        try { Activate(); }
        finally { if (attached) AttachThreadInput(curTid, foreTid, false); }
        return GetForegroundWindow() == Hwnd;
    }

    private bool RestoreForeground(IntPtr target)
    {
        if (GetForegroundWindow() == target) return true;
        uint targetTid = GetWindowThreadProcessId(target, out _);
        uint curTid = GetCurrentThreadId();
        bool attached = targetTid != 0 && targetTid != curTid && AttachThreadInput(curTid, targetTid, true);
        try { _ = SetForegroundWindow(target); }
        finally { if (attached) AttachThreadInput(curTid, targetTid, false); }
        return GetForegroundWindow() == target;
    }

    private void Verdict(string id, bool pass, string detail) =>
        _log.AppendLine($"| {id} | {(pass ? "通过" : "未过")} | {detail} |");

    private async Task RunSequenceAsync()
    {
        Park();
        _baselineForeground = GetForegroundWindow();
        _log.AppendLine("# P1 焦点试验报告");
        _log.AppendLine();
        _log.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _log.AppendLine($"- 系统：{Environment.OSVersion.VersionString}，{(Environment.Is64BitProcess ? "x64" : "x86")}");
        _log.AppendLine($"- 前台基准 HWND：{_baselineForeground}");
        _log.AppendLine();
        _log.AppendLine("| 判据 | 结果 | 证据 |");
        _log.AppendLine("|---|---|---|");

        // A：初始停靠 → 显示 → 呼出，全程不抢前台
        Show();
        await Task.Delay(400);
        Verdict("A1 显示不抢焦点", GetForegroundWindow() == _baselineForeground,
            $"Show 后前台={GetForegroundWindow()}");
        Verdict("A1b 初始停靠可见（停靠不销毁）", IsWindowVisible(Hwnd),
            $"IsWindowVisible={IsWindowVisible(Hwnd)}, rect={PhysicalRect()}");

        Summon();
        await Task.Delay(400);
        var summoned = PhysicalRect();
        Verdict("A2 呼出落地且不抢焦点",
            IsWindowVisible(Hwnd) && summoned.left > -200 && GetForegroundWindow() == _baselineForeground,
            $"rect={summoned}, 前台={GetForegroundWindow()}");

        // B：进入输入态（搜索），清 NOACTIVATE 后可激活、真实键入到达
        ApplyNoActivate(false);
        SearchBox.Focus();
        bool activated = ActivatePanel();
        await Task.Delay(300);
        Verdict("B1 输入态可激活聚焦", activated && SearchBox.IsKeyboardFocused,
            $"前台==面板:{GetForegroundWindow() == Hwnd}, 键盘焦点:{SearchBox.IsKeyboardFocused}");

        SendChars("p1");
        await Task.Delay(300);
        Verdict("B2 真实键盘输入到达 TextBox", SearchBox.Text.Contains("p1"),
            $"Text=\"{SearchBox.Text}\"");
        StateText.Text = "搜索态（可聚焦）";

        // C：注入 Esc，KeyDown 处理器退出输入态并归还前台
        SendEsc();
        await Task.Delay(400);
        Verdict("C1 Esc 退出输入态归还前台",
            GetForegroundWindow() == _baselineForeground && string.IsNullOrEmpty(SearchBox.Text),
            $"前台={GetForegroundWindow()}（基准 {_baselineForeground}），输入已清空:{SearchBox.Text.Length == 0}");
        Verdict("C2 退出时输入保留过、未误发导航动作", _inputAtExit.Contains("p1"),
            $"退出瞬间输入=\"{_inputAtExit}\"");

        // D：停靠 → 再呼出，同一 HWND 复用
        IntPtr hwndBeforePark = Hwnd;
        Park();
        await Task.Delay(300);
        var parked = PhysicalRect();
        Verdict("D1 停靠=屏外可见不销毁", IsWindowVisible(Hwnd) && parked.left <= -10000,
            $"IsWindowVisible={IsWindowVisible(Hwnd)}, rect={parked}");

        Summon();
        await Task.Delay(300);
        var resummoned = PhysicalRect();
        Verdict("D2 再呼出同 HWND 落地且不抢焦点",
            Hwnd == hwndBeforePark && IsWindowVisible(Hwnd) && resummoned.left > -200
                && GetForegroundWindow() == _baselineForeground,
            $"HWND 复用:{Hwnd == hwndBeforePark}, rect={resummoned}, 前台={GetForegroundWindow()}");

        Park();
        _log.AppendLine();
        _log.AppendLine("待人工项：鼠标点击卡片不激活（WS_EX_NOACTIVATE 系统行为）、真实打字与中文 IME、多显示器呼出——用 `--manual` 参数复核。");
        var reportPath = Path.Combine(AppContext.BaseDirectory, "p1-report.md");
        await File.WriteAllTextAsync(reportPath, _log.ToString());
        Application.Current.Shutdown();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _inputAtExit = SearchBox.Text;
            ApplyNoActivate(true);
            SearchBox.Clear();
            _ = RestoreForeground(_baselineForeground);
            StateText.Text = "浏览态（不抢焦点）";
            e.Handled = true;
        }
    }

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

    private static void SendEsc()
    {
        var array = new[] { INPUT.Vk(VK_ESCAPE), INPUT.Vk(VK_ESCAPE, KEYEVENTF_KEYUP) };
        _ = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

        public static INPUT Vk(ushort vk, uint flags = 0) => new()
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
        };
    }
}
