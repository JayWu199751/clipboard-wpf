namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 搜索头交互规则（纯函数，隧道处理与测试共用，F26）。
/// legacy 语义：浏览态点击井 = 进搜索；点击井内主题按钮 = 切主题不进搜索（两态皆然）；
/// 搜索态的井点击交给输入框，不重复激活。
/// </summary>
public static class PanelHeaderRules
{
    /// <summary>搜索井按下是否激活搜索：非搜索态且未按在主题按钮上。</summary>
    public static bool ShouldActivateSearch(bool searchActive, bool pressedThemeButton) =>
        !searchActive && !pressedThemeButton;
}
