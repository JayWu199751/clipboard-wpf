using ClipboardTool.Domain.Settings;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 托盘图标的纯判定（F29；legacy tray.rs 判定一~六）：五档阶梯按主屏 scaleFactor 取
/// 「恰好物理尺寸」，去重键取实际选中档位（两档之间的缩放变化不改图标不重设），
/// 左键只认抬起呼出，菜单三条随状态变化的文案与主题子菜单勾选。
/// </summary>
public class TrayIconDeciderTests
{
    [Theory]
    [InlineData(1.0, 16)]   // 整数缩放恰好命中
    [InlineData(1.25, 20)]
    [InlineData(1.5, 24)]
    [InlineData(1.75, 28)]  // 本机 175% 实况
    [InlineData(2.0, 32)]
    [InlineData(1.1, 16)]   // 目标 18px 与 16/20 等距 → 取较小档
    [InlineData(1.4, 20)]
    [InlineData(0.5, 16)]   // 低于最小档夹下端
    [InlineData(10.0, 32)]  // 高于最大档夹上端
    public void 缩放取最近档位(double scale, int expected)
    {
        Assert.Equal(expected, TrayIconDecider.SizeForScale(scale));
    }

    [Fact]
    public void 去重键_同档位不重设_跨档位或换主题才变()
    {
        // 1.0 与 1.1 都落在 16 档：图标没变，键也不该变
        Assert.Equal(TrayIconDecider.IconKey(dark: false, 1.0), TrayIconDecider.IconKey(dark: false, 1.1));
        Assert.NotEqual(TrayIconDecider.IconKey(dark: false, 1.0), TrayIconDecider.IconKey(dark: false, 1.25));
        Assert.NotEqual(TrayIconDecider.IconKey(dark: false, 1.0), TrayIconDecider.IconKey(dark: true, 1.0));
        // 键里带的是选中的档位；深色任务栏用浅色（白色）图，故 dark 对应 light 资产
        Assert.Equal("light@24", TrayIconDecider.IconKey(dark: true, 1.5));
        Assert.Equal("dark@32", TrayIconDecider.IconKey(dark: false, 2.0));
    }

    // —— 哪一发托盘事件算呼出（legacy 判定四） ——
    [Fact]
    public void 一次左键点击只投一次呼出_按下与右键都不算()
    {
        Assert.True(TrayIconDecider.OpensPanel(TrayMouseEvent.LeftUp));
        Assert.False(TrayIconDecider.OpensPanel(TrayMouseEvent.LeftDown),
            "外壳对一次左键点击发来 Down + Up 两条，两条都认就是一次点击两次呼出");
        Assert.False(TrayIconDecider.OpensPanel(TrayMouseEvent.RightUp), "右键那一下是弹菜单的");
        Assert.False(TrayIconDecider.OpensPanel(TrayMouseEvent.Enter));
        Assert.True(TrayIconDecider.OpensPanel(TrayMouseEvent.LeftDoubleClick));
    }

    [Fact]
    public void 指针进图标算核配色的时机_不算呼出()
    {
        Assert.True(TrayIconDecider.PointerEntered(TrayMouseEvent.Enter));
        Assert.False(TrayIconDecider.OpensPanel(TrayMouseEvent.Enter));
        Assert.False(TrayIconDecider.PointerEntered(TrayMouseEvent.LeftUp));
    }

    // —— 菜单文案（legacy 判定六） ——
    [Fact]
    public void 菜单文案_快捷键走展示格式_开机启动带状态符号()
    {
        var labels = TrayIconDecider.MenuLabels("Control+Shift+V", autoStart: true);
        Assert.Equal("更换快捷键(当前: Ctrl + Shift + V)", labels.Shortcut);
        Assert.Equal("开机启动 ✅", labels.Autostart);

        var off = TrayIconDecider.MenuLabels("Alt+X", autoStart: false);
        Assert.Equal("更换快捷键(当前: Alt + X)", off.Shortcut);
        Assert.Equal("开机启动 ❌", off.Autostart);

        // 空快捷键归一为默认键，与存档契约同一口径
        Assert.Equal("更换快捷键(当前: Ctrl + Shift + V)", TrayIconDecider.MenuLabels("", true).Shortcut);
    }

    [Fact]
    public void 主题子菜单标题带当前态()
    {
        Assert.Equal("主题(当前: 跟随系统)", TrayIconDecider.ThemeMenuTitle(ThemeKind.System));
        Assert.Equal("主题(当前: 暗色)", TrayIconDecider.ThemeMenuTitle(ThemeKind.Dark));
    }

    [Fact]
    public void 主题子菜单恰好一项打勾_且能映射回当前偏好()
    {
        foreach (var selected in new[] { ThemeKind.System, ThemeKind.Light, ThemeKind.Dark })
        {
            var items = TrayIconDecider.ThemeMenuItems(selected);
            var checkedItems = items.Where(item => item.Checked).ToList();
            Assert.Single(checkedItems);
            Assert.Equal(selected, TrayIconDecider.ThemeOfMenuId(checkedItems[0].Id));
            var labels = items.Select(item => item.Label).ToList();
            Assert.All(labels, label => Assert.False(string.IsNullOrEmpty(label)));
            Assert.Equal(3, labels.Distinct().Count());
        }
    }

    [Fact]
    public void 主题菜单id只认三条_其余一律不改设置()
    {
        Assert.Equal(ThemeKind.System, TrayIconDecider.ThemeOfMenuId("theme-system"));
        Assert.Equal(ThemeKind.Light, TrayIconDecider.ThemeOfMenuId("theme-light"));
        Assert.Equal(ThemeKind.Dark, TrayIconDecider.ThemeOfMenuId("theme-dark"));
        // 子菜单本身的 id、其余菜单项 id、大小写与近似拼写都不算
        foreach (var id in new[] { "", "theme", "autostart", "clear-history", "theme-bright", "Theme-Light" })
        {
            Assert.Null(TrayIconDecider.ThemeOfMenuId(id));
        }
    }
}
