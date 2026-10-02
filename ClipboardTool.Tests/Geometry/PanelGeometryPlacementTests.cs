using ClipboardTool.Domain.Geometry;

namespace ClipboardTool.Tests.Geometry;

/// <summary>呼出落地与停靠坐标（F16）：光标屏工作区居中、放不下主屏兜底；停靠右缘外 20 DIP。</summary>
public class PanelGeometryPlacementTests
{
    [Fact]
    public void ResolveSummonPosition_CentersInCursorWorkArea()
    {
        var panel = new PanelSize(473, 945);

        var position = PanelGeometry.ResolveSummonPosition(
            panel, cursorScreenWorkArea: new WorkAreaDip(0, 0, 1920, 1040), primaryWorkArea: new WorkAreaDip(0, 0, 1920, 1040));

        Assert.Equal(new DipPoint(724, 48), position);
    }

    [Fact]
    public void ResolveSummonPosition_KeepsCursorScreen_WhenPanelFitsExactly()
    {
        var panel = new PanelSize(473, 945);

        var position = PanelGeometry.ResolveSummonPosition(
            panel, cursorScreenWorkArea: new WorkAreaDip(0, 0, 473, 945), primaryWorkArea: new WorkAreaDip(0, 0, 1920, 1040));

        Assert.Equal(new DipPoint(0, 0), position);
    }

    [Fact]
    public void ResolveSummonPosition_FallsBackToPrimary_WhenCursorWorkAreaTooSmall()
    {
        var panel = new PanelSize(473, 945);

        var position = PanelGeometry.ResolveSummonPosition(
            panel, cursorScreenWorkArea: new WorkAreaDip(0, 0, 800, 900), primaryWorkArea: new WorkAreaDip(0, 0, 1920, 1040));

        Assert.Equal(new DipPoint(724, 48), position);
    }

    [Fact]
    public void ResolveSummonPosition_UsesPrimary_WhenCursorScreenMissing()
    {
        var panel = new PanelSize(473, 945);

        var position = PanelGeometry.ResolveSummonPosition(
            panel, cursorScreenWorkArea: null, primaryWorkArea: new WorkAreaDip(0, 0, 1920, 1040));

        Assert.Equal(new DipPoint(724, 48), position);
    }

    [Fact]
    public void ResolveSummonPosition_RespectsWorkAreaOffset()
    {
        var panel = new PanelSize(315, 630);

        var position = PanelGeometry.ResolveSummonPosition(
            panel, cursorScreenWorkArea: new WorkAreaDip(100, 40, 800, 700), primaryWorkArea: new WorkAreaDip(0, 0, 1920, 1040));

        Assert.Equal(new DipPoint(343, 75), position);
    }

    [Fact]
    public void ResolveDockPosition_IsBeyondRightEdge_AtWorkAreaTop()
    {
        var position = PanelGeometry.ResolveDockPosition(new WorkAreaDip(0, 0, 1920, 1040));

        Assert.Equal(new DipPoint(1940, 0), position);
    }

    [Fact]
    public void ResolveDockPosition_FallsBackOffscreen_WhenNoMonitor()
    {
        Assert.Equal(new DipPoint(-10000, 0), PanelGeometry.ResolveDockPosition(null));
    }
}
