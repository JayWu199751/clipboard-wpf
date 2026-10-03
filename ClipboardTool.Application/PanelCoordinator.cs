using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Domain.PasteChain;
using ClipboardTool.Domain.PanelModes;

namespace ClipboardTool.Application;

/// <summary>
/// 面板模式状态机的效果宿主端口（legacy ModesHost 同构）。实现方只做效果、不得回锁状态机：
/// 本协调器把状态机调用与效果调用放在同一线程，实现内部再投递到别的线程是允许的。
/// </summary>
public interface IPanelModesHost
{
    // —— 全局热键 seam：本协调器只发差量指令，已生效键集合由宿主（执行注册者，ADR-0006/0010）记账。
    //    register_key 返回是否真的注册上了；current_keys 是差量的另一侧。
    bool RegisterKey(HotkeyCombo combo, PanelKeyAction action);

    void UnregisterKey(HotkeyCombo combo);

    IReadOnlyCollection<HotkeyCombo> CurrentKeys { get; }

    // —— 面板窗口效果
    bool CanInteract();

    /// <summary>输入态聚焦面板（ADR-0002：清 NOACTIVATE + 激活/级联）。</summary>
    void FocusPanel();

    /// <summary>退出输入态时释放面板键盘焦点（不动前台；前台归还原程序由 RestoreFocus 负责）。</summary>
    void BlurPanelIfFocused();

    // —— 渲染层通知
    void SendPanelKey(string action, string? noteEntryId);

    void SendPanelShown();

    void SendCaptureEnd();

    // —— 焦点快照通道（呼出时由宿主捕获；同一次呼出内复用，隐藏时消费）
    FocusTarget? CaptureFocus();

    void RestoreFocus(FocusTarget target);

    void ReportNoFocusTarget();

    // —— 领域查询（备注编辑目标校验）
    bool ValidateNoteTarget(string? targetId);

    // —— 长按重复期间轮询物理键态（全局热键无松键事件）
    bool IsKeyDown(uint virtualKey);
}

/// <summary>
/// 面板四态协调器（F18–F21、F14 呼出重置；legacy panel_modes.rs PanelModes 的移植）：
/// 浏览/搜索/备注编辑/捕获的模式状态机。全局快捷键集合由「当前模式」唯一推导
/// （PanelKeySets.DesiredKeys），对宿主已生效集合做差量同步；模式转换不各自手写
/// register/unregister。状态与全部效果调用线程封闭在宿主消息线程（WPF UI 线程）——
/// WM_HOTKEY、托盘回调、呼出/停靠都在该线程到达，「模式单一串行写入者」由线程封闭保证；
/// 慢效果的粘贴链路仍走 ModeExecutor 专用线程（ADR-0004，不在本类）。
/// </summary>
public sealed class PanelCoordinator
{
    private readonly IPanelModesHost _host;
    private readonly IDelayScheduler? _scheduler;

    private bool _visible;
    private PanelMode _mode = PanelMode.Browse;
    private bool _composing; // 搜索模式子态：中文输入法组合中
    private string? _noteEntryId;
    private FocusTarget? _focusTarget; // 本次呼出期间的前台焦点快照（退出输入态复用，隐藏时消费）
    private HotkeyCombo? _toggle;      // 呼出快捷键（捕获期间临时注销，值不变）
    private readonly Dictionary<NavAction, IDisposable> _repeatTokens = []; // 活着的连发链（每键一条）

    public PanelCoordinator(IPanelModesHost host, IDelayScheduler? scheduler = null)
    {
        _host = host;
        _scheduler = scheduler;
    }

    public bool Visible => _visible;

    public PanelMode Mode => _mode;

    /// <summary>输入态（搜索/备注/捕获）豁免「浏览态自动失焦」。</summary>
    public bool InputActive => _visible && _mode != PanelMode.Browse;

    /// <summary>当前焦点快照（只读，不消费）。粘贴链路用它恢复原输入框；隐藏面板时才被消费清空。</summary>
    public FocusTarget? FocusTargetSnapshot => _focusTarget;

    // —— 差量同步：只动需要动的键。先注销后注册（腾出系统侧的槽位，换键时同一个组合键才注册得上）；
    //    注册侧不判返回值，「有没有登记上」由宿主的表说了算——失败就是没进表，下一次差量自然重试。
    private void ApplyHotkeys()
    {
        var desired = PanelKeySets.DesiredKeys(_visible, _mode, _composing, _toggle);
        var current = _host.CurrentKeys;
        var desiredCombos = new HashSet<HotkeyCombo>(desired.Select(b => b.Combo));

        foreach (var stale in current.Where(combo => !desiredCombos.Contains(combo)).ToList())
        {
            _host.UnregisterKey(stale);
        }
        foreach (var binding in desired)
        {
            if (!current.Contains(binding.Combo))
            {
                _ = _host.RegisterKey(binding.Combo, binding.Action);
            }
        }
    }

