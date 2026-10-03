using ClipboardTool.Domain.Settings;

namespace ClipboardTool.Tests.Settings;

/// <summary>
/// 明暗判定的两条独立来源（F26–F28；legacy tray.rs taskbar_is_dark）：
/// 托盘图标画在任务栏上，只看「Windows 模式」SystemUsesLightTheme；
/// 面板内容是应用皮肤，看「应用模式」AppsUseLightTheme。
/// 「个性化 → 颜色 → 默认模式 = 自定义」时两个键可以相反，此时两者各判各的（ADR-0012）。
/// 键缺失是「这台机器没写过」，不是「值为 0」：None 与 Some(0) 语义不同。
/// </summary>
public class ThemeColorRulesTests
{
    // —— 任务栏（托盘图标用）：SystemUsesLightTheme 优先，缺键才退 AppsUseLightTheme ——
    [Fact]
    public void 任务栏明暗_两键相反时只认_windows_模式键()
    {
        Assert.False(ThemeColorRules.TaskbarIsDark(systemUsesLight: 1, appsUseLight: 0),
            "亮任务栏 + 暗应用：该用深色描边的图");
        Assert.True(ThemeColorRules.TaskbarIsDark(systemUsesLight: 0, appsUseLight: 1),
            "暗任务栏 + 亮应用：该用白色描边的图");
    }

    [Fact]
    public void 任务栏明暗_windows_模式键缺失才退应用模式键()
    {
        Assert.True(ThemeColorRules.TaskbarIsDark(systemUsesLight: null, appsUseLight: 0));
        Assert.False(ThemeColorRules.TaskbarIsDark(systemUsesLight: null, appsUseLight: 1));
    }

    [Fact]
    public void 任务栏明暗_两键都缺按浅色任务栏处理()
    {
        // 精简系统/GPO 未写键：宁可深描边，也别把白图贴到可能亮的条上
        Assert.False(ThemeColorRules.TaskbarIsDark(null, null));
    }

    // —— 应用模式（面板内容用）：只看 AppsUseLightTheme，与任务栏互不干扰 ——
    [Fact]
    public void 应用明暗_只认应用模式键()
    {
        Assert.True(ThemeColorRules.AppIsDark(appsUseLight: 0));
        Assert.False(ThemeColorRules.AppIsDark(appsUseLight: 1));
    }

    [Fact]
    public void 应用明暗_缺键按浅色处理()
    {
        Assert.False(ThemeColorRules.AppIsDark(null));
    }
}
