using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Tests.Hotkeys;

/// <summary>
/// 捕获覆盖层的组合键校验（F31；legacy App.tsx shortcutCapture keydown 判定）：
/// Ctrl / Alt / Win 至少含一 + 主键可识别；Shift 单独不合格（Shift+X 只是打字）；
/// 裸主键也不合格。Esc 不进校验——它是取消键，捕获层先行消费。
/// </summary>
public class CaptureKeyRulesTests
{
    private static HotkeyCombo Combo(HotkeyModifiers modifiers, uint vk) => new(modifiers, vk);

    [Theory]
    [InlineData(HotkeyModifiers.Control)]
    [InlineData(HotkeyModifiers.Alt)]
    [InlineData(HotkeyModifiers.Win)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)]
    public void 含_ctrl_alt_win_至少一_合格(HotkeyModifiers modifiers)
    {
        Assert.Equal(CaptureVerdict.Ok,
            CaptureKeyRules.Validate(Combo(modifiers, 0x58)));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None)]      // 裸主键
    [InlineData(HotkeyModifiers.Shift)]     // Shift 单独（Shift+X 只是打字）
    public void 只有_shift_或无修饰_不合格(HotkeyModifiers modifiers)
    {
        var verdict = CaptureKeyRules.Validate(Combo(modifiers, 0x58));
        Assert.NotEqual(CaptureVerdict.Ok, verdict);
        Assert.Contains("Ctrl / Alt / Win", CaptureKeyRules.MessageOf(verdict));
    }

    [Fact]
    public void 提示文案与legacy一致()
    {
        Assert.Equal("请包含 Ctrl / Alt / Win 修饰键",
            CaptureKeyRules.MessageOf(CaptureVerdict.MissingModifier));
        Assert.Equal("无法识别的按键", CaptureKeyRules.MessageOf(CaptureVerdict.UnknownKey));
    }
}
