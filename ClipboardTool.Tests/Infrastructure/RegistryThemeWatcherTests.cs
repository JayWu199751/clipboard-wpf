using ClipboardTool.Infrastructure.Windows;
using Microsoft.Win32;

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

    // —— RegGetValueW pcbData 回归（真机 E2E 实测缺陷）：键存在时读数必须命中真实值，
    //    不得退化为 null（null=缺键语义，会把亮暗判定推到浅色兜底，面板永远不跟随系统）。
    [Fact]
    public void 键存在时读数必须命中系统真实值()
    {
        const string personalize =
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        var watcher = new RegistryThemeWatcher();

        var appsRaw = Registry.GetValue(personalize, "AppsUseLightTheme", null);
        if (appsRaw is not null)
        {
            Assert.True(watcher.ReadAppsUseLightTheme() == (uint)(int)appsRaw,
                "AppsUseLightTheme 键存在却读成 null/错值（RegGetValueW 调用约定回归）");
        }

        var systemRaw = Registry.GetValue(personalize, "SystemUsesLightTheme", null);
        if (systemRaw is not null)
        {
            Assert.True(watcher.ReadSystemUsesLightTheme() == (uint)(int)systemRaw,
                "SystemUsesLightTheme 键存在却读成 null/错值（RegGetValueW 调用约定回归）");
        }
    }
}
