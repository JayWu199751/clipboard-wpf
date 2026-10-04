using ClipboardTool.Domain.History;

namespace ClipboardTool.Application;

/// <summary>剪贴板监听端口契约（Application 定义端口，Infrastructure.Windows 落平台实现）。</summary>

/// <summary>一次读取的快照。text 为空串表示没有文字；png 为 null 表示没有可解的位图（T06 接入）。</summary>
public sealed record ClipboardSnapshot(string Text, byte[]? Png);

/// <summary>
/// 「读不到」与「剪贴板里就是没有内容」是两件事，必须分开（legacy clipboard.rs）：
/// 混为一谈的代价是把「打不开」当成「空」接受，基线被记成空、序列号照旧推进、
/// 下一轮被短路——用户刚按下 Ctrl+C 的那次复制从此永久消失。
/// </summary>
public abstract record ClipboardReadOutcome
{
    public sealed record Known(ClipboardSnapshot Snapshot) : ClipboardReadOutcome;

    /// <summary>剪贴板被别的程序占着，这次读取不可信：不要推进基线，稍后重试。</summary>
    public sealed record Occupied : ClipboardReadOutcome;
}

/// <summary>独占读取：打开重试、取完字节立即关闭、解码在独占段之外（F10）。</summary>
public interface IClipboardReader
{
    ClipboardReadOutcome Read();
}

/// <summary>写剪贴板。写文字直接写；写图片按 PNG 文件路径落位图内容（F11，保透明通道）。</summary>
public interface IClipboardWriter
{
    bool WriteText(string text);

    /// <summary>写图片（T06）：按路径按需读盘，写位图内容（DIBV5+DIB，非文件路径）；
    /// 文件读不出或被占用返回 false（调用方基线不动）。</summary>
    bool WriteImage(string pngPath);
}

/// <summary>剪贴板序列号：Win32 全局计数器，任何写操作都会 +1；读取不需要打开剪贴板。</summary>
public interface IClipboardSequence
{
    uint Current();
}

/// <summary>
/// 来源应用采集端口（F06，票 14）：复制处理时取前台窗口信息（legacy source_app.rs 口径）。
/// 返回 null 表示采集不可得（无前台/查不到进程），落库与显示按「未知来源」处理；
/// SourceApp 各字段允许空串（exe 查不到但窗口标题可得等中间态，legacy 同样原样保留）。
/// 实现必须自吞异常、快速返回——采集在任何情况下都不得阻塞记录链路（监听线程防线）。
/// </summary>
public interface IForegroundSource
{
    SourceApp? Capture();
}

/// <summary>
/// 剪贴板变化事件源（F09）：message-only 窗口 + WM_CLIPBOARDUPDATE。
/// RoundHandler 返回 true 表示本轮尘埃落定；false 表示没读到或落库失败——事件模型下没有
/// 「下一轮」可等，实现方必须显式安排重试（50ms 周期定时器，成功即撤）。
/// 启动是异步就绪制：Start 立即返回、不阻塞调用线程（UI 不 Wait 的线程防线）；
/// 就绪与否经 onReady 回调报出（false = 消息窗建不起来/注册失败），调用方据此退 600ms 轮询兜底。
/// </summary>
public interface IClipboardChangeEventSource
{
    /// <summary>启动事件源，立即返回。onReady 恰好回调一次：true=可用；false=不可用（走轮询兜底，最坏是慢，不是全哑）。</summary>
    void Start(Action<bool> onReady);

    /// <summary>每收到变化通知（或重试定时器到点）调用一次，返回值决定是否撤掉重试定时器。</summary>
    Func<bool>? RoundHandler { get; set; }
}
