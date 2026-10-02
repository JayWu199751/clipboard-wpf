using System.Windows;
using System.Windows.Interop;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 面板焦点策略的平台侧（本仓库 ADR-0002，P1 已验证）：
/// 浏览态常驻 WS_EX_NOACTIVATE；进入输入态清除后聚焦/级联激活；
/// 退出输入态恢复 NOACTIVATE 并归还前台。
/// </summary>
public static class FocusAdapter
{
    public static void SetNoActivate(Window window, bool on)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var style = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style = on
            ? style | NativeMethods.WS_EX_NOACTIVATE
            : style & ~NativeMethods.WS_EX_NOACTIVATE;
        _ = NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>
    /// 常驻挂上 WS_EX_TOOLWINDOW（ADR-0002 决策 1；任务栏/Alt+Tab 消失靠外壳按实时样式评估，
    /// legacy 教训：必须显式置位，不能依赖框架的 skipTaskbar 一次性调用）。
    /// 该位在任何面板模式下都不清除。
    /// </summary>
    public static void EnsureToolWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var style = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW;
        _ = NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>输入态激活：直接 Activate，失败时 AttachThreadInput 级联（legacy focus_paste.rs 同思路）。</summary>
    public static bool ActivateForInput(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        window.Activate();
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try { window.Activate(); }
        finally { if (attached) _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false); }
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>把前台归还给焦点快照目标（完整恢复链由粘贴链路负责，归 P2/T02）。</summary>
    public static bool RestoreForeground(IntPtr target)
    {
        if (target == IntPtr.Zero || NativeMethods.GetForegroundWindow() == target) return true;

        var targetThread = NativeMethods.GetWindowThreadProcessId(target, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try { _ = NativeMethods.SetForegroundWindow(target); }
        finally { if (attached) _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false); }
        return NativeMethods.GetForegroundWindow() == target;
    }
}
