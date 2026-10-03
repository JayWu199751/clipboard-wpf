namespace ClipboardTool.Domain.Settings;

/// <summary>主题偏好三态循环（F26；legacy panelView.ts themeControl：light→dark→system→light）。</summary>
public static class ThemeKindExtensions
{
    public static ThemeKind Next(this ThemeKind theme) => theme switch
    {
        ThemeKind.Light => ThemeKind.Dark,
        ThemeKind.Dark => ThemeKind.System,
        _ => ThemeKind.Light,
    };
}
