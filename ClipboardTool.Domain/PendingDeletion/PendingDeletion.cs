namespace ClipboardTool.Domain.PendingDeletion;

/// <summary>延迟删除阶段（legacy pendingDeletion.ts DeletionPhase）：撤销期 → 提交中。</summary>
public enum DeletionPhase
{
    Undoable,
    Removing,
}

/// <summary>延迟删除事件种类（legacy DeletionEvent）。</summary>
public enum DeletionEventKind
{
    Request,
    Undo,
    Deadline,
    RemoveSucceeded,
    RemoveFailed,
}

/// <summary>延迟删除效果种类（legacy DeletionEffect）：hide/start-timer/show-undo/cancel-timer/restore/remove/show-error。</summary>
public enum DeletionEffectKind
{
    Hide,
    StartTimer,
    ShowUndo,
    CancelTimer,
    Restore,
    Remove,
    ShowError,
}

/// <summary>一次延迟删除事件：种类 + 目标条目。</summary>
public readonly record struct DeletionEvent(DeletionEventKind Kind, string Id);

/// <summary>一次延迟删除效果：种类 + 目标条目。</summary>
public readonly record struct DeletionEffect(DeletionEffectKind Kind, string Id);

/// <summary>转移结果：新状态 + 待执行效果（legacy DeletionTransition）。</summary>
public sealed record DeletionTransition(
    IReadOnlyDictionary<string, DeletionPhase> State,
    IReadOnlyList<DeletionEffect> Effects);

/// <summary>
/// 延迟删除状态机（F25；legacy pendingDeletion.ts transitionDeletion 逐语义移植）：
/// 状态和每一步允许的转移在这里判定，计时/toast/删除由服务执行。
/// 撤销期条目只是从可见列表摘除（渲染层遮罩），到期（Deadline）才转提交并由服务真删；
/// 提交互斥（提交中不可撤销/不重复删除），失败恢复可见并报错；未到期强退条目自然保留。
/// </summary>
public static class PendingDeletion
{
    /// <summary>空状态（初始/强退清理后）。</summary>
    public static IReadOnlyDictionary<string, DeletionPhase> EmptyState() =>
        new Dictionary<string, DeletionPhase>();

    private static readonly DeletionTransition UnchangedEmpty =
        new(new Dictionary<string, DeletionPhase>(), []);

    /// <summary>状态转移（纯函数）：不修改入参字典，返回新字典与效果序列。</summary>
    public static DeletionTransition Transition(
        IReadOnlyDictionary<string, DeletionPhase> state, DeletionEvent @event)
    {
        var known = state.TryGetValue(@event.Id, out var phase);

        switch (@event.Kind)
        {
            case DeletionEventKind.Request:
                if (known)
                {
                    return SameState(state); // 同条重复删除不重启
                }
                var requested = Clone(state);
                requested[@event.Id] = DeletionPhase.Undoable;
                return new DeletionTransition(
                    requested,
                    [
                        new DeletionEffect(DeletionEffectKind.Hide, @event.Id),
                        new DeletionEffect(DeletionEffectKind.StartTimer, @event.Id),
                        new DeletionEffect(DeletionEffectKind.ShowUndo, @event.Id),
                    ]);
            case DeletionEventKind.Undo:
                if (!known || phase != DeletionPhase.Undoable)
                {
                    return SameState(state);
                }
                var undone = Clone(state);
                undone.Remove(@event.Id);
                return new DeletionTransition(
                    undone,
                    [
                        new DeletionEffect(DeletionEffectKind.CancelTimer, @event.Id),
                        new DeletionEffect(DeletionEffectKind.Restore, @event.Id),
                    ]);
            case DeletionEventKind.Deadline:
                if (!known || phase != DeletionPhase.Undoable)
                {
                    return SameState(state);
                }
                var deadline = Clone(state);
                deadline[@event.Id] = DeletionPhase.Removing;
                return new DeletionTransition(
                    deadline,
                    [new DeletionEffect(DeletionEffectKind.Remove, @event.Id)]);
            case DeletionEventKind.RemoveSucceeded:
                if (!known || phase != DeletionPhase.Removing)
                {
                    return SameState(state);
                }
                var succeeded = Clone(state);
                succeeded.Remove(@event.Id);
                return new DeletionTransition(succeeded, []);
            case DeletionEventKind.RemoveFailed:
                if (!known || phase != DeletionPhase.Removing)
                {
                    return SameState(state);
                }
                var failed = Clone(state);
                failed.Remove(@event.Id);
                return new DeletionTransition(
                    failed,
                    [
                        new DeletionEffect(DeletionEffectKind.Restore, @event.Id),
                        new DeletionEffect(DeletionEffectKind.ShowError, @event.Id),
                    ]);
            default:
                return SameState(state);
        }
    }

    private static DeletionTransition SameState(IReadOnlyDictionary<string, DeletionPhase> state) =>
        new(state, []);

    private static Dictionary<string, DeletionPhase> Clone(IReadOnlyDictionary<string, DeletionPhase> state) =>
        new(state);
}
