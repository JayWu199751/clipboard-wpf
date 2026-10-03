using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Domain.PanelModes;

namespace ClipboardTool.Tests.PanelModes;

/// <summary>
/// 四态 × 键位目标集合全矩阵 + 差量推导 + 可重复键判定（F18–F20/F21；legacy panel_modes.rs 移植）。
/// 键位注册表硬约束「提示 = 行为」：NAV 表、让位矩阵、连发常量在此钉死。
/// </summary>
public class PanelKeySetsTests
{
    private static readonly HotkeyCombo Toggle = HotkeyPlan.SummonDefault;

    private static HashSet<HotkeyCombo> Desired(bool visible, PanelMode mode, bool composing = false) =>
        Desired(visible, mode, composing, HotkeyPlan.SummonDefault);

    private static HashSet<HotkeyCombo> Desired(bool visible, PanelMode mode, bool composing, HotkeyCombo? toggle) =>
        [.. PanelKeySets.DesiredKeys(visible, mode, composing, toggle).Select(b => b.Combo)];

    // —— NAV 表（legacy NAV_SHORTCUTS 镜像：8 键、顺序、搜索态让位旗标） ——

    [Fact]
    public void NavShortcuts_TableMatchesLegacyEightKeys()
    {
        var table = PanelNavKeys.Shortcuts;

        Assert.Equal(8, table.Count);
        // 顺序与 legacy NAV_SHORTCUTS 一致：Up/Down/Enter/Esc 搜索态保留，Delete/Z/B/Space 让位
        Assert.Equal(NavAction.Up, table[0].Action);
        Assert.Equal(NavAction.Down, table[1].Action);
        Assert.Equal(NavAction.Enter, table[2].Action);
        Assert.Equal(NavAction.Escape, table[3].Action);
        Assert.Equal(NavAction.Delete, table[4].Action);
        Assert.Equal(NavAction.Pin, table[5].Action);
        Assert.Equal(NavAction.Note, table[6].Action);
        Assert.Equal(NavAction.Search, table[7].Action);
        // EnabledInSearch：前四真、后四假
        for (var i = 0; i < table.Count; i++)
        {
            Assert.Equal(i < 4, table[i].EnabledInSearch);
        }
        // 全部为无修饰裸键（全局注册；Up/Down 单键才可用 GetAsyncKeyState 判按住）
        Assert.All(table, s => Assert.Equal(HotkeyModifiers.None, s.Combo.Modifiers));
    }

    [Fact]
    public void NavKeys_VirtualKeyCodesPinned()
    {
        Assert.Equal(0x26u, PanelNavKeys.VirtualKeyUp);
        Assert.Equal(0x28u, PanelNavKeys.VirtualKeyDown);
        Assert.Equal(0x0Du, PanelNavKeys.VirtualKeyReturn);
        Assert.Equal(0x1Bu, PanelNavKeys.VirtualKeyEscape);
        Assert.Equal(0x2Eu, PanelNavKeys.VirtualKeyDelete);
        Assert.Equal(0x20u, PanelNavKeys.VirtualKeySpace);
        Assert.Equal(0x5Au, PanelNavKeys.VirtualKeyZ);
        Assert.Equal(0x42u, PanelNavKeys.VirtualKeyB);
        // 与 HotkeyPlan 既有常量同源不漂移（跨表防漂移）
        Assert.Equal(HotkeyPlan.VirtualKeyEscape, PanelNavKeys.VirtualKeyEscape);
        Assert.Equal(HotkeyPlan.VirtualKeyReturn, PanelNavKeys.VirtualKeyReturn);
        Assert.Equal(HotkeyPlan.BrowseDock, PanelNavKeys.Shortcuts[3].Combo);
        Assert.Equal(HotkeyPlan.BrowseEnter, PanelNavKeys.Shortcuts[2].Combo);
    }

    // —— 四态 × 目标键集合全矩阵 ——

    [Fact]
    public void DesiredKeys_Docked_ReturnsToggleOnly()
    {
        // 面板停靠：只有呼出键（浏览态导航键全部让位注销）
        var keys = Desired(visible: false, PanelMode.Browse);

        Assert.Equal([Toggle], keys);
    }

