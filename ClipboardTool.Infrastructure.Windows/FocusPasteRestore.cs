using System.Runtime.InteropServices;
using ClipboardTool.Domain.PasteChain;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 焦点恢复与 Ctrl+V 注入（legacy focus_paste.rs / 本仓库 P2 探针 FocusRestore.cs 的正式移植，
/// 本仓库 ADR-0003）：Snapshot 捕获四元组、RestoreAndPaste 恢复+注入。
/// 跑在 ModeExecutor 专用线程（非 UI 线程，无需泵）；不碰 WPF 类型，纯 Win32。
/// 安全契约（P2 判据钉住）：失效目标先拒绝（PID/TID 双校验防 HWND 复用）、
/// 恢复/注入分开报、SetForegroundWindow 返回值不作数（每步回读）、注入数量必须校验、
/// 失败绝不向前台降级注入。
/// </summary>
public static class FocusPasteRestore
{
    private const uint PollStepMs = 4;
    private const uint ActivateBudgetMs = 48;
    private const int RetryGapMs = 60;

    /// <summary>捕获焦点快照（呼出时在 UI 线程调用，F13/F14）：前台顶层窗口 + 其线程焦点控件。</summary>
    public static FocusTarget? Capture()
    {
        var top = NativeMethods.GetForegroundWindow();
        if (top == IntPtr.Zero)
        {
            return null;
        }
        var tid = NativeMethods.GetWindowThreadProcessId(top, out var pid);
        if (tid == 0 || pid == 0)
        {
            return null;
        }
        var info = new NativeMethods.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        var focus = top;
        if (NativeMethods.GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero)
        {
            focus = info.hwndFocus;
        }
        return new FocusTarget(top, focus, pid, tid);
    }

    /// <summary>恢复原窗口焦点并注入 Ctrl+V（paste=false 时仅恢复）。失败重试一次（60ms 间隔）。</summary>
    public static RestoreFailure? RestoreAndPaste(FocusTarget target, bool paste)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (RestoreTarget(target))
            {
                return paste && !PasteClipboard()
                    ? new RestoreFailure("paste", "paste_send_failed")
                    : null;
            }
            Thread.Sleep(RetryGapMs);
        }
        return new RestoreFailure("restore", "restore_failed");
    }

    private static bool ValidateTarget(FocusTarget target)
    {
        var hwnd = (IntPtr)target.Hwnd;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }
        var tid = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        // PID/TID 双校验：HWND 是可复用资源，仅 IsWindow 不足以防错窗口
        return pid == (uint)target.Pid && tid == (uint)target.Tid;
    }

    private static bool IsForegroundTarget(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }
        return foreground == hwnd || NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT) == hwnd;
    }

    /// <summary>4ms 步进轮询前台归属，成功即刻返回；不信任 SetForegroundWindow 返回值。</summary>
    private static bool WaitForForeground(IntPtr hwnd, uint budgetMs)
    {
        uint waited = 0;
        while (waited < budgetMs)
        {
            if (IsForegroundTarget(hwnd))
            {
                return true;
            }
            Thread.Sleep((int)PollStepMs);
            waited += PollStepMs;
        }
        return IsForegroundTarget(hwnd);
    }

    private static bool TryActivate(IntPtr hwnd)
    {
        _ = NativeMethods.SetForegroundWindow(hwnd);
        return WaitForForeground(hwnd, ActivateBudgetMs);
    }

    /// <summary>激活级联：ASFW 授权 → 无害 Alt 取输入权 → SwitchToThisWindow → ShowWindow+SetWindowPos。</summary>
    private static bool CascadeActivate(IntPtr hwnd)
    {
        _ = NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        var ok = TryActivate(hwnd);
        if (!ok)
        {
            SendAlt();
            ok = TryActivate(hwnd);
        }
        if (!ok)
        {
            NativeMethods.SwitchToThisWindow(hwnd, altTab: true);
            ok = TryActivate(hwnd);
        }
        if (!ok)
        {
            _ = NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            ok = TryActivate(hwnd);
        }
        return ok;
    }

    private static bool RestoreTarget(FocusTarget target)
    {
        var hwnd = (IntPtr)target.Hwnd;
        var focusHwnd = (IntPtr)target.FocusHwnd;
        if (hwnd == IntPtr.Zero || !ValidateTarget(target))
        {
            return false;
        }

        // 快速路径（浏览模式常态）：目标仍是前台且原控件仍持焦 → 零操作直接通过（回车→粘贴主路径）
        if (IsForegroundTarget(hwnd))
        {
            var current = new NativeMethods.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            var probeThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            var currentFocus = probeThread != 0 && NativeMethods.GetGUIThreadInfo(probeThread, ref current)
                ? current.hwndFocus
                : IntPtr.Zero;
            if (focusHwnd == IntPtr.Zero || !NativeMethods.IsWindow(focusHwnd) || currentFocus == focusHwnd)
            {
                return true;
            }
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            _ = NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        }

        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = targetThread != 0 && NativeMethods.AttachThreadInput(currentThread, targetThread, true);

        // 面板握有前台激活权时 Windows 会拒绝直接 SetForegroundWindow：走级联，每步回读验证
        var foregroundSet = CascadeActivate(hwnd);
        _ = NativeMethods.SetFocus(focusHwnd != IntPtr.Zero && NativeMethods.IsWindow(focusHwnd) ? focusHwnd : hwnd);

        if (attached)
        {
            _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false); // 对称解绑
        }

        return foregroundSet;
    }

    /// <summary>注入 Ctrl+V；SendInput 少发即失败，不得带病继续。</summary>
    private static bool PasteClipboard()
    {
        var inputs = new[]
        {
            KeyInput(NativeMethods.VK_CONTROL, 0),
            KeyInput(NativeMethods.VK_V, 0),
            KeyInput(NativeMethods.VK_V, NativeMethods.KEYEVENTF_KEYUP),
            KeyInput(NativeMethods.VK_CONTROL, NativeMethods.KEYEVENTF_KEYUP),
        };
        return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) == (uint)inputs.Length;
    }

    private static NativeMethods.INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = 1, // INPUT_KEYBOARD
        ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = flags },
    };

    private static void SendAlt()
    {
        _ = NativeMethods.SendInput(1, [KeyInput(NativeMethods.VK_MENU, 0)], Marshal.SizeOf<NativeMethods.INPUT>());
        _ = NativeMethods.SendInput(1, [KeyInput(NativeMethods.VK_MENU, NativeMethods.KEYEVENTF_KEYUP)], Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
