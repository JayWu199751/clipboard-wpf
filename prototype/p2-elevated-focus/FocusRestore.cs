using System.Diagnostics;
using System.Runtime.InteropServices;

namespace P2Focus;

/// <summary>焦点快照：顶层 HWND / 焦点控件 HWND / PID / TID（02-spec/02 §2 契约的四元组）。</summary>
public readonly record struct FocusTarget(long Hwnd, long FocusHwnd, long Pid, long Tid);

/// <summary>恢复与注入失败分开报；stage ∈ { restore, paste }。</summary>
public sealed record RestoreFailure(string Stage, string Reason);

public sealed class RunStats
{
    public bool FastPathHit;
    public int ActivateAttempts;
    public int AttachCalls;
    public bool AltSent;
    public int SendInputKeys;
    public long ElapsedMs;
    public override string ToString() =>
        $"fastpath={FastPathHit} activate={ActivateAttempts} attach={AttachCalls} alt={AltSent} keys={SendInputKeys} ms={ElapsedMs}";
}

/// <summary>
/// legacy focus_paste.rs 的 C# 移植（T02 将据此实现 Infrastructure.Windows.FocusAdapter）。
/// 顺序与安全约束不变：目标失效必须先拒绝（PID/TID 校验防 HWND 复用）、恢复/注入分开报、
/// SetForegroundWindow 返回值不是唯一事实（每步 try_activate 都回读前台归属）、注入数量必须校验。
/// </summary>
public static class FocusRestore
{
    // 探针在 UI 线程上串行执行，等待循环需抽水；正式 FocusAdapter 跑在专用线程，保持 null
    public static Action<int>? PumpHook;

    public static RunStats LastRun = new();

    static void Pump(int ms) => PumpHook?.Invoke(ms);

    public static FocusTarget? Snapshot()
    {
        var top = Win32.GetForegroundWindow();
        if (top == IntPtr.Zero) return null;
        uint tid = Win32.GetWindowThreadProcessId(top, out uint pid);
        if (tid == 0 || pid == 0) return null;
        var info = new Win32.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Win32.GUITHREADINFO>() };
        var focus = top;
        if (Win32.GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero) focus = info.hwndFocus;
        return new FocusTarget(top, focus, pid, tid);
    }

    static bool ValidateTarget(FocusTarget target)
    {
        var hwnd = (IntPtr)target.Hwnd;
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        uint tid = Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        // PID/TID 双校验：HWND 是可复用资源，仅 IsWindow 不足以防错窗口
        return pid == target.Pid && tid == target.Tid;
    }

    static bool IsForegroundTarget(IntPtr hwnd)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        return fg == hwnd || Win32.GetAncestor(fg, Win32.GA_ROOT) == hwnd;
    }

    // 4ms 步进轮询前台归属，成功即刻返回；不信任 SetForegroundWindow 返回值
    static bool WaitForForeground(IntPtr hwnd, uint budgetMs)
    {
        uint waited = 0;
        while (waited < budgetMs)
        {
            if (IsForegroundTarget(hwnd)) return true;
            Pump(4);
            waited += 4;
        }
        return IsForegroundTarget(hwnd);
    }

    public static void SendAlt()
    {
        Win32.SendInputKey(Win32.VK_MENU, 0);
        Win32.SendInputKey(Win32.VK_MENU, Win32.KEYEVENTF_KEYUP);
    }

    public static bool TryActivate(IntPtr hwnd)
    {
        LastRun.ActivateAttempts++;
        Win32.SetForegroundWindow(hwnd);
        return WaitForForeground(hwnd, 48);
    }

    static bool RestoreTarget(FocusTarget target)
    {
        var hwnd = (IntPtr)target.Hwnd;
        var focusHwnd = (IntPtr)target.FocusHwnd;
        if (hwnd == IntPtr.Zero || !ValidateTarget(target)) return false;

        // 快速路径（浏览模式常态）：目标仍是前台且原控件仍持焦 → 零操作直接通过
        if (IsForegroundTarget(hwnd))
        {
            var current = new Win32.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Win32.GUITHREADINFO>() };
            uint probeThread = Win32.GetWindowThreadProcessId(hwnd, out _);
            var currentFocus = probeThread != 0 && Win32.GetGUIThreadInfo(probeThread, ref current)
                ? current.hwndFocus
                : IntPtr.Zero;
            if (focusHwnd == IntPtr.Zero || !Win32.IsWindow(focusHwnd) || currentFocus == focusHwnd)
            {
                LastRun.FastPathHit = true;
                return true;
            }
        }

        if (Win32.IsIconic(hwnd)) Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);

        uint targetThread = Win32.GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = Win32.GetCurrentThreadId();
        bool attached = targetThread != 0 && Win32.AttachThreadInput(currentThread, targetThread, true);
        if (attached) LastRun.AttachCalls++;

        // 面板握有前台激活权时 Windows 会拒绝直接 SetForegroundWindow：
        // 先 ASFW 授权，再以一次无害 Alt 取得输入权，然后逐级重试（每步回读验证，成功即刻返回）
        Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
        bool foregroundSet = TryActivate(hwnd);
        if (!foregroundSet)
        {
            SendAlt();
            LastRun.AltSent = true;
            foregroundSet = TryActivate(hwnd);
        }
        if (!foregroundSet)
        {
            Win32.SwitchToThisWindow(hwnd, true);
            foregroundSet = TryActivate(hwnd);
        }
        if (!foregroundSet)
        {
            Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
            Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
            foregroundSet = TryActivate(hwnd);
        }
        Win32.SetFocus(focusHwnd != IntPtr.Zero && Win32.IsWindow(focusHwnd) ? focusHwnd : hwnd);

        if (attached) Win32.AttachThreadInput(currentThread, targetThread, false); // 对称解绑

        return foregroundSet;
    }

    public static bool PasteClipboard()
    {
        // 仅探针用来演示 paste 阶段失败路径；正式实现无此钩子
        if (Environment.GetEnvironmentVariable("P2_FORCE_PASTE_FAIL") == "1") return false;
        uint n = Win32.SendCtrlV();
        LastRun.SendInputKeys = (int)n;
        // 注入数量必须校验：SendInput 少发即视为失败，不得带病继续
        return n == 4;
    }

    /// <summary>恢复原窗口焦点并注入 Ctrl+V（paste=false 时仅恢复）。失败重试一次（60ms 间隔）。</summary>
    public static (bool Ok, RestoreFailure? Failure) RestoreAndPaste(FocusTarget target, bool paste)
    {
        LastRun = new RunStats();
        var sw = Stopwatch.StartNew();
        bool restored = false;
        string reason = "restore_failed";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (RestoreTarget(target)) { restored = true; break; }
            reason = "restore_failed";
            Pump(60);
        }
        LastRun.ElapsedMs = sw.ElapsedMilliseconds;
        if (!restored) return (false, new RestoreFailure("restore", reason));
        if (paste && !PasteClipboard()) return (false, new RestoreFailure("paste", "paste_send_failed"));
        return (true, null);
    }
}