    [Fact]
    public void DesiredKeys_BrowseVisible_ReturnsTogglePlusAllNav()
    {
        var keys = Desired(visible: true, PanelMode.Browse);

        Assert.Equal(9, keys.Count);
        Assert.Contains(Toggle, keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyUp), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyDown), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyReturn), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyEscape), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyDelete), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyZ), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyB), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeySpace), keys);
    }

    [Fact]
    public void DesiredKeys_SearchVisible_NavigationKeysYieldToInput()
    {
        // 搜索态：Space/Z/Del/B 让位给输入框；↑↓/Enter/Esc 保持面板语义（F20）
        var keys = Desired(visible: true, PanelMode.Search, composing: false);

        Assert.Equal(5, keys.Count);
        Assert.Contains(Toggle, keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyUp), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyDown), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyReturn), keys);
        Assert.Contains(new HotkeyCombo(HotkeyModifiers.None, PanelNavKeys.VirtualKeyEscape), keys);
    }

    [Fact]
    public void DesiredKeys_SearchComposing_AllNavigationKeysSuspend()
    {
        // IME 组合期间：全部导航键暂停交给输入法，只留呼出键（F21）
        var keys = Desired(visible: true, PanelMode.Search, composing: true);

        Assert.Equal([Toggle], keys);
    }

    [Fact]
    public void DesiredKeys_NoteEdit_ReturnsToggleOnly()
    {
        // 备注编辑：导航键全部让位（Enter/Esc 归编辑器）
        Assert.Equal([Toggle], Desired(visible: true, PanelMode.NoteEdit));
    }

    [Fact]
    public void DesiredKeys_Capture_EvenToggleYields()
    {
        // 捕获态：连呼出键也让位（否则录不到新键）
        Assert.Empty(Desired(visible: true, PanelMode.ShortcutCapture));
        Assert.Empty(Desired(visible: false, PanelMode.ShortcutCapture));
    }

    [Fact]
    public void DesiredKeys_NoToggleConfigured_OmitsToggle()
    {
        // 呼出键未配置（启动早期）：只推导面板导航键，不捏造空组合
        var keys = PanelKeySets.DesiredKeys(visible: false, PanelMode.Browse, composing: false, toggle: null);

        Assert.Empty(keys);
        var browse = PanelKeySets.DesiredKeys(visible: true, PanelMode.Browse, composing: false, toggle: null);
        Assert.Equal(8, browse.Count);
    }

    // —— 差量推导（对已生效集合做增删） ——

    [Fact]
    public void Delta_BrowseToSearch_UnregistersOnlyYieldingKeys()
    {
        var browse = Desired(visible: true, PanelMode.Browse);
        var search = Desired(visible: true, PanelMode.Search);

        var stale = browse.Except(search).ToList();
        var added = search.Except(browse).ToList();

        Assert.Empty(added); // 退搜索只减不增
        Assert.Equal(
            [PanelNavKeys.VirtualKeySpace, PanelNavKeys.VirtualKeyDelete, PanelNavKeys.VirtualKeyB, PanelNavKeys.VirtualKeyZ],
            stale.Select(c => c.VirtualKey).OrderBy(v => v));
    }

    [Fact]
    public void Delta_ComposingOnAndOff_RestoresNavigationKeys()
    {
        var composing = Desired(visible: true, PanelMode.Search, composing: true);
        var normal = Desired(visible: true, PanelMode.Search, composing: false);

        var restored = normal.Except(composing).ToList();
        Assert.Equal(
            [PanelNavKeys.VirtualKeyReturn, PanelNavKeys.VirtualKeyEscape, PanelNavKeys.VirtualKeyUp, PanelNavKeys.VirtualKeyDown],
            restored.Select(c => c.VirtualKey).OrderBy(v => v));
    }

    // —— 长按重复（F19；legacy modes.rs NAV_REPEAT_*） ——

    [Fact]
    public void RepeatableNavigation_OnlyUpDown()
    {
        Assert.True(PanelNavKeys.IsRepeatableNavigation(PanelNavKeys.VirtualKeyUp));
        Assert.True(PanelNavKeys.IsRepeatableNavigation(PanelNavKeys.VirtualKeyDown));
        Assert.False(PanelNavKeys.IsRepeatableNavigation(PanelNavKeys.VirtualKeyReturn));
        Assert.False(PanelNavKeys.IsRepeatableNavigation(PanelNavKeys.VirtualKeyEscape));
        Assert.False(PanelNavKeys.IsRepeatableNavigation(0x56)); // V
    }

    [Fact]
    public void RepeatDelays_Initial300Interval50()
    {
        Assert.Equal(300, PanelNavKeys.RepeatInitialDelayMs);
        Assert.Equal(50, PanelNavKeys.RepeatIntervalMs);
    }

    // —— 模式判定（进入/退出事件、聚焦、面板可见要求） ——

    [Fact]
    public void ModeTraits_FocusAndVisibility()
    {
        // 搜索/备注要把面板聚焦起来；捕获态不要求（面板随后才呼出）、浏览态不是输入态
        Assert.True(PanelModeTraits.NeedsFocus(PanelMode.Search));
        Assert.True(PanelModeTraits.NeedsFocus(PanelMode.NoteEdit));
        Assert.False(PanelModeTraits.NeedsFocus(PanelMode.Browse));
        Assert.False(PanelModeTraits.NeedsFocus(PanelMode.ShortcutCapture));

        // 托盘「更换快捷键」在面板收起时进入捕获，只有捕获态不要求面板已显示
        Assert.True(PanelModeTraits.RequiresVisiblePanel(PanelMode.Search));
        Assert.True(PanelModeTraits.RequiresVisiblePanel(PanelMode.NoteEdit));
        Assert.True(PanelModeTraits.RequiresVisiblePanel(PanelMode.Browse));
        Assert.False(PanelModeTraits.RequiresVisiblePanel(PanelMode.ShortcutCapture));
    }

    [Fact]
    public void ModeEvents_EnterExitPerMode()
    {
        // 进入事件：搜索与备注各一条（备注带目标条目 id）；捕获/浏览没有
        Assert.Equal(new PanelRendererEvent.PanelKey("search-enter", null),
            PanelModeEvents.EnterEvent(PanelMode.Search, null));
        Assert.Equal(new PanelRendererEvent.PanelKey("note-edit-enter", "e1"),
            PanelModeEvents.EnterEvent(PanelMode.NoteEdit, "e1"));
        Assert.Null(PanelModeEvents.EnterEvent(PanelMode.ShortcutCapture, null));
        Assert.Null(PanelModeEvents.EnterEvent(PanelMode.Browse, null));

        // 退出事件：搜索/备注走 panel:key，捕获走 capture-end 独立通道，浏览没有
        Assert.Equal(new PanelRendererEvent.PanelKey("search-exit", null),
            PanelModeEvents.ExitEvent(PanelMode.Search));
        Assert.Equal(new PanelRendererEvent.PanelKey("note-edit-exit", null),
            PanelModeEvents.ExitEvent(PanelMode.NoteEdit));
        Assert.IsType<PanelRendererEvent.CaptureEnd>(PanelModeEvents.ExitEvent(PanelMode.ShortcutCapture));
        Assert.Null(PanelModeEvents.ExitEvent(PanelMode.Browse));
    }

    [Fact]
    public void NavAction_ProtocolNames()
    {
        // 渲染层 panel:key 协议动作名（legacy NavAction::as_str）
        Assert.Equal("up", PanelModesExtensions.ActionName(NavAction.Up));
        Assert.Equal("down", PanelModesExtensions.ActionName(NavAction.Down));
        Assert.Equal("enter", PanelModesExtensions.ActionName(NavAction.Enter));
        Assert.Equal("escape", PanelModesExtensions.ActionName(NavAction.Escape));
        Assert.Equal("delete", PanelModesExtensions.ActionName(NavAction.Delete));
        Assert.Equal("pin", PanelModesExtensions.ActionName(NavAction.Pin));
        Assert.Equal("note", PanelModesExtensions.ActionName(NavAction.Note));
        Assert.Equal("search", PanelModesExtensions.ActionName(NavAction.Search));
    }

    [Fact]
    public void DesiredKeys_BindingsCarryActions()
    {
        // 集合元素携带动作，执行注册时按动作分发（呼出=Toggle，导航=Nav）
        var browse = PanelKeySets.DesiredKeys(true, PanelMode.Browse, false, Toggle);

        Assert.All(browse, b =>
        {
            if (b.Combo == Toggle)
            {
                Assert.Equal(PanelKeyAction.Toggle, b.Action);
            }
            else
            {
                Assert.Equal(PanelKeyKind.Nav, b.Action.Kind);
            }
        });
        var up = browse.Single(b => b.Combo.VirtualKey == PanelNavKeys.VirtualKeyUp);
        Assert.Equal(PanelKeyAction.Nav(NavAction.Up), up.Action);
    }
}