    // —— 焦点快照：呼出期间复用同一份。reportOnFailure=false 用于呼出面板（失败静默，面板照常显示）。
    private bool EnsureFocusTarget(bool reportOnFailure)
    {
        if (_focusTarget is not null)
        {
            return true;
        }
        var target = _host.CaptureFocus();
        if (target is null)
        {
            if (reportOnFailure)
            {
                _host.ReportNoFocusTarget();
            }
            return false;
        }
        _focusTarget = target;
        return true;
    }

    private void RestoreFocusKeepingSnapshot()
    {
        if (_focusTarget is { } target)
        {
            _host.RestoreFocus(target);
        }
    }

    private void Announce(PanelRendererEvent? @event)
    {
        switch (@event)
        {
            case PanelRendererEvent.PanelKey key:
                _host.SendPanelKey(key.Action, key.NoteEntryId);
                break;
            case PanelRendererEvent.CaptureEnd:
                _host.SendCaptureEnd();
                break;
        }
    }

    /// <summary>启动/更换呼出快捷键。</summary>
    public void SetToggleShortcut(HotkeyCombo combo)
    {
        _toggle = combo;
        ApplyHotkeys();
    }

    /// <summary>
    /// 呼出时序的第一步（legacy show_on capture=true）：先记录前台窗口与焦点控件，再显示面板。
    /// 失败静默（面板照常显示）；快照在本次呼出内复用，进入输入态时不重复补拍。
    /// </summary>
    public bool EnsureFocusSnapshot() => EnsureFocusTarget(reportOnFailure: false);

    /// <summary>
    /// 呼出面板：重置搜索/备注态（F14 呼出重置）；捕获进行中则保持捕获
    /// （热键集合由 ApplyHotkeys 推导，不会误注册导航键）。
    /// </summary>
    public void Show()
    {
        _visible = true;
        if (_mode != PanelMode.ShortcutCapture)
        {
            _mode = PanelMode.Browse;
            _composing = false;
            _noteEntryId = null;
        }
        ApplyHotkeys();
        _host.SendPanelShown();
    }

    /// <summary>
    /// 隐藏面板：退出当前输入态（发对退出事件）、注销导航键（呼出键保留）、消费焦点快照。
    /// restoreFocus=true 时由宿主归还焦点。
    /// </summary>
    public void Hide(bool restoreFocus)
    {
        var previous = _mode;
        _visible = false;
        _mode = PanelMode.Browse;
        _composing = false;
        _noteEntryId = null;
        Announce(PanelModeEvents.ExitEvent(previous));
        ApplyHotkeys();
        _host.BlurPanelIfFocused();
        var target = _focusTarget;
        _focusTarget = null;
        if (restoreFocus && target is { } consumed)
        {
            _host.RestoreFocus(consumed);
        }
    }

    /// <summary>
    /// 全局热键导航动作的统一入口：search/escape(搜索态)/note 三个动作在状态机内消化，其余转发渲染层。
    /// </summary>
    public void OnNavAction(NavAction action)
    {
        switch (action)
        {
            case NavAction.Search:
                _ = EnterInput(PanelMode.Search);
                break;
            case NavAction.Escape when _mode == PanelMode.Search:
                ExitInput(PanelMode.Search, restoreFocus: true);
                break;
            case NavAction.Note:
                _ = EnterInput(PanelMode.NoteEdit);
                break;
            default:
                _host.SendPanelKey(action.ActionName(), null);
                break;
        }
    }

    /// <summary>真实按键路径：分发一次 + 武装长按连发（连发自投递的路径走 OnNavAction，不再武装）。</summary>
    public void OnNavPressed(NavAction action)
    {
        OnNavAction(action);
        ArmRepeat(action);
    }

    /// <summary>
    /// 中文输入法组合开始/结束：组合期间暂停全部导航键（F21）。
    /// 同值幂等，不触发差量。
    /// </summary>
    public void SetComposing(bool value)
    {
        if (_composing == value)
        {
            return;
        }
        _composing = value;
        if (_visible && _mode == PanelMode.Search)
        {
            ApplyHotkeys();
        }
    }

