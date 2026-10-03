namespace P5RoundedHitTest;

/// <summary>命中判定结果：弧内命中（正常交互）/ 弧外穿透（HTTRANSPARENT 透传下层）。</summary>
public enum CornerHit
{
    Client,
    Transparent,
}

/// <summary>
/// P5 命中几何纯函数（零 WPF/Win32 依赖）：物理像素坐标下的圆角矩形内/外判定、
/// DIP↔物理换算、WM_NCHITTEST lParam 有符号解析。单测见 GeometryTests.cs；
/// 运行时由试验窗口的 WM_NCHITTEST 钩子调用。
/// </summary>
public static class CornerHitGeometry
{
    /// <summary>圆角半径 DIP → 物理像素。</summary>
    public static double ScaleRadius(double radiusDip, double pixelsPerDip) => radiusDip * pixelsPerDip;

    /// <summary>物理像素 → DIP。</summary>
    public static double PhysicalToDip(double physicalPx, double pixelsPerDip) => physicalPx / pixelsPerDip;

    /// <summary>
    /// 物理像素坐标判定点是否在圆角矩形内（窗口左上角为原点，width/height 为物理尺寸）。
    /// 角部方带外（直边区）必命中；角部按到该角弧心的欧氏距离判定，恰在弧上归弧内。
    /// </summary>
    public static CornerHit HitTestPhysical(double x, double y, double width, double height, double radiusPx)
    {
        // 窗口外一律穿透（兜底：系统对窗口外坐标不应派发 WM_NCHITTEST，防御性处理）
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return CornerHit.Transparent;
        }

        // 半径钳制到短边一半：超值时四弧相接，按小半径判定仍正确
        var r = Math.Min(radiusPx, Math.Min(width, height) / 2);

        bool leftBand = x < r;
        bool rightBand = x >= width - r;
        bool topBand = y < r;
        bool bottomBand = y >= height - r;

        // 不与任何角部方带相交 → 直边区，必命中
        if ((!(leftBand || rightBand)) || (!(topBand || bottomBand)))
        {
            return CornerHit.Client;
        }

        var cx = leftBand ? r : width - r;
        var cy = topBand ? r : height - r;
        var dx = x - cx;
        var dy = y - cy;

        return dx * dx + dy * dy <= r * r ? CornerHit.Client : CornerHit.Transparent;
    }

    /// <summary>
    /// 解析 WM_NCHITTEST 的 lParam 为屏幕物理坐标：低/高 16 位各为有符号 short
    /// （多显示器负坐标屏下坐标可为负，必须按有符号取）。
    /// </summary>
    public static (int X, int Y) UnpackLParam(IntPtr lParam)
    {
        var v = lParam.ToInt64();
        return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }
}
