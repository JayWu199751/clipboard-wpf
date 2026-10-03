namespace ClipboardTool.Domain.PasteChain;

/// <summary>焦点快照四元组（legacy 术语表：焦点快照；02-spec/02 §2 契约）。</summary>
public readonly record struct FocusTarget(long Hwnd, long FocusHwnd, long Pid, long Tid);

/// <summary>恢复与注入失败分开报（legacy 术语表：失败阶段）；stage ∈ { restore, paste }。</summary>
public sealed record RestoreFailure(string Stage, string Reason);

/// <summary>失败阶段取值（FocusErrorMessage 的分派键；两处出现以常量单源，避免裸串拼写漂移）。</summary>
public static class RestoreStage
{
    public const string Restore = "restore";
    public const string Paste = "paste";
}

/// <summary>链路要写进剪贴板的内容：文字直接写，图片按 PNG 文件路径写位图（T06 落图片侧）。</summary>
public abstract record CopyContent
{
    public sealed record Text(string Value) : CopyContent;
    public sealed record Image(string PngPath) : CopyContent;
}

/// <summary>
/// 链路结果。两个入口（键盘 Enter / 双击卡片）共用同一个契约 { ok, message }，
/// 渲染层按契约渲染提示，不再各自拼文案（legacy 术语表：结果契约）。
/// </summary>
public sealed record CopyResult(bool Ok, string Message);

/// <summary>
/// 复制并粘贴链路的唯一归属：六步顺序与结果文案（本仓库 ADR-0003/legacy ADR-0005）。
///
/// 顺序契约（测试逐条断言，改动前先看它们）：
///   content_of → write_clipboard → settle_after_copy → focus_target
///     → restore_and_paste → hide_after_paste（仅粘贴成功时）
/// 两条容易写反的次序：
///   - 落位（提升 + 落盘广播 + 同步基线）在注入之前：粘贴失败时列表仍要反映这次复制，
///     因为内容确实已经进了剪贴板；
///   - 隐藏面板在注入成功之后，且隐藏时不再重复恢复焦点（焦点已由粘贴链路归还）。
/// 失败文案与面板焦点错误事件同源：两处都只经 FocusErrorMessage 这一个函数。
/// </summary>
public static class PasteChain
{
    /// <summary>条目取不到 / 图片文件已丢失 / 剪贴板被其它程序占住，三种情况共用这一句。</summary>
    public const string EntryUnavailable = "条目不存在或内容已不可用。";

    public const string PastedOk = "已复制并粘贴";
    public const string RestoreFailedMessage = "无法恢复原输入框，已取消本次粘贴，避免写入错误窗口。";
    public const string PasteFailedMessage = "复制已写入剪贴板，但无法粘贴回原输入框，请重试。";

    /// <summary>焦点恢复与粘贴注入的失败文案（唯一映射处，与面板焦点错误事件同源）。</summary>
    public static string FocusErrorMessage(string stage) =>
        stage == RestoreStage.Paste ? PasteFailedMessage : RestoreFailedMessage;

    /// <summary>跑完整条链路。任一步失败即中止，返回给渲染层的文案与已发出的事件同源。</summary>
    public static CopyResult Run(IPastePort port, string id)
    {
        var content = port.ContentOf(id);
        if (content is null)
        {
            return new CopyResult(false, EntryUnavailable);
        }

        if (!port.WriteClipboard(content))
        {
            return new CopyResult(false, EntryUnavailable);
        }

        port.SettleAfterCopy(id);

        var target = port.FocusTarget();
        if (target is not { } resolved)
        {
            port.ReportFocusError(RestoreStage.Restore, "no_focus_target");
            return new CopyResult(false, FocusErrorMessage(RestoreStage.Restore));
        }

        var failure = port.RestoreAndPaste(resolved);
        if (failure is null)
        {
            port.HideAfterPaste();
            return new CopyResult(true, PastedOk);
        }

        port.ReportFocusError(failure.Stage, failure.Reason);
        return new CopyResult(false, FocusErrorMessage(failure.Stage));
    }
}

/// <summary>链路的全部外部效果。生产实现见 Application（真剪贴板/真恢复/面板编排），
/// 测试实现记录调用顺序，顺序与文案因此可断言。</summary>
public interface IPastePort
{
    /// <summary>1) 取条目内容；条目不存在或图片文件路径缺失 → null。</summary>
    CopyContent? ContentOf(string id);

    /// <summary>2) 写剪贴板。</summary>
    bool WriteClipboard(CopyContent content);

    /// <summary>3) 复制后的落位：提升到最近使用 + 落盘广播 + 同步轮询基线。</summary>
    void SettleAfterCopy(string id);

    /// <summary>4) 取本次呼出捕获的焦点快照。</summary>
    FocusTarget? FocusTarget();

    /// <summary>5) 恢复原窗口焦点并注入 Ctrl+V；成功返回 null。</summary>
    RestoreFailure? RestoreAndPaste(FocusTarget target);

    /// <summary>6) 粘贴成功后隐藏面板（焦点已归还，不再重复恢复）。</summary>
    void HideAfterPaste();

    /// <summary>失败回报渲染层；文案由 FocusErrorMessage 给出。</summary>
    void ReportFocusError(string stage, string reason);
}