    /// <summary>
    /// 进入一个输入态。五步固定顺序，三个输入态共用：
    /// 1) 退出当前输入态（互斥退出发对退出事件，但不归还程序焦点——紧接着就要把面板聚焦起来）；
    /// 2) 确保焦点快照：缺失先补拍，拍不到就上报并放弃；
    /// 3) 该态独有的准入（备注编辑要校验目标条目）；
    /// 4) 改 mode 并清子态（composing 只属于搜索、noteEntryId 只属于备注编辑）；
    /// 5) 热键差量 → 需要焦点的态聚焦面板 → 发进入事件。
    /// </summary>
    public bool EnterInput(PanelMode target, string? noteTargetId = null)
    {
        if (!_host.CanInteract() || _mode == target)
        {
            return false;
        }
        if (PanelModeTraits.RequiresVisiblePanel(target) && !_visible)
        {
            return false;
        }
        ExitInputInternal(announceExit: true);
        if (!EnsureFocusTarget(reportOnFailure: true))
        {
            return false;
        }
        if (target == PanelMode.NoteEdit && !_host.ValidateNoteTarget(noteTargetId))
        {
            return false;
        }
        _mode = target;
        _composing = false;
        _noteEntryId = target == PanelMode.NoteEdit ? noteTargetId : null;
        ApplyHotkeys();
        if (PanelModeTraits.NeedsFocus(target))
        {
            _host.FocusPanel();
        }
        Announce(PanelModeEvents.EnterEvent(target, _noteEntryId));
        return true;
    }

    /// <summary>退出一个输入态（只有当前正处于该态才退）：五步同 ExitInputInternal，外加按需归还焦点
    /// （快照保留，同一次呼出内还能再进搜索/备注）。</summary>
    public void ExitInput(PanelMode from, bool restoreFocus)
    {
        if (_mode != from)
        {
            return;
        }
        ExitInputInternal(announceExit: true);
        if (restoreFocus)
        {
            RestoreFocusKeepingSnapshot();
        }
    }

    /// <summary>退出当前输入态回到浏览态：改 mode → 清子态 → 热键差量 → 交还面板焦点 → 发退出事件。
    /// 已是浏览态则整段跳过并返回 false（热键与焦点都还没同步过）。</summary>
    private bool ExitInputInternal(bool announceExit)
    {
        var previous = _mode;
        if (previous == PanelMode.Browse)
        {
            return false;
        }
        _mode = PanelMode.Browse;
        _composing = false;
        _noteEntryId = null;
        ApplyHotkeys();
        _host.BlurPanelIfFocused();
        if (announceExit)
        {
            Announce(PanelModeEvents.ExitEvent(previous));
        }
        return true;
    }

    /// <summary>
    /// 捕获确认：新呼出键注册成功才算成功；成功则退出捕获态，但不发 capture-end
    /// （覆盖层由渲染层自行收起）。
    /// </summary>
    public bool TrySetToggleShortcut(HotkeyCombo combo)
    {
        if (_mode != PanelMode.ShortcutCapture)
        {
            return false;
        }
        if (!_host.RegisterKey(combo, PanelKeyAction.Toggle))
        {
            return false;
        }
        _toggle = combo;
        _ = ExitInputInternal(announceExit: false);
        return true;
    }

    /// <summary>把焦点还回原程序（快照保留到 HidePanel 时消费）。</summary>
    public void RestoreOriginalFocus() => RestoreFocusKeepingSnapshot();

    // —— 长按重复（F19）：全局热键没有系统自动重复，只有上下方向键由应用自行投递。
    //    300ms 初始延迟、50ms 间隔；到点检查「面板收起 / 松键 / 键已被注销（模式切换）」任一即停手。
    private void ArmRepeat(NavAction action)
    {
        if (_scheduler is null || !PanelNavKeys.IsRepeatableNavigation(VirtualKeyOf(action)))
        {
            return;
        }
        if (_repeatTokens.ContainsKey(action))
        {
            return; // 已有活的连发在跑：不换新链
        }
        ChainRepeat(action, PanelNavKeys.RepeatInitialDelayMs);
    }

    private void ChainRepeat(NavAction action, int delayMs)
    {
        var virtualKey = VirtualKeyOf(action);
        var combo = ComboOf(action);
        _repeatTokens[action] = _scheduler!.Delay(delayMs, () =>
        {
            _ = _repeatTokens.Remove(action);
            if (!_visible || !_host.IsKeyDown(virtualKey) || !_host.CurrentKeys.Contains(combo))
            {
                return; // 面板收起（连发中途粘贴成功也会收）或松键就停手，不等下次
            }
            OnNavAction(action);
            ChainRepeat(action, PanelNavKeys.RepeatIntervalMs);
        });
    }

    private static uint VirtualKeyOf(NavAction action) => action switch
    {
        NavAction.Up => PanelNavKeys.VirtualKeyUp,
        NavAction.Down => PanelNavKeys.VirtualKeyDown,
        _ => 0,
    };

    private static HotkeyCombo ComboOf(NavAction action) => new(HotkeyModifiers.None, VirtualKeyOf(action));
}
