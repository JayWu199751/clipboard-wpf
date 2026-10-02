namespace ClipboardTool.Domain.Geometry;

/// <summary>呼出面板的尺寸（DIP）。</summary>
/// <param name="Width">面板宽 = 高 ÷ 2（四舍五入）。</param>
/// <param name="Height">面板高 = 显示器全屏物理高 ÷ DPI × 7/8（四舍五入）。</param>
public readonly record struct PanelSize(double Width, double Height);

/// <summary>某显示器的工作区（DIP；坐标按该屏 DPI 从物理像素折算）。</summary>
public readonly record struct WorkAreaDip(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>DIP 坐标点。</summary>
public readonly record struct DipPoint(double X, double Y);
