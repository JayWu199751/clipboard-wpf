using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Domain.PanelModes;

/// <summary>面板四态（F20）：浏览、搜索、备注编辑、快捷键捕获。</summary>
public enum PanelMode
{
    Browse,
    Search,
    NoteEdit,
    ShortcutCapture,
}

/// <summary>面板导航动作（F18；legacy NavAction）。协议动作名经 ActionName 发给渲染层。</summary>
public enum NavAction
{
    Up,
    Down,
    Enter,
    Escape,
    Delete,
    Pin,
    Note,
    Search,
}

/// <summary>动作的渲染层协议名（panel:key 的 action 字段；legacy NavAction::as_str）。</summary>
public static class PanelModesExtensions
{
    public static string ActionName(this NavAction action) => action switch
    {
        NavAction.Up => "up",
        NavAction.Down => "down",
        NavAction.Enter => "enter",
        NavAction.Escape => "escape",
        NavAction.Delete => "delete",
        NavAction.Pin => "pin",
        NavAction.Note => "note",
        NavAction.Search => "search",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}

/// <summary>全局键动作：呼出（Toggle）或面板导航（Nav）。</summary>
public readonly record struct PanelKeyAction(PanelKeyKind Kind, NavAction NavAction)
{
    public static readonly PanelKeyAction Toggle = new(PanelKeyKind.Toggle, default);

    public static PanelKeyAction Nav(NavAction nav) => new(PanelKeyKind.Nav, nav);
}

public enum PanelKeyKind
{
    Toggle,
    Nav,
}

/// <summary>一条目标键位：组合键 + 动作（差量注册时按动作分发）。</summary>
public sealed record PanelKeyBinding(HotkeyCombo Combo, PanelKeyAction Action);

/// <summary>
/// 面板导航键位表（legacy NAV_SHORTCUTS 的移植）：[组合键, 动作, 是否在搜索模式下继续拦截]。
/// 搜索态里 Space/Z/Del/B 让位给搜索输入框，↑↓/Enter/Esc 保持面板语义（F20）。
/// 全部为无修饰裸键：全局注册后输入框只收到普通字符，导航键经 WM_HOTKEY 回来重放语义。
/// </summary>
public static class PanelNavKeys
{
    public const uint VirtualKeyUp = 0x26;
    public const uint VirtualKeyDown = 0x28;
    public const uint VirtualKeyReturn = 0x0D;
    public const uint VirtualKeyEscape = 0x1B;
    public const uint VirtualKeyDelete = 0x2E;
    public const uint VirtualKeyZ = 0x5A;
    public const uint VirtualKeyB = 0x42;
    public const uint VirtualKeySpace = 0x20;

    private static readonly NavShortcut[] ShortcutsTable =
    [
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeyUp), NavAction.Up, EnabledInSearch: true),
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeyDown), NavAction.Down, EnabledInSearch: true),
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeyReturn), NavAction.Enter, EnabledInSearch: true),
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeyEscape), NavAction.Escape, EnabledInSearch: true),
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeyDelete), NavAction.Delete, EnabledInSearch: false),
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeyZ), NavAction.Pin, EnabledInSearch: false),
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeyB), NavAction.Note, EnabledInSearch: false),
        new(new HotkeyCombo(HotkeyModifiers.None, VirtualKeySpace), NavAction.Search, EnabledInSearch: false),
    ];

    public static IReadOnlyList<NavShortcut> Shortcuts => ShortcutsTable;

    /// <summary>Windows 全局热键没有系统自动重复：只有上下方向键由应用自行重复投递。</summary>
    public static bool IsRepeatableNavigation(uint virtualKey) =>
        virtualKey is VirtualKeyUp or VirtualKeyDown;

    /// <summary>长按重复节奏（F19；legacy modes.rs NAV_REPEAT_*）。</summary>
    public const int RepeatInitialDelayMs = 300;
    public const int RepeatIntervalMs = 50;
}

/// <summary>一条导航键位：组合键 + 动作 + 搜索态让位旗标。</summary>
public readonly record struct NavShortcut(HotkeyCombo Combo, NavAction Action, bool EnabledInSearch);

