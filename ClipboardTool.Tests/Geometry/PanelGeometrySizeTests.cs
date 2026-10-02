using ClipboardTool.Domain.Geometry;

namespace ClipboardTool.Tests.Geometry;

/// <summary>面板尺寸公式（F15）：显示器全屏物理高 ÷ DPI × 7/8（四舍五入），宽 = 高 ÷ 2（四舍五入）。</summary>
public class PanelGeometrySizeTests
{
    [Theory]
    [InlineData(1080, 1.0, 473, 945)]   // 1080p@100%
    [InlineData(1440, 2.0, 315, 630)]   // 1440p@200%
    [InlineData(1440, 1.75, 360, 720)]  // 1440p@175%
    public void CalculateSize_MatchesSpecExamples(
        double monitorPhysicalHeightPx, double dpiScale, double expectedWidth, double expectedHeight)
    {
        var size = PanelGeometry.CalculateSize(monitorPhysicalHeightPx, dpiScale);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }
}
