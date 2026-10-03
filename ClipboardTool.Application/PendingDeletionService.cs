using ClipboardTool.Domain.History;
using ClipboardTool.Domain.PendingDeletion;

namespace ClipboardTool.Application;

/// <summary>删除相关 toast 载荷（legacy pushToast 的删除分支）：文案、次级说明、是否错误、可选动作。</summary>
public sealed record PendingDeletionToast(
    string Message,
    string? Dim,
    bool IsError,
    string? ActionLabel,
    Action? OnAction);

/// <summary>
/// 延迟删除服务（F25；legacy App.tsx dispatchDeletion 效果执行侧）：
/// 状态机判定走 Domain.PendingDeletion，本类只执行效果——立即从可见列表摘除（HiddenIds）、
/// 经 IDelayScheduler 安排 6 秒到期、到期经 HistoryService 真删、撤销取消计时恢复显示、
/// 失败恢复可见并报错。计时跨停靠/呼出继续（撤销窗口是秒表语义，不随面板隐藏重置）；
/// 未到期强退条目自然保留（到期才持久化删除）。
/// 线程模型：全部方法与事件都在调用方编排线程（WPF UI 线程）上，与 PanelCoordinator 同封闭。
/// </summary>
public sealed class PendingDeletionService : IDisposable
{
    /// <summary>撤销窗口时长（legacy DELETE_UNDO_MS = 6000）。</summary>
    public const int UndoWindowMs = 6000;

    /// <summary>撤销 toast 展示时长与窗口同步（legacy pushToast：带 actionLabel 的 toast 生命 = DELETE_UNDO_MS）。</summary>
    public const int UndoToastMs = UndoWindowMs;

    private readonly IDelayScheduler _scheduler;
    private readonly HistoryService _history;
    private IReadOnlyDictionary<string, DeletionPhase> _state = PendingDeletion.EmptyState();
    private readonly Dictionary<string, IDisposable> _timers = [];

    /// <summary>隐藏集合变化（摘除/恢复/到期收尾）：渲染层重载可见列表。UI 线程触发。</summary>
    public event Action? HiddenChanged;

    /// <summary>删除相关 toast（撤销提示/失败报错）。UI 线程触发。</summary>
    public event Action<PendingDeletionToast>? ToastRequested;

    /// <summary>当前被摘除（删除流程中）的条目 id。</summary>
    public IReadOnlyCollection<string> HiddenIds => _state.Keys.ToArray();

    public PendingDeletionService(IDelayScheduler scheduler, HistoryService history)
    {
        _scheduler = scheduler;
        _history = history;
    }

    /// <summary>请求删除（Del）：同条已在删除流程则不重启（重复请求幂等）。
    /// Request 独立执行转移是因为 ShowUndo 需要 StartTimer 前的条目快照（条目仍在 store），
    /// 其余事件统一走 Apply——Domain 效果里的 ShowUndo 在此与 StartTimer 合并为同一次入栈。</summary>
    public void Request(string id)
    {
        var transition = PendingDeletion.Transition(_state, new DeletionEvent(DeletionEventKind.Request, id));
        if (transition.Effects.Count == 0 && ReferenceEquals(transition.State, _state))
        {
            return;
        }
        _state = transition.State;
        foreach (var effect in transition.Effects)
        {
            switch (effect.Kind)
            {
                case DeletionEffectKind.Hide:
                    HiddenChanged?.Invoke();
                    break;
                case DeletionEffectKind.StartTimer:
                    // Request 时条目还在 store（摘除只是渲染遮罩）→ 取内容快照供 toast 展示
                    var entry = _history.Find(id);
                    _timers[id] = _scheduler.Delay(UndoWindowMs, () => Deadline(id));
                    ToastRequested?.Invoke(new PendingDeletionToast(
                        "已删除",
                        Describe(entry),
                        IsError: true,
                        "撤销",
                        () => Undo(id)));
                    break;
            }
        }
    }

    /// <summary>撤销（toast 动作按钮）：取消计时、恢复显示。</summary>
    public void Undo(string id)
    {
        Apply(new DeletionEvent(DeletionEventKind.Undo, id));
    }

    /// <summary>清全部计时与状态（进程退出收尾）：未到期条目自然保留在 store。</summary>
    public void Dispose()
    {
        foreach (var timer in _timers.Values)
        {
            timer.Dispose();
        }
        _timers.Clear();
        _state = PendingDeletion.EmptyState();
    }

    /// <summary>
    /// 清空历史前的协调入口（F33）：托盘「清空历史」会把全部条目连同尚未到期的删除流程目标
    /// 一并删掉——先取消全部到期计时、清空摘除状态并通知渲染层重载，否则残留计时到期后
    /// 会在 store 里找不到条目而误报「删除失败」。调用方随后执行 HistoryService.Clear()
    /// （含置顶与 PNG 联动），无确认窗（legacy 行为）。
    /// </summary>
    public void ClearAll()
    {
        if (_timers.Count == 0 && _state.Count == 0)
        {
            return;
        }
        foreach (var timer in _timers.Values)
        {
            timer.Dispose();
        }
        _timers.Clear();
        _state = PendingDeletion.EmptyState();
        HiddenChanged?.Invoke();
    }

    private void Deadline(string id)
    {
        _ = _timers.Remove(id);
        Apply(new DeletionEvent(DeletionEventKind.Deadline, id));
    }

    /// <summary>事件统一入口：转移 → 执行效果。Remove 在此同步真删（HistoryService 线程安全）。</summary>
    private void Apply(DeletionEvent @event)
    {
        var transition = PendingDeletion.Transition(_state, @event);
        if (transition.Effects.Count == 0 && ReferenceEquals(transition.State, _state))
        {
            return; // 无效转移（如提交中撤销）
        }
        _state = transition.State;
        foreach (var effect in transition.Effects)
        {
            switch (effect.Kind)
            {
                case DeletionEffectKind.CancelTimer:
                    if (_timers.Remove(@event.Id, out var timer))
                    {
                        timer.Dispose();
                    }
                    break;
                case DeletionEffectKind.Restore:
                    HiddenChanged?.Invoke();
                    break;
                case DeletionEffectKind.Remove:
                    var removed = _history.Remove(@event.Id);
                    Apply(new DeletionEvent(
                        removed ? DeletionEventKind.RemoveSucceeded : DeletionEventKind.RemoveFailed,
                        @event.Id));
                    break;
                case DeletionEffectKind.ShowError:
                    ToastRequested?.Invoke(new PendingDeletionToast(
                        "删除失败，请重试", Dim: null, IsError: true, ActionLabel: null, OnAction: null));
                    break;
            }
        }
    }

    /// <summary>撤销 toast 的次级说明（legacy show-undo dim：content.replace(/\s+/g,' ').slice(0,18)——
    /// 空白序列折叠为单空格、含前导，再取前 18 字符）：图片用文件名，文字用正文。</summary>
    private static string? Describe(HistoryEntry? entry) => entry switch
    {
        null => null,
        { Type: EntryKind.Image, ImagePath: { } path } => Path.GetFileName(path),
        { Text: { Length: > 0 } text } => Compress(text),
        _ => null,
    };

    private static string Compress(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length == 0 || sb[^1] != ' ')
                {
                    sb.Append(' '); // 空白序列折叠为单空格（含前导，与 legacy replace 语义一致）
                }
                continue;
            }
            sb.Append(ch);
            if (sb.Length >= 18)
            {
                break;
            }
        }
        return sb.Length > 18 ? sb.ToString(0, 18) : sb.ToString();
    }
}