/// <summary>目标键集合推导（F20 完整矩阵）：按「面板可见性 + 当前模式 + IME 子态」给出应生效的全局键。</summary>
public static class PanelKeySets
{
    /// <summary>
    /// 目标键集合。呼出键只在非捕获态生效；导航键：浏览态全量、搜索态仅留 EnabledInSearch 四键、
    /// IME 组合期间与备注编辑全部让位、捕获态连呼出键也让位（否则录不到新键）。
    /// </summary>
    public static IReadOnlyList<PanelKeyBinding> DesiredKeys(
        bool visible, PanelMode mode, bool composing, HotkeyCombo? toggle)
    {
        var desired = new List<PanelKeyBinding>();

        if (mode != PanelMode.ShortcutCapture && toggle is { } summon)
        {
            desired.Add(new PanelKeyBinding(summon, PanelKeyAction.Toggle));
        }

        if (!visible)
        {
            return desired;
        }

        switch (mode)
        {
            case PanelMode.Browse:
                desired.AddRange(PanelNavKeys.Shortcuts.Select(
                    s => new PanelKeyBinding(s.Combo, PanelKeyAction.Nav(s.Action))));
                break;
            case PanelMode.Search when !composing:
                // IME 组合期间所有导航键暂停，交给输入法（F21）
                desired.AddRange(PanelNavKeys.Shortcuts.Where(s => s.EnabledInSearch).Select(
                    s => new PanelKeyBinding(s.Combo, PanelKeyAction.Nav(s.Action))));
                break;
            // Search+composing / NoteEdit / ShortcutCapture：导航键全部让位
        }

        return desired;
    }
}

/// <summary>输入态的纯判定（legacy Mode::needs_focus / requires_visible_panel）。</summary>
public static class PanelModeTraits
{
    /// <summary>进入该态是否要把面板聚焦起来（捕获态不要：它此刻只负责注销全局键）。</summary>
    public static bool NeedsFocus(PanelMode mode) => mode is PanelMode.Search or PanelMode.NoteEdit;

    /// <summary>进入该态是否要求面板已经显示（捕获态可在面板收起时进入，随后才呼出）。</summary>
    public static bool RequiresVisiblePanel(PanelMode mode) => mode != PanelMode.ShortcutCapture;
}

/// <summary>状态机发给渲染层的事件（legacy RendererEvent 的两条通道收口）。</summary>
public abstract record PanelRendererEvent
{
    /// <summary>panel:key 通道：动作名 + 可选备注条目 id。</summary>
    public sealed record PanelKey(string Action, string? NoteEntryId) : PanelRendererEvent;

    /// <summary>shortcut:capture-end 通道：捕获覆盖层收起。</summary>
    public sealed record CaptureEnd : PanelRendererEvent;
}

/// <summary>「哪个态进/出各发哪条事件」的纯判定（legacy Mode::enter_event / exit_event）。</summary>
public static class PanelModeEvents
{
    /// <summary>进入该输入态要发的事件。捕获态没有进入事件：覆盖层由宿主在呼出面板之后编排。</summary>
    public static PanelRendererEvent? EnterEvent(PanelMode mode, string? noteEntryId) => mode switch
    {
        PanelMode.Search => new PanelRendererEvent.PanelKey("search-enter", null),
        PanelMode.NoteEdit => new PanelRendererEvent.PanelKey("note-edit-enter", noteEntryId),
        _ => null,
    };

    /// <summary>退出该输入态要发的事件。浏览态不是输入态，没有退出事件。</summary>
    public static PanelRendererEvent? ExitEvent(PanelMode mode) => mode switch
    {
        PanelMode.Search => new PanelRendererEvent.PanelKey("search-exit", null),
        PanelMode.NoteEdit => new PanelRendererEvent.PanelKey("note-edit-exit", null),
        PanelMode.ShortcutCapture => new PanelRendererEvent.CaptureEnd(),
        _ => null,
    };
}
