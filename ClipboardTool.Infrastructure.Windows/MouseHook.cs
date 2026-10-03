using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 全局低级鼠标钩子（WH_MOUSE_LL，F17；legacy click_watcher.rs 的进程内移植）。
/// 面板不抢焦点（ADR-0002）收不到 blur，「点击面板外」只能由全局钩子上报；普通权限即可
/// （LL 钩子无需提权）。线程防线（本仓库 ADR-0004）：**钩子回调只记录不处理**——取坐标与
/// 时刻入队即返回（出队满即丢，绝不阻塞系统钩子链），判定与效果全部由消费侧完成。
/// 时刻必须在钩子里取（单调毫秒）：「这次点击早不早于上一次呼出」正是判该不该收起的依据
/// （Domain.ExternalClickRules），等消费者拿到时已分不出先后。
/// 结构：钩子线程（安装钩子 + 消息泵，WM_QUIT 退出）→ 有界队列 → 转发线程（触发 MouseDown）。
/// </summary>
public sealed class MouseHook : IDisposable
{
    /// <summary>一次真实按下：物理像素坐标 + 事件发生的时刻（Environment.TickCount64 单调毫秒）。</summary>
    public readonly record struct MouseDown(int X, int Y, long AtMs);

    /// <summary>队列上限：正常消费速度远超点击速率，满载（UI 冻结等极端场景）丢最旧的记录即可。</summary>
    private const int QueueCapacity = 64;

    /// <summary>真实鼠标按下（左/右/中/侧键，legacy 口径）。在转发线程触发，订阅方自行归队。</summary>
    public event Action<int, int, long>? Pressed;

    private readonly BlockingCollection<MouseDown> _queue = new(QueueCapacity);
    private readonly NativeMethods.HookProc _hookProc;
    private Thread? _hookThread;
    private Thread? _forwardThread;
    private IntPtr _hook;
    private int _hookThreadId;
    private bool _disposed;

    public MouseHook()
    {
        // 委托存字段保活：钩子回调经 P/Invoke 到达，delegate 被 GC 会直接崩进程
        _hookProc = HookProc;
    }

    /// <summary>启动钩子线程与转发线程。重复调用无操作。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hookThread is not null)
        {
            return;
        }

        _forwardThread = new Thread(ForwardLoop)
        {
            IsBackground = true,
            Name = "MouseHookForward",
        };
        _forwardThread.Start();

        _hookThread = new Thread(HookLoop)
        {
            IsBackground = true,
            Name = "MouseHook",
        };
        _hookThread.Start();
    }

    private void HookLoop()
    {
        _hookThreadId = (int)NativeMethods.GetCurrentThreadId();
        // LL 钩子的 hMod 传 Zero（钩子过程在本进程内），全线程作用域 dwThreadId=0
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _hookProc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            return; // 安装失败：无点击上报（面板外部点击停靠失效，不影响其余功能）
        }
        var msg = new NativeMethods.MSG();
        while (NativeMethods.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = NativeMethods.TranslateMessage(ref msg);
            _ = NativeMethods.DispatchMessageW(ref msg);
        }
        _ = NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>钩子回调：只记录（坐标 + 钩子内取的时刻）入队即返回，绝不处理（ADR-0004）。</summary>
    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (uint)wParam.ToInt64();
            if (message is NativeMethods.WM_LBUTTONDOWN
                or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN
                or NativeMethods.WM_XBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                _ = _queue.TryAdd(new MouseDown(info.pt.X, info.pt.Y, Environment.TickCount64));
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void ForwardLoop()
    {
        foreach (var down in _queue.GetConsumingEnumerable())
        {
            Pressed?.Invoke(down.X, down.Y, down.AtMs);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var threadId = _hookThreadId;
        if (threadId != 0)
        {
            _ = NativeMethods.PostThreadMessageW(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _queue.CompleteAdding();
        _queue.Dispose();
    }
}
