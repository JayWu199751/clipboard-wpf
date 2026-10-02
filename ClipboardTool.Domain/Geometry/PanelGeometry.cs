namespace ClipboardTool.Domain.Geometry;

/// <summary>
/// 呼出面板的几何纯规则（F15 尺寸公式、F16 工作区居中/停靠）。
/// 只做 DIP 运算；物理像素与显示器的换算归 Infrastructure。
/// </summary>
public static class PanelGeometry
{
    /// <summary>停靠位：工作区右缘外 20 DIP。</summary>
    public const double DockOffsetDip = 20;

    /// <summary>无显示器可停靠时的兜底坐标。</summary>
    public static readonly DipPoint DockFallback = new(-10000, 0);

    /// <summary>面板高 = 显示器全屏物理高 ÷ DPI × 7/8（四舍五入），宽 = 高 ÷ 2（四舍五入）。</summary>
    public static PanelSize CalculateSize(double monitorPhysicalHeightPx, double dpiScale)
    {
        var dipHeight = monitorPhysicalHeightPx / dpiScale;
        var height = RoundAway(dipHeight * 7.0 / 8.0);
        return new PanelSize(RoundAway(height / 2.0), height);
    }

    /// <summary>呼出落地：光标所在屏工作区居中；光标屏缺省或放不下时主屏兜底。</summary>
    public static DipPoint ResolveSummonPosition(
        PanelSize panel, WorkAreaDip? cursorScreenWorkArea, WorkAreaDip primaryWorkArea)
    {
        var area = cursorScreenWorkArea is { } cursor && Fits(panel, cursor) ? cursor : primaryWorkArea;
        return new DipPoint(
            RoundAway(area.X + (area.Width - panel.Width) / 2),
            RoundAway(area.Y + (area.Height - panel.Height) / 2));
    }

    /// <summary>停靠位：工作区右缘外 20 DIP、y = 工作区顶；无显示器时兜底 (-10000, 0)。</summary>
    public static DipPoint ResolveDockPosition(WorkAreaDip? workArea) =>
        workArea is { } area
            ? new DipPoint(RoundAway(area.Right + DockOffsetDip), RoundAway(area.Y))
            : DockFallback;

    private static bool Fits(PanelSize panel, WorkAreaDip area) =>
        panel.Width <= area.Width && panel.Height <= area.Height;

    private static double RoundAway(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}
