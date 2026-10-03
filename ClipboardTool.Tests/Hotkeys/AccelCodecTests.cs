using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Tests.Hotkeys;

/// <summary>
/// accel 协议字符串 ↔ 组合键的编解码（码表按 legacy hotkeys.rs / tauri Shortcut 解析口径）：
/// 设置存档里存的是串（Control+Shift+V），注册与捕获要的是组合键；两向都要能走通。
/// </summary>
public class AccelCodecTests
{
    // —— 解析：合法 ——
    [Theory]
    [InlineData("Control+Shift+V", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56u)]
    [InlineData("Ctrl+Shift+V", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56u)] // 别名同一键
    [InlineData("CONTROL+SHIFT+V", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56u)] // 大小写不敏感
    [InlineData("CommandOrControl+D", HotkeyModifiers.Control, 0x44u)]
    [InlineData("Super+Space", HotkeyModifiers.Win, 0x20u)]
    [InlineData("Meta+Enter", HotkeyModifiers.Win, 0x0Du)]
    [InlineData("Alt+X", HotkeyModifiers.Alt, 0x58u)]
    [InlineData("Control+Alt+Shift+F12", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x7Bu)]
    [InlineData("Up", HotkeyModifiers.None, 0x26u)]
    [InlineData("Esc", HotkeyModifiers.None, 0x1Bu)]
    [InlineData("PageDown", HotkeyModifiers.None, 0x22u)]
    public void 解析合法accel(string accel, HotkeyModifiers modifiers, uint vk)
    {
        var combo = AccelCodec.Parse(accel);
        Assert.NotNull(combo);
        Assert.Equal(modifiers, combo.Value.Modifiers);
        Assert.Equal(vk, combo.Value.VirtualKey);
    }

    // —— 解析：非法 ——
    [Theory]
    [InlineData("")]                 // 空
    [InlineData("Control+Shift")]    // 有修饰无主键（工单校验也是拒）
    [InlineData("Shift")]            // 只有修饰键名单上的词，无主键
    [InlineData("NotAKey")]          // 主键认不出
    [InlineData("Control+")]         // 尾随分隔符
    [InlineData("Ctrl+Shift+Foo")]   // 主键非法
    public void 解析非法accel返回null(string accel)
    {
        Assert.Null(AccelCodec.Parse(accel));
    }

    // —— 编码：规范形（Control 命名 + legacy 修饰顺序 Control,Alt,Shift,Win） ——
    [Fact]
    public void 编码走规范形_往返一致()
    {
        Assert.Equal("Control+Shift+V", AccelCodec.Encode(new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56)));
        Assert.Equal("Win+Space", AccelCodec.Encode(new(HotkeyModifiers.Win, 0x20)));
        Assert.Equal("Control+Alt+Shift+F5",
            AccelCodec.Encode(new(HotkeyModifiers.Shift | HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x74)));
        // 往返：解析出的组合键编码回规范串
        Assert.Equal("Control+Shift+V", AccelCodec.Encode(AccelCodec.Parse("Ctrl+Shift+V")!.Value));
    }

    // —— 展示文案（legacy format_shortcut；托盘菜单/捕获覆盖层共用） ——
    [Theory]
    [InlineData("Control+Shift+V", "Ctrl + Shift + V")]
    [InlineData("CommandOrControl+D", "Ctrl + D")]
    [InlineData("Super+Space", "Win + Space")]
    [InlineData("Meta+Enter", "Win + Enter")]
    [InlineData("Alt+Shift+Delete", "Alt + Shift + Delete")]
    [InlineData("Esc", "Esc")]
    [InlineData("Up", "Up")]
    [InlineData("F5", "F5")]
    public void 展示文案_缩写与间隔(string accel, string expected)
    {
        Assert.Equal(expected, AccelCodec.FormatDisplay(accel));
    }

    [Fact]
    public void 展示文案_空串退回默认键()
    {
        Assert.Equal("Ctrl + Shift + V", AccelCodec.FormatDisplay(""));
    }
}
