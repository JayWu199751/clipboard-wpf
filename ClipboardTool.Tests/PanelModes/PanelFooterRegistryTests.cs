using ClipboardTool.Domain.History;
using ClipboardTool.Domain.PanelModes;

namespace ClipboardTool.Tests.PanelModes;

/// <summary>
/// 页脚键位注册表（F45「提示 = 行为，写死键名即违规」；legacy keyboard.ts footerChips/KEY_NAMES）：
/// 页脚六组与备注组文案全部由 PanelFooterRegistry 注册表推导，组件不写字面键名。
/// </summary>
public class PanelFooterRegistryTests
{
    [Fact]
    public void 浏览态页脚_六组_顺序与文案来自注册表()
    {
        var chips = PanelFooterRegistry.BrowseChips();

        Assert.Equal(6, chips.Count);
        Assert.Equal(("↑↓", "选择"), (chips[0].Keys, chips[0].Action));
        Assert.Equal(("⏎", "复制"), (chips[1].Keys, chips[1].Action));
        Assert.Equal(("Z", "置顶"), (chips[2].Keys, chips[2].Action));
        Assert.Equal(("B", "备注"), (chips[3].Keys, chips[3].Action));
        Assert.Equal(("Del", "删除"), (chips[4].Keys, chips[4].Action));
        Assert.Equal(("Esc", "隐藏"), (chips[5].Keys, chips[5].Action));
    }

    [Fact]
    public void 上下键_拼进同一枚chip_与源UI同形()
    {
        var up = PanelFooterRegistry.Glyph(NavAction.Up);
        var down = PanelFooterRegistry.Glyph(NavAction.Down);
        var chips = PanelFooterRegistry.BrowseChips();

        Assert.Equal(up + down, chips[0].Keys);
    }

    [Fact]
    public void 注册表覆盖全部导航动作_字形不落在组件里()
    {
        foreach (NavAction action in Enum.GetValues<NavAction>())
        {
            Assert.False(string.IsNullOrEmpty(PanelFooterRegistry.Glyph(action)));
        }
    }

    [Fact]
    public void 备注态页脚_保存取消两组_字数段由渲染层拼接()
    {
        var chips = PanelFooterRegistry.NoteChips();

        Assert.Equal(2, chips.Count);
        Assert.Equal(("⏎", "保存"), (chips[0].Keys, chips[0].Action));
        Assert.Equal(("Esc", "取消"), (chips[1].Keys, chips[1].Action));
    }

    [Fact]
    public void 备注字数上限_与存储归一化上限同源()
    {
        Assert.Equal(200, PanelFooterRegistry.MaxNoteLength);
        Assert.Equal(HistoryStore.DefaultMaxNoteLength, PanelFooterRegistry.MaxNoteLength);
    }
}
