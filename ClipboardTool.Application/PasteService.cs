using ClipboardTool.Domain.PasteChain;

namespace ClipboardTool.Application;

/// <summary>
/// 粘贴链路的生产 IPastePort（legacy main.rs Win32PastePort 对应物）：
/// paste_chain 只管顺序与文案，六个效果在这里落地。
/// 链路整体跑在 ModeExecutor 的专用线程上（本仓库 ADR-0004）；UI 相关步骤经归队回调落地。
/// </summary>
public sealed class PasteService : IPastePort
{
    private readonly HistoryService _history;
    private readonly ClipboardWatchService _watch;
    private readonly Func<FocusTarget?> _capturedFocus;
    private readonly Func<FocusTarget, RestoreFailure?> _restoreAndPaste;
    private readonly Action _hidePanel;
    private readonly Action<string, string> _reportFocusError;

    public PasteService(
        HistoryService history,
        ClipboardWatchService watch,
        Func<FocusTarget?> capturedFocus,
        Func<FocusTarget, RestoreFailure?> restoreAndPaste,
        Action hidePanel,
        Action<string, string> reportFocusError)
    {
        _history = history;
        _watch = watch;
        _capturedFocus = capturedFocus;
        _restoreAndPaste = restoreAndPaste;
        _hidePanel = hidePanel;
        _reportFocusError = reportFocusError;
    }

    /// <summary>执行复制并粘贴（Enter 与双击卡片共用同一入口，F11/F12）。</summary>
    public CopyResult Paste(string id) => PasteChain.Run(this, id);

    public CopyContent? ContentOf(string id)
    {
        var entry = _history.Find(id);
        return entry is null ? null : new CopyContent.Text(entry.Text);
    }

    public bool WriteClipboard(CopyContent content) => content switch
    {
        // 写入与基线同步原子化（同轮内临界区）：事件轮看不到「已写入未同步」的中间态
        CopyContent.Text text => _watch.WriteAndSyncText(text.Value),
        // 图片侧（按 PNG 文件路径写位图）归 T06；当前历史里不可能出现图片条目
        CopyContent.Image => false,
        _ => false,
    };

    public void SettleAfterCopy(string id)
    {
        // 复制后的落位（提升 + 落盘广播 + 去重提升是同一规则）；
        // 基线同步已随 WriteClipboard 原子化完成（见 WriteAndSyncText）
        _history.Promote(id);
    }

    /// <summary>取本次呼出捕获的焦点快照（呼出时在 UI 线程捕获，F13/F14）。</summary>
    public FocusTarget? FocusTarget() => _capturedFocus();

    public RestoreFailure? RestoreAndPaste(FocusTarget target) => _restoreAndPaste(target);

    /// <summary>粘贴已把焦点归还原窗口，隐藏时不再重复恢复（归队 UI 线程执行）。</summary>
    public void HideAfterPaste() => _hidePanel();

    public void ReportFocusError(string stage, string reason) => _reportFocusError(stage, reason);
}
