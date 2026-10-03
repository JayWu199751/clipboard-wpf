using ClipboardTool.Domain.PendingDeletion;

namespace ClipboardTool.Tests.DeletionRules;

/// <summary>
/// 延迟删除纯规则（legacy pendingDeletion.ts transitionDeletion 移植）：
/// 两态（撤销期/提交中）五事件七效果，到期才真删，重复请求不重启，提交互斥，失败恢复。
/// </summary>
public class PendingDeletionRulesTests
{
    private static DeletionEvent Ev(DeletionEventKind kind, string id = "e1") => new(kind, id);

    private static IReadOnlyDictionary<string, DeletionPhase> StateWith(string id, DeletionPhase phase)
    {
        Dictionary<string, DeletionPhase> state = new() { [id] = phase };
        return state;
    }

    [Fact]
    public void Request_新条目_进入撤销期并隐藏起计时给撤销提示()
    {
        var transition = PendingDeletion.Transition(PendingDeletion.EmptyState(), Ev(DeletionEventKind.Request));

        Assert.Equal(DeletionPhase.Undoable, transition.State["e1"]);
        Assert.Equal(
            [DeletionEffectKind.Hide, DeletionEffectKind.StartTimer, DeletionEffectKind.ShowUndo],
            transition.Effects.Select(e => e.Kind));
        Assert.All(transition.Effects, e => Assert.Equal("e1", e.Id));
    }

    [Fact]
    public void Request_同条已在删除流程_不重启()
    {
        var state = StateWith("e1", DeletionPhase.Undoable);
        var transition = PendingDeletion.Transition(state, Ev(DeletionEventKind.Request));

        Assert.Empty(transition.Effects);
        Assert.Equal(DeletionPhase.Undoable, transition.State["e1"]);
    }

    [Fact]
    public void Undo_撤销期_取消计时恢复显示()
    {
        var state = StateWith("e1", DeletionPhase.Undoable);
        var transition = PendingDeletion.Transition(state, Ev(DeletionEventKind.Undo));

        Assert.False(transition.State.ContainsKey("e1"));
        Assert.Equal(
            [DeletionEffectKind.CancelTimer, DeletionEffectKind.Restore],
            transition.Effects.Select(e => e.Kind));
    }

    [Fact]
    public void Undo_提交中_无效()
    {
        var state = StateWith("e1", DeletionPhase.Removing);
        var transition = PendingDeletion.Transition(state, Ev(DeletionEventKind.Undo));

        Assert.Empty(transition.Effects);
        Assert.Equal(DeletionPhase.Removing, transition.State["e1"]);
    }

    [Fact]
    public void Deadline_撤销期到点_进入提交并请求删除()
    {
        var state = StateWith("e1", DeletionPhase.Undoable);
        var transition = PendingDeletion.Transition(state, Ev(DeletionEventKind.Deadline));

        Assert.Equal(DeletionPhase.Removing, transition.State["e1"]);
        Assert.Equal([DeletionEffectKind.Remove], transition.Effects.Select(e => e.Kind));
    }

    [Fact]
    public void Deadline_提交中_不重复删除()
    {
        var state = StateWith("e1", DeletionPhase.Removing);
        var transition = PendingDeletion.Transition(state, Ev(DeletionEventKind.Deadline));

        Assert.Empty(transition.Effects);
    }

    [Fact]
    public void RemoveSucceeded_提交中_收尾清状态()
    {
        var state = StateWith("e1", DeletionPhase.Removing);
        var transition = PendingDeletion.Transition(state, Ev(DeletionEventKind.RemoveSucceeded));

        Assert.False(transition.State.ContainsKey("e1"));
        Assert.Empty(transition.Effects);
    }

    [Fact]
    public void RemoveFailed_提交中_恢复显示并报错()
    {
        var state = StateWith("e1", DeletionPhase.Removing);
        var transition = PendingDeletion.Transition(state, Ev(DeletionEventKind.RemoveFailed));

        Assert.False(transition.State.ContainsKey("e1"));
        Assert.Equal(
            [DeletionEffectKind.Restore, DeletionEffectKind.ShowError],
            transition.Effects.Select(e => e.Kind));
    }

    [Fact]
    public void 多条删除互不干扰_各自独立计时与撤销()
    {
        var state = StateWith("a", DeletionPhase.Removing);
        state = PendingDeletion.Transition(state, Ev(DeletionEventKind.Request, "b")).State;

        Assert.Equal(DeletionPhase.Removing, state["a"]);
        Assert.Equal(DeletionPhase.Undoable, state["b"]);

        // 撤销 b 不影响 a
        var undoB = PendingDeletion.Transition(state, Ev(DeletionEventKind.Undo, "b"));
        Assert.Equal(DeletionPhase.Removing, undoB.State["a"]);
        Assert.False(undoB.State.ContainsKey("b"));
    }

    [Fact]
    public void 转移不改入参状态_纯函数无副作用()
    {
        var state = StateWith("e1", DeletionPhase.Undoable);
        _ = PendingDeletion.Transition(state, Ev(DeletionEventKind.Deadline));

        Assert.Equal(DeletionPhase.Undoable, state["e1"]);
    }

    [Fact]
    public void 未知事件条目_全部无效()
    {
        var state = PendingDeletion.EmptyState();
        foreach (var kind in new[]
                 {
                     DeletionEventKind.Undo, DeletionEventKind.Deadline,
                     DeletionEventKind.RemoveSucceeded, DeletionEventKind.RemoveFailed,
                 })
        {
            var transition = PendingDeletion.Transition(state, Ev(kind, "ghost"));
            Assert.Empty(transition.Effects);
        }
    }
}
