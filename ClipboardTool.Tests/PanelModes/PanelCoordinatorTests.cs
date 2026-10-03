using ClipboardTool.Domain.PasteChain;
using ClipboardTool.Domain.PanelModes;
using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Application;

namespace ClipboardTool.Tests.PanelModes;

/// <summary>
/// PanelCoordinator 四态状态机（legacy panel_modes.rs 测试移植）：
/// 热键差量、让位矩阵、IME 组合子态、互斥退出、焦点快照生命周期、捕获换键、呼出重置。
/// </summary>
public class PanelCoordinatorTests
{
    private static readonly HotkeyCombo Toggle = HotkeyPlan.SummonDefault;
    private static readonly HotkeyCombo Up = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeyUp);
    private static readonly HotkeyCombo Down = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeyDown);
    private static readonly HotkeyCombo Enter = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeyReturn);
    private static readonly HotkeyCombo Esc = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeyEscape);
    private static readonly HotkeyCombo Del = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeyDelete);
    private static readonly HotkeyCombo Z = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeyZ);
    private static readonly HotkeyCombo B = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeyB);
    private static readonly HotkeyCombo Space = new(HotkeyModifiers.None, PanelNavKeys.VirtualKeySpace);
    private static readonly FocusTarget Snapshot = new(1, 1, 1, 1);

    private readonly MockHost _host = new();
    private readonly FakeScheduler _scheduler = new();
    private PanelCoordinator CreateCoordinator() => new(_host, _scheduler);

    /// <summary>假宿主：自己那张已生效键表 + 事件流水 + 焦点快照桩。</summary>
    private sealed class MockHost : IPanelModesHost
    {
        public Dictionary<HotkeyCombo, PanelKeyAction> Registered = [];
        public List<(string Channel, string Action, string? NoteEntryId)> Events = [];
        public FocusTarget? FocusSnapshot;
        public int SnapshotRequests;
        public List<FocusTarget> Restored = [];
        public int NoFocusErrors;
        public int Focused;
        public int Blurred;
        public bool CanInteractValue = true;
        public Dictionary<uint, bool> KeyDown = [];

        public bool RegisterKey(HotkeyCombo combo, PanelKeyAction action)
        {
            if (Registered.ContainsKey(combo)) return false;
            Registered[combo] = action;
            return true;
        }

        public void UnregisterKey(HotkeyCombo combo) => Registered.Remove(combo);

        public IReadOnlyCollection<HotkeyCombo> CurrentKeys => Registered.Keys;

        public bool CanInteract() => CanInteractValue;

        public void FocusPanel() => Focused++;

        public void BlurPanelIfFocused() => Blurred++;

        public void SendPanelKey(string action, string? noteEntryId) => Events.Add(("panel:key", action, noteEntryId));

        public void SendPanelShown() => Events.Add(("panel:shown", "", null));

        public void SendCaptureEnd() => Events.Add(("shortcut:capture-end", "", null));

        public FocusTarget? CaptureFocus()
        {
            SnapshotRequests++;
            return FocusSnapshot;
        }

        public void RestoreFocus(FocusTarget target) => Restored.Add(target);

        public void ReportNoFocusTarget() => NoFocusErrors++;

        public bool ValidateNoteTarget(string? targetId) => targetId is null || targetId == "entry-1";

        public bool IsKeyDown(uint virtualKey) => KeyDown.GetValueOrDefault(virtualKey);
    }

    private int KeyEvents(MockHost host, string action) =>
        host.Events.Count(e => e.Channel == "panel:key" && e.Action == action);

    private PanelCoordinator SummonedCoordinator(MockHost host)
    {
        var modes = new PanelCoordinator(host, _scheduler);
        host.FocusSnapshot = Snapshot;
        modes.SetToggleShortcut(Toggle);
        modes.EnsureFocusSnapshot(); // 呼出时序第一步：先拍快照再显示（失败静默）
        modes.Show();
        return modes;
    }

    // —— 启动/呼出 ——

    [Fact]
    public void StartupRegistersToggleOnly_ShowRegistersAllNavKeys()
    {
        var modes = CreateCoordinator();
        modes.SetToggleShortcut(Toggle);
        Assert.Equal([Toggle], _host.Registered.Keys);

        modes.Show();
        Assert.Contains(Toggle, _host.Registered.Keys);
        Assert.Contains(Up, _host.Registered.Keys);
        Assert.Contains(Down, _host.Registered.Keys);
        Assert.Contains(Enter, _host.Registered.Keys);
        Assert.Contains(Esc, _host.Registered.Keys);
        Assert.Contains(Del, _host.Registered.Keys);
        Assert.Contains(Z, _host.Registered.Keys);
        Assert.Contains(B, _host.Registered.Keys);
        Assert.Contains(Space, _host.Registered.Keys);
        Assert.Equal(1, _host.Events.Count(e => e.Channel == "panel:shown"));
    }

    [Fact]
    public void SummonResetsSearchAndNoteState()
    {
        // 呼出重置（F14）：搜索/备注输入态不跨呼出保留
        var modes = SummonedCoordinator(_host);
        Assert.True(modes.EnterInput(PanelMode.Search));
        modes.ExitInput(PanelMode.Search, restoreFocus: false);
        Assert.True(modes.EnterInput(PanelMode.NoteEdit, "entry-1"));
        modes.Hide(restoreFocus: false);

        modes.Show();
        Assert.Equal(PanelMode.Browse, modes.Mode);
        Assert.True(modes.Visible);
        // 浏览态热键全量恢复（含让位的四键）
        Assert.Contains(Space, _host.Registered.Keys);
        Assert.Contains(B, _host.Registered.Keys);
    }

    // —— 搜索态让位与 IME ——

    [Fact]
    public void SearchModePartialYield_ComposingSuspendsAll()
    {
        var modes = SummonedCoordinator(_host);
        Assert.True(modes.EnterInput(PanelMode.Search));
        Assert.DoesNotContain(Space, _host.Registered.Keys);
        Assert.DoesNotContain(Z, _host.Registered.Keys);
        Assert.DoesNotContain(Del, _host.Registered.Keys);
        Assert.DoesNotContain(B, _host.Registered.Keys);
        Assert.Contains(Up, _host.Registered.Keys);
        Assert.Contains(Down, _host.Registered.Keys);
        Assert.Contains(Enter, _host.Registered.Keys);
        Assert.Contains(Esc, _host.Registered.Keys);

        modes.SetComposing(true);
        Assert.DoesNotContain(Up, _host.Registered.Keys);
        Assert.Contains(Toggle, _host.Registered.Keys);

        modes.SetComposing(false);
        Assert.Contains(Up, _host.Registered.Keys);
        // 同值幂等：不再触发差量（表不变即可视为无事发生）
        modes.SetComposing(false);
        Assert.Contains(Up, _host.Registered.Keys);
    }

    [Fact]
    public void SearchExit_RestoresBrowseKeys_RestoresFocus_SendsExitEvent_KeepsSnapshot()
    {
        var modes = SummonedCoordinator(_host);
        modes.EnterInput(PanelMode.Search);
        var snapshotRequestsBefore = _host.SnapshotRequests;

        modes.ExitInput(PanelMode.Search, restoreFocus: true);

        Assert.Equal(PanelMode.Browse, modes.Mode);
        Assert.Contains(Space, _host.Registered.Keys);
        Assert.Contains(Z, _host.Registered.Keys);
        Assert.Equal(1, KeyEvents(_host, "search-exit"));
        Assert.Equal([Snapshot], _host.Restored);
        // 快照保留：同一次呼出内再进搜索不补拍
        modes.EnterInput(PanelMode.Search);
        Assert.Equal(snapshotRequestsBefore, _host.SnapshotRequests);
    }

    [Fact]
    public void BrowseEscForwards_SearchEscExitsSearch()
    {
        var modes = SummonedCoordinator(_host);

        modes.OnNavPressed(NavAction.Escape);
        Assert.Equal(1, KeyEvents(_host, "escape")); // 浏览态 Esc 转发渲染层（停靠）

        modes.EnterInput(PanelMode.Search);
        modes.OnNavPressed(NavAction.Escape);
        Assert.Equal(1, KeyEvents(_host, "search-exit")); // 搜索态 Esc 退出搜索（先退搜索再停靠）
        Assert.Equal(PanelMode.Browse, modes.Mode);
    }

    [Fact]
    public void SearchEnterAndForwardedKeys_DigestedOrForwarded()
    {
        var modes = SummonedCoordinator(_host);

        // Space 进入搜索（状态机内消化，不再转发 "search"）
        modes.OnNavPressed(NavAction.Search);
        Assert.Equal(PanelMode.Search, modes.Mode);
        Assert.Equal(0, KeyEvents(_host, "search"));

        // Up 转发渲染层
        modes.OnNavPressed(NavAction.Up);
        Assert.Equal(1, KeyEvents(_host, "up"));

        // B 从搜索态进入备注编辑：互斥退出先发 search-exit，再发 note-edit-enter
        modes.OnNavPressed(NavAction.Note);
        Assert.Equal(PanelMode.NoteEdit, modes.Mode);
        Assert.Equal(1, KeyEvents(_host, "search-exit"));
        Assert.Equal(1, KeyEvents(_host, "note-edit-enter"));
        Assert.DoesNotContain(Enter, _host.Registered.Keys); // 备注编辑中导航键全部让位
    }

    // —— 备注编辑 ——

    [Fact]
    public void NoteTargetValidationFailure_NoEnter()
    {
        var modes = SummonedCoordinator(_host);

        Assert.False(modes.EnterInput(PanelMode.NoteEdit, "no-such-entry"));
        Assert.Equal(PanelMode.Browse, modes.Mode);
        Assert.Contains(Space, _host.Registered.Keys); // 浏览态热键原样
    }

    [Fact]
    public void Hide_ExitsInputLayered_WithCorrectEvents()
    {
        var modes = SummonedCoordinator(_host);
        modes.EnterInput(PanelMode.Search);
        modes.OnNavPressed(NavAction.Note); // search → note-edit

        modes.Hide(restoreFocus: true);

        Assert.Equal(PanelMode.Browse, modes.Mode);
        Assert.False(modes.Visible);
        Assert.Equal([Toggle], _host.Registered.Keys); // 导航键注销、呼出键保留
        Assert.Equal(1, KeyEvents(_host, "note-edit-exit"));
        // search-exit 只在进入备注编辑的互斥退出时发过一次，hide 不再重复
        Assert.Equal(1, KeyEvents(_host, "search-exit"));
    }

    // —— 焦点快照 ——

    [Fact]
    public void HideConsumesSnapshot_RestoreFocusFalseOnlyClears()
    {
        var modes = SummonedCoordinator(_host);

        modes.Hide(restoreFocus: true);
        Assert.Equal([Snapshot], _host.Restored);

        // 快照已消费 → 重新补拍
        _host.FocusSnapshot = Snapshot;
        modes.Show();
        modes.Hide(restoreFocus: false);
        Assert.Single(_host.Restored); // 没有新的归还
        Assert.Null(modes.FocusTargetSnapshot);
    }

    [Fact]
    public void SnapshotFailure_SilentOnSummon_ReportedAndAbortedOnEnterInput()
    {
        // 焦点快照为 None（captureFocus 返回 null）
        var modes = CreateCoordinator();
        modes.SetToggleShortcut(Toggle);
        modes.Show();
        Assert.Equal(0, _host.NoFocusErrors);

        Assert.False(modes.EnterInput(PanelMode.Search));
        Assert.Equal(1, _host.NoFocusErrors);
        Assert.Equal(PanelMode.Browse, modes.Mode);
        Assert.False(modes.EnterInput(PanelMode.NoteEdit, null));
        Assert.Equal(2, _host.NoFocusErrors);
    }

    // —— 快捷键捕获 ——

    [Fact]
    public void Capture_UnregistersAll_ConfirmSwitchesAndExits()
    {
        var modes = SummonedCoordinator(_host);

        Assert.True(modes.EnterInput(PanelMode.ShortcutCapture));
        Assert.Empty(_host.Registered); // 捕获中无任何全局键（连呼出键也让位）

        var newToggle = new HotkeyCombo(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58); // Ctrl+Alt+X
        Assert.True(modes.TrySetToggleShortcut(newToggle));
        Assert.Contains(newToggle, _host.Registered.Keys);
        Assert.Contains(Up, _host.Registered.Keys); // 导航键恢复
        Assert.Equal(PanelMode.Browse, modes.Mode);
        // 确认路径不发 capture-end（覆盖层由渲染层自行收起）
        Assert.DoesNotContain(_host.Events, e => e.Channel == "shortcut:capture-end");
    }

    [Fact]
    public void Capture_NewKeyRejectedStaysInCapture_CancelRestoresAndSendsCaptureEnd()
    {
        var modes = SummonedCoordinator(_host);
        modes.EnterInput(PanelMode.ShortcutCapture);

        var occupied = new HotkeyCombo(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58);
        _host.Registered[occupied] = PanelKeyAction.Toggle; // 模拟被占用
        Assert.False(modes.TrySetToggleShortcut(occupied));
        Assert.Equal(PanelMode.ShortcutCapture, modes.Mode);

        modes.ExitInput(PanelMode.ShortcutCapture, restoreFocus: true);
        Assert.Contains(Toggle, _host.Registered.Keys); // 原键恢复
        Assert.Contains(Up, _host.Registered.Keys);
        Assert.Contains(_host.Events, e => e.Channel == "shortcut:capture-end"); // 取消才发
    }

    [Fact]
    public void ShowDuringCapture_StaysCapture_NoNavKeysRegistered()
    {
        var modes = CreateCoordinator();
        _host.FocusSnapshot = Snapshot;
        modes.SetToggleShortcut(Toggle);
        Assert.True(modes.EnterInput(PanelMode.ShortcutCapture));
        modes.Show(); // 捕获随后的呼出面板

        Assert.Empty(_host.Registered); // 捕获中 show 不注册任何键
        Assert.Equal(PanelMode.ShortcutCapture, modes.Mode);

        modes.ExitInput(PanelMode.ShortcutCapture, restoreFocus: false);
        Assert.Equal(PanelMode.Browse, modes.Mode);
        Assert.Contains(Up, _host.Registered.Keys); // 面板仍显示 → 导航键恢复
    }

    [Fact]
    public void CaptureToSearch_SendsCaptureEndThenSearchEnter()
    {
        // 从捕获态直接进搜索：先发 capture-end 再发进入事件（否则覆盖层留在屏幕上）
        var modes = SummonedCoordinator(_host);
        Assert.True(modes.EnterInput(PanelMode.ShortcutCapture));
        Assert.True(modes.EnterInput(PanelMode.Search));

        var seq = _host.Events
            .Where(e => e.Channel == "shortcut:capture-end" || e.Action == "search-enter")
            .Select(e => e.Channel == "panel:key" ? e.Action : e.Channel)
            .ToList();
        Assert.Equal(["shortcut:capture-end", "search-enter"], seq);
    }

    // —— 互斥与幂等 ——

    [Fact]
    public void ExitInputOnlyExitsSpecifiedMode_EnterSameModeRejected()
    {
        var modes = SummonedCoordinator(_host);
        Assert.True(modes.EnterInput(PanelMode.Search));
        var eventsBefore = _host.Events.Count;
        var blurredBefore = _host.Blurred;

        // 已在搜索态：再进一次幂等拒绝
        Assert.False(modes.EnterInput(PanelMode.Search));
        Assert.Equal(eventsBefore, _host.Events.Count);

        // 备注态没开：exit_input(NoteEdit) 整段跳过，搜索态、事件、焦点都不动
        modes.ExitInput(PanelMode.NoteEdit, restoreFocus: true);
        Assert.Equal(PanelMode.Search, modes.Mode);
        Assert.Equal(eventsBefore, _host.Events.Count);
        Assert.Equal(blurredBefore, _host.Blurred);
        Assert.Empty(_host.Restored);
    }

    [Fact]
    public void ToggleShortcutUpdate_ReplacesOldKey()
    {
        var modes = SummonedCoordinator(_host);
        var newToggle = new HotkeyCombo(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58);

        modes.SetToggleShortcut(newToggle);

        Assert.DoesNotContain(Toggle, _host.Registered.Keys);
        Assert.Contains(newToggle, _host.Registered.Keys);
    }

    // —— 长按重复（F19） ——

    [Fact]
    public void Repeat_ArmedForUpDownOnly_WithInitialDelayThenInterval()
    {
        var modes = SummonedCoordinator(_host);
        _host.KeyDown[PanelNavKeys.VirtualKeyUp] = true;

        modes.OnNavPressed(NavAction.Up); // 真实按下：分发一次 + 武装连发

        var armed = Assert.Single(_scheduler.Scheduled);
        Assert.Equal(PanelNavKeys.RepeatInitialDelayMs, armed.DelayMs);

        // Enter 不武装
        _scheduler.Scheduled.Clear();
        modes.OnNavPressed(NavAction.Enter);
        Assert.Empty(_scheduler.Scheduled);
    }

    [Fact]
    public void Repeat_KeyHeld_DispatchesAtInterval()
    {
        var modes = SummonedCoordinator(_host);
        _host.KeyDown[PanelNavKeys.VirtualKeyUp] = true;
        modes.OnNavPressed(NavAction.Up);
        var countBefore = KeyEvents(_host, "up");

        _scheduler.FireAll(); // 300ms 到点：键仍按住 → 分发 + 安排 50ms
        Assert.Equal(countBefore + 1, KeyEvents(_host, "up"));
        var chained = Assert.Single(_scheduler.Scheduled);
        Assert.Equal(PanelNavKeys.RepeatIntervalMs, chained.DelayMs);

        _scheduler.FireAll(); // 50ms 到点：继续
        Assert.Equal(countBefore + 2, KeyEvents(_host, "up"));
    }

    [Fact]
    public void Repeat_KeyReleased_Stops()
    {
        var modes = SummonedCoordinator(_host);
        _host.KeyDown[PanelNavKeys.VirtualKeyUp] = true;
        modes.OnNavPressed(NavAction.Up);

        _host.KeyDown[PanelNavKeys.VirtualKeyUp] = false;
        _scheduler.FireAll();

        Assert.Empty(_scheduler.Scheduled); // 松键即停（无 keyup 事件，轮询物理键态）
    }

    [Fact]
    public void Repeat_PanelHidden_Stops()
    {
        var modes = SummonedCoordinator(_host);
        _host.KeyDown[PanelNavKeys.VirtualKeyDown] = true;
        modes.OnNavPressed(NavAction.Down);

        modes.Hide(restoreFocus: false);
        _scheduler.FireAll();

        Assert.Empty(_scheduler.Scheduled); // 面板收起就停手，不等松键
    }

    [Fact]
    public void Repeat_KeyUnregisteredMidHold_Stops()
    {
        // 长按 Up 途中退到搜索→备注（Up 被注销）：连发自动停手（legacy 表查询守卫）
        var modes = SummonedCoordinator(_host);
        _host.KeyDown[PanelNavKeys.VirtualKeyUp] = true;
        modes.OnNavPressed(NavAction.Up);

        modes.OnNavPressed(NavAction.Note); // 进入备注编辑，Up 注销
        _scheduler.FireAll();

        Assert.Empty(_scheduler.Scheduled);
    }

    [Fact]
    public void Repeat_SecondPressWhileActive_NotRearmed()
    {
        var modes = SummonedCoordinator(_host);
        _host.KeyDown[PanelNavKeys.VirtualKeyUp] = true;

        modes.OnNavPressed(NavAction.Up);
        modes.OnNavPressed(NavAction.Up);

        Assert.Single(_scheduler.Scheduled); // 已有活的连发在跑，不再换新
    }
}

