using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 剪贴板变化事件源（legacy clipboard_events.rs 移植）：独立线程上的 message-only 窗口
/// + AddClipboardFormatListener + 阻塞消息循环。空闲时 0 唤醒、0 空转。
///
/// 重试必须显式安排：纯事件模型下没有「下一轮」可等——读了没拿到或落库失败，系统不会
/// 因为失败而再发一次通知。每轮返回 false 就用 SetTimer 排一次重试（50ms，实测「等到可读」
/// 最长 13.8ms 的约 3 倍余量），成功即 KillTimer；同一 ID 的 SetTimer 是重置不是叠加。
///
/// 只有「消息窗建不起来 / 格式监听注册不上」才报失败——调用方据此退回 600ms 轮询兜底，
/// 免得最坏情况从「慢」变成「静默全哑」。
/// </summary>
public sealed class ClipboardMessageSource : IClipboardChangeEventSource, IDisposable
{
    private const int RetryTimerId = 1;
    private const uint RetryIntervalMs = 50;

    private readonly TaskCompletionSource<bool> _startup = new();
    private readonly object _handlerGate = new();
    private Func<bool>? _roundHandler;
    private Action<bool>? _onReady;
    private Timer? _watchdog;
    private IntPtr _hwnd;
    private int _threadId;

    public Func<bool>? RoundHandler
    {
        get
        {
            lock (_handlerGate)
            {
                return _roundHandler;
            }
        }
        set
        {
            lock (_handlerGate)
            {
                _roundHandler = value;
            }
        }
    }

    /// <summary>启动监听线程并立即返回；就绪与否经 onReady 恰好回调一次（不阻塞调用线程——UI 不 Wait 的线程防线）。</summary>
    public void Start(Action<bool> onReady)
    {
        _onReady = onReady;
        // 看门狗：监视线程若卡死在窗口创建，2 秒后仍按失败上报（调用方退兜底轮询）
        _watchdog = new Timer(_ => Complete(false), null, 2000, Timeout.Infinite);
        var thread = new Thread(RunLoop) { Name = "ClipboardTool.ClipboardWatch", IsBackground = true };
        thread.Start();
    }

    private void Complete(bool ready)
    {
        // 只让第一次到达生效。看门狗误报（晚到）而监视线程随后就绪时，兜底轮询与事件源会并存
        // 一小段——PollRound 有轮内串行锁与序列号短路，行为正确，代价只是多余唤醒
        if (_startup.TrySetResult(ready))
        {
            _watchdog?.Dispose();
            _watchdog = null;
            _onReady?.Invoke(ready);
        }
    }

    private void RunLoop()
    {
        _threadId = (int)NativeMethods.GetCurrentThreadId();
        // message-only 窗口：不参与桌面窗口栈，只收我们注册监听的通知
        _hwnd = NativeMethods.CreateWindowExW(0, "STATIC", "clipboard-tool-watch", 0,
            0, 0, 0, 0, NativeMethods.HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Complete(false);
            return;
        }

        if (!NativeMethods.AddClipboardFormatListener(_hwnd))
        {
            _ = NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            Complete(false);
            return;
        }

        Complete(true);

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = NativeMethods.TranslateMessage(ref msg);
            _ = NativeMethods.DispatchMessageW(ref msg);
            var settled = msg.message switch
            {
                NativeMethods.WM_CLIPBOARDUPDATE => RunRound(),
                NativeMethods.WM_TIMER when msg.wParam == RetryTimerId => RunRound(),
                _ => (bool?)null,
            };
            if (settled.HasValue)
            {
                _ = settled.Value
                    ? NativeMethods.KillTimer(_hwnd, RetryTimerId)
                    : NativeMethods.SetTimer(_hwnd, RetryTimerId, RetryIntervalMs, IntPtr.Zero) != UIntPtr.Zero;
            }
        }

        _ = NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _ = NativeMethods.DestroyWindow(_hwnd);
    }

    private bool RunRound()
    {
        var handler = RoundHandler;
        return handler is null || handler();
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            _ = NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
