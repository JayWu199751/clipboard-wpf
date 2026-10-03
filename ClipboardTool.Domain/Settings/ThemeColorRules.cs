namespace ClipboardTool.Domain.Settings;

/// <summary>
/// 系统明暗判定的纯规则（F26–F28；legacy tray.rs taskbar_is_dark 移植）。
/// 读数侧（注册表取数）归 Infrastructure；这里只判「键值 → 明暗」。
/// </summary>
public static class ThemeColorRules
{
    /// <summary>
    /// 任务栏那块面板是不是深色（是 → 托盘该用浅色描边那套图）。
    /// 只看「Windows 模式」SystemUsesLightTheme（任务栏、开始菜单跟它走），不是应用模式的
    /// AppsUseLightTheme（管窗口内容）：「自定义」模式下两者可以相反，而托盘图标画在任务栏上。
    /// SystemUsesLightTheme 缺键（None）才退 AppsUseLightTheme；两个键都没写（精简系统/GPO）
    /// 按浅色任务栏处理——宁可深描边，也别把白图贴到可能亮的条上。
    /// </summary>
    public static bool TaskbarIsDark(uint? systemUsesLight, uint? appsUseLight)
    {
        var light = systemUsesLight ?? appsUseLight;
        return light is 0;
    }

    /// <summary>
    /// 应用模式是不是深色（面板内容皮肤用）：只看 AppsUseLightTheme，与任务栏判定互不干扰。
    /// 缺键按浅色处理（与任务栏缺键兜底同一口径）。
    /// </summary>
    public static bool AppIsDark(uint? appsUseLight) => appsUseLight is 0;
}
