using P5RoundedHitTest;
using Xunit;

namespace P5RoundedHitTest.Tests;

/// <summary>
/// 判据 3：命中几何纯函数——DIP↔物理换算、圆角矩形内/外判定、WM_NCHITTEST lParam 有符号解析。
/// 期望值全部为手工推演的独立事实源（3-4-5 直角三角形等），不是按实现重算。
/// 四档 DPI（100/125/150/175%）在此以参数化数学验证；真机运行时行为归自检程序（175% 实测）。
/// </summary>
public class GeometryTests
{
    public static TheoryData<double> Scales => [1.0, 1.25, 1.5, 1.75];

    // —— 换算：radius 36 DIP 在四档 DPI 下的物理半径（36/45/54/63 px，可心算验证） ——

    [Theory]
    [MemberData(nameof(Scales))]
    public void 三十六_DIP_圆角半径按比例换算成物理像素(double scale)
    {
        Assert.Equal(36 * scale, CornerHitGeometry.ScaleRadius(36, scale), 10);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void 物理像素换回_DIP_往返一致(double scale)
    {
        Assert.Equal(36, CornerHitGeometry.PhysicalToDip(CornerHitGeometry.ScaleRadius(36, scale), scale), 10);
    }

    // —— 命中判定：700×1050 物理窗口、半径 63 px（= 36 DIP @ 175%），窗口左上角为原点 ——

    private const double W = 700;
    private const double H = 1050;
    private const double R = 63; // 36 DIP × 1.75

    [Fact]
    public void 中央与四边中带_必命中()
    {
        // 角部方带 [0,63)∪(637,700] 之外全是直边区
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(350, 525, W, H, R));
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(350, 10, W, H, R));   // 上边中带
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(350, H - 5, W, H, R)); // 下边中带
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(10, 525, W, H, R));   // 左边中带
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(W - 5, 525, W, H, R)); // 右边中带
    }

    [Fact]
    public void 左上角_弧内命中弧外穿透_手工推演点()
    {
        // 弧心 (63,63)。3-4-5 三角形：半径 63 = 37.8²+50.4² 的平方和（0.6/0.8 方向）
        // 弧内（距离 52.5 < 63）：(63-31.5, 63-42) = (31.5, 21)
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(31.5, 21, W, H, R));
        // 恰在弧上（距离恰 63）：(25.2, 12.6) —— 边界归弧内
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(25.2, 12.6, W, H, R));
        // 弧外（距离 70 > 63）：(21, 7)
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(21, 7, W, H, R));
        // 对角深外：距离 √(53²+53²) ≈ 74.95
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(10, 10, W, H, R));
    }

    [Fact]
    public void 四个角_对角浅处命中深处穿透_几何对称()
    {
        // 浅点：距各自弧心 (0.5R, 0.5R) 方向移动，距离 = R√0.5 ≈ 44.5 < 63 → 命中
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(R - R / 2 * 0.7071, R - R / 2 * 0.7071, W, H, R)); // 左上
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(W - R + R / 2 * 0.7071, R - R / 2 * 0.7071, W, H, R)); // 右上
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(R - R / 2 * 0.7071, H - R + R / 2 * 0.7071, W, H, R)); // 左下
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(W - R + R / 2 * 0.7071, H - R + R / 2 * 0.7071, W, H, R)); // 右下
        // 深点：对角向 (0.2R, 0.2R)，距弧心 (R,R) = R√2·0.8 ≈ 1.13R > 63 → 穿透
        // （对角线上 t < R(1−1/√2) ≈ 0.293R 才在弧外，t∈[0.293R, R] 都在弧内）
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(R * 0.2, R * 0.2, W, H, R));
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(W - R * 0.2, R * 0.2, W, H, R));
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(R * 0.2, H - R * 0.2, W, H, R));
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(W - R * 0.2, H - R * 0.2, W, H, R));
        // 对角线上 t = 0.35R：距弧心 √2·0.65R ≈ 0.92R < R → 命中（穿透/命中分界验证）
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(R * 0.35, R * 0.35, W, H, R));
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void 四档DPI_同一点相对角_半径缩放后判定翻转(double scale)
    {
        // 取左上角对角线上距角点 0.2·(36 DIP·scale) 处：距弧心 = r√2·0.8 ≈ 1.13r > r → 穿透；
        // 向内挪到 0.95r 处：距弧心 = r√2·0.05 ≈ 0.07r → 必命中。半径随 DPI 缩放后分界不变。
        double r = 36 * scale;
        double inside = r * 0.95;
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(inside, inside, 700 * scale, 1050 * scale, r));
        double outside = r * 0.2;
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(outside, outside, 700 * scale, 1050 * scale, r));
    }

    [Fact]
    public void 窗口外坐标一律穿透_兜底()
    {
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(-1, 10, W, H, R));
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(10, -1, W, H, R));
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(W, 10, W, H, R));
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(10, H, W, H, R));
    }

    [Fact]
    public void 半径超过短边一半_钳制到短边一半()
    {
        // 100×100 窗口、半径 63 → 钳到 50。点 (10,10) 距弧心 (50,50) 约 56.6 > 50 → 穿透。
        Assert.Equal(CornerHit.Transparent, CornerHitGeometry.HitTestPhysical(10, 10, 100, 100, 63));
        // 点 (30,30) 距弧心约 28.3 < 50 → 命中。
        Assert.Equal(CornerHit.Client, CornerHitGeometry.HitTestPhysical(30, 30, 100, 100, 63));
    }

    // —— lParam 解析：MAKELPARAM 两个有符号 16 位（多显示器负坐标屏，判据 4） ——

    [Fact]
    public void lParam_正坐标解析()
    {
        // MAKELPARAM(100, 200) = 0x00C80064
        var lp = new IntPtr(0x00C8_0064);
        Assert.Equal((100, 200), CornerHitGeometry.UnpackLParam(lp));
    }

    [Fact]
    public void lParam_负坐标按有符号十六位解析()
    {
        // 左侧副屏场景：MAKELPARAM(-100, -50)：低 16 位 0xFF9C、高 16 位 0xFFCE
        var lp = new IntPtr(unchecked((int)0xFFCE_FF9C));
        Assert.Equal((-100, -50), CornerHitGeometry.UnpackLParam(lp));
        // 64 位符号扩展形态（消息泵透传时可能出现）
        var lp64 = new IntPtr(unchecked((long)0xFFFF_FFFF_FFCE_FF9C));
        Assert.Equal((-100, -50), CornerHitGeometry.UnpackLParam(lp64));
        // 混合：x 正 y 负（顶边上方副屏）
        var mixed = new IntPtr(unchecked((int)0xFFC0_012C)); // MAKELPARAM(300, -64)
        Assert.Equal((300, -64), CornerHitGeometry.UnpackLParam(mixed));
    }
}