/// <summary>
/// 假调度器：记录安排的延迟回调，测试手动触发。
/// FireAll 只触发未取消的回调（正常流）；Fire(i) 无视取消强行触发（模拟「取消前已在执行」的竞态）。
/// </summary>
internal sealed class FakeScheduler : IDelayScheduler
{
    public sealed class Token(Action callback) : IDisposable
    {
        public bool Cancelled;

        public void Dispose() => Cancelled = true;

        public void Fire()
        {
            if (!Cancelled)
            {
                callback();
            }
        }
    }

    public sealed record Schedule(int DelayMs, Token Token);

    public List<Schedule> Scheduled { get; } = [];

    public IDisposable Delay(int milliseconds, Action callback)
    {
        var token = new Token(callback);
        Scheduled.Add(new Schedule(milliseconds, token));
        return token;
    }

    /// <summary>按记录顺序触发所有未取消的回调并移除已消耗的记录（链式安排的新回调留在表里）。</summary>
    public void FireAll()
    {
        var pending = Scheduled.Where(s => !s.Token.Cancelled).ToList();
        foreach (var schedule in pending)
        {
            Scheduled.Remove(schedule);
        }
        foreach (var schedule in pending)
        {
            schedule.Token.Fire();
        }
    }

    /// <summary>无视取消强行触发第 i 个回调（模拟取消落地前已在执行的竞态）。</summary>
    public void ForceFire(int index) => Scheduled[index].Token.Fire();
}
