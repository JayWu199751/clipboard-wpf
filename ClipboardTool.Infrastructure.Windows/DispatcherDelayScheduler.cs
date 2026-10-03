using System.Windows.Threading;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// IDelayScheduler 的 UI 线程实现：DispatcherTimer 延迟一拍。
/// 面板协调器（模式状态/键位差量/连发/搜索防抖）线程封闭在 UI 线程，
/// 回调经宿主 Dispatcher 到达，天然与 WM_HOTKEY、托盘回调同线程序列化。
/// </summary>
public sealed class DispatcherDelayScheduler : IDelayScheduler
{
    private readonly Dispatcher _dispatcher;

    /// <summary>绑定给定 Dispatcher（通常为 UI 线程：App.Current.Dispatcher）。</summary>
    public DispatcherDelayScheduler(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public IDisposable Delay(int milliseconds, Action callback)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)),
        };
        var token = new DelayToken();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!token.Cancelled)
            {
                callback();
            }
        };
        token.OnCancel = timer.Stop;
        timer.Start();
        return token;
    }

    private sealed class DelayToken : IDisposable
    {
        public bool Cancelled;
        public Action? OnCancel;

        public void Dispose()
        {
            if (Cancelled)
            {
                return;
            }
            Cancelled = true;
            OnCancel?.Invoke();
        }
    }
}
