using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 注册表主题读数（F26–F28 效果侧）的真机环境 smoke：键值语义只能是「缺失（null）」或
/// 「0/1」。具体亮暗值随系统状态变化，不在这里断言（判定规则由 ThemeColorRulesTests 钉死）。
/// RegNotifyChangeKeyValue 广播到事件的时机是 T07 真机步骤（工单真机判据①）。
/// </summary>
public class RegistryThemeWatcherTests
{
    [Fact]
    public void 读数只能取到null或布尔DWORD()
    {
        var watcher = new RegistryThemeWatcher();
        var apps = watcher.ReadAppsUseLightTheme();
        var system = watcher.ReadSystemUsesLightTheme();

        Assert.True(apps is null or 0 or 1, $"AppsUseLightTheme 读数越界: {apps}");
        Assert.True(system is null or 0 or 1, $"SystemUsesLightTheme 读数越界: {system}");
    }
}
