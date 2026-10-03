namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 进程内剪贴板独占段：所有 OpenClipboard 使用方共享一把进程锁（map.md 线程防线
/// 「剪贴板独占读取串行」），杜绝监视线程与粘贴链线程自己跟自己争用——系统级互斥
/// 只保证同一时刻只有一个进程打开，两个线程同时 OpenClipboard(Null) 会互相挤掉重试。
/// 打开重试在锁内小步进行；跨进程占用仍由 OpenClipboard 系统互斥 + 重试兜底。
/// 完整 STA 专用出口（消息泵）待 T06 图片侧（DIBV5/BitmapSource 需要 STA）再立项。
/// </summary>
public static class ClipboardOps
{
    private static readonly object Gate = new();

    /// <summary>进入独占段并打开剪贴板。返回 null 表示重试用尽仍被占用（调用方按「不可信」处理）。</summary>
    public static ClipboardLease? Acquire(int attempts)
    {
        ClipboardLease? lease = null;
        try
        {
            lease = new ClipboardLease(Gate); // 构造即持锁，以下任何路径经 Dispose 放锁
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    return lease;
                }
                if (attempt + 1 < attempts)
                {
                    Thread.Sleep(10);
                }
            }
            lease.Dispose();
            return null;
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }
}

/// <summary>独占段租约：构造即持进程锁，持有期间剪贴板已打开；Dispose 关闭剪贴板并放掉锁。</summary>
public sealed class ClipboardLease : IDisposable
{
    private readonly object _gate;
    private bool _closed;

    internal ClipboardLease(object gate)
    {
        _gate = gate;
        Monitor.Enter(gate);
    }

    public void Dispose()
    {
        // 幂等：Acquire 重试用尽路径会先 Dispose 一次（此时剪贴板未打开，CloseClipboard 返回 false 无害）
        if (!_closed)
        {
            _closed = true;
            _ = NativeMethods.CloseClipboard();
        }
        Monitor.Exit(_gate);
    }
}
