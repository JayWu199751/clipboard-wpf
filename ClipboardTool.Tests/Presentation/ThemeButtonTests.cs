// 主题按钮（F26 面板入口）：井点击的进搜索排除规则 + 按钮三态展示映射。
// 回归钉子（用户报告）：搜索井内主题按钮点击后进入搜索而非切换主题——
// 根因是按钮此前为占位 Border 未接线，井的隧道处理按来源排除后两态都应切主题。
using System.Windows;
using ClipboardTool.Domain.Settings;
using ClipboardTool.Presentation.Wpf;
using Xunit;

namespace ClipboardTool.Tests.Presentation;

// —— 井点击规则：主题按钮排除（隧道祖先先至，排除必须在井的处理里做） ——
public class PanelHeaderRulesTests
{
    [Theory]
    [InlineData(false, false, true)]  // 浏览态点井 = 进搜索
    [InlineData(true, false, false)]  // 搜索态井点击交给输入框，不重复激活
    [InlineData(false, true, false)]  // 浏览态点主题按钮 = 切主题，不进搜索（本次回归）
    [InlineData(true, true, false)]   // 搜索态点主题按钮 = 切主题，不进搜索
    public void 井点击是否激活搜索按搜索态与主题按钮来源判定(
        bool searchActive, bool pressedThemeButton, bool expected)
    {
        Assert.Equal(expected, PanelHeaderRules.ShouldActivateSearch(searchActive, pressedThemeButton));
    }
}

// —— 按钮三态展示：图标与文案展示当前偏好，提示含下一态（与 ThemeKindExtensions 循环一致） ——
public class ThemeButtonViewModelTests
{
    [Fact]
    public void 默认偏好跟随系统_显示器图标可见_提示下一态亮色()
    {
        var vm = new PanelViewModel();
        Assert.Equal(ThemeKind.System, vm.ThemePreference);
        Assert.Equal(Visibility.Visible, vm.ThemeMonitorVisible);
        Assert.Equal(Visibility.Collapsed, vm.ThemeSunVisible);
        Assert.Equal(Visibility.Collapsed, vm.ThemeMoonVisible);
        Assert.Equal(ThemeLabels.System, vm.ThemeButtonLabel);
        Assert.Contains(ThemeLabels.Light, vm.ThemeButtonTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void 亮色偏好_太阳图标可见_提示下一态暗色()
    {
        var vm = new PanelViewModel { ThemePreference = ThemeKind.Light };
        Assert.Equal(Visibility.Visible, vm.ThemeSunVisible);
        Assert.Equal(Visibility.Collapsed, vm.ThemeMoonVisible);
        Assert.Equal(Visibility.Collapsed, vm.ThemeMonitorVisible);
        Assert.Equal(ThemeLabels.Light, vm.ThemeButtonLabel);
        Assert.Contains(ThemeLabels.Dark, vm.ThemeButtonTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void 暗色偏好_月亮图标可见_提示下一态跟随系统()
    {
        var vm = new PanelViewModel { ThemePreference = ThemeKind.Dark };
        Assert.Equal(Visibility.Visible, vm.ThemeMoonVisible);
        Assert.Equal(Visibility.Collapsed, vm.ThemeSunVisible);
        Assert.Equal(Visibility.Collapsed, vm.ThemeMonitorVisible);
        Assert.Equal(ThemeLabels.Dark, vm.ThemeButtonLabel);
        Assert.Contains(ThemeLabels.System, vm.ThemeButtonTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void 提示的下一态链与三态循环一致_系统回亮_亮到暗_暗到系统()
    {
        // 下一态链钉住 legacy 循环：亮色 → 暗色 → 跟随系统 → 亮色（themeControl.next）
        Assert.Equal(ThemeKind.Light, ThemeKind.System.Next());
        Assert.Equal(ThemeKind.Dark, ThemeKind.Light.Next());
        Assert.Equal(ThemeKind.System, ThemeKind.Dark.Next());
    }
}
