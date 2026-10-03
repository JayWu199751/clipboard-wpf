using ClipboardTool.Infrastructure.Windows;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// DibDecoder 字节矩阵测试正式化（T06，P3 原型 DibDecodeMatrixTests 迁入）。
/// 判据 1/2：样例 DIB 字节矩阵逐例解码，像素与 legacy dib.rs 测试向量一致；alpha 保留。
/// 期望值全部来自 legacy 测试向量（独立事实源），不是按实现重算。
/// </summary>
public class DibDecodeMatrixTests
{
    [Fact]
    public void Pixpin截图形状_V5头32bpp带alpha掩码_能解出像素且alpha保留()
    {
        // 回归本 bug 的最小形状：BITMAPV5HEADER + BI_RGB + alphaMask=0xff000000，
        // 像素 BGRA、自下而上。改之前 arboard 在这形状上必报 ConversionFailure。
        var d = DibSampleBuilder.Build(
            124, 2, 2, 32, DibSampleBuilder.BiRgb,
            (0, 0, 0, 0xff00_0000),
            [
                [0xff, 0x21, 0x1a, 0xff, 0x10, 0x20, 0x30, 0x80],
                [0x00, 0x00, 0xff, 0xff, 0x00, 0xff, 0x00, 0x00],
            ]);
        var r = DibDecoder.DecodeToRgba(d);

        Assert.NotNull(r);
        Assert.Equal((2, 2), (r!.Width, r.Height));
        // 视觉第一行 = 缓冲区的最后一行（自下而上）；BGRA(00,00,ff,ff) → RGBA(ff,00,00,ff)
        Assert.Equal(new byte[] { 0xff, 0x00, 0x00, 0xff }, r.Rgba[0..4]);
        // 第二行第二像素：BGRA(0x10,0x20,0x30,0x80) → RGBA(0x30,0x20,0x10,0x80)，alpha 要留住
        Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 0x80 }, r.Rgba[12..16]);
    }

    [Fact]
    public void 位域掩码接在40字节头后_也能解()
    {
        // CF_DIB 的形状：BITMAPINFOHEADER + BI_BITFIELDS + 12 字节掩码 + 像素
        var d = DibSampleBuilder.Build(
            40, 1, 1, 32, DibSampleBuilder.BiBitfields,
            (0x00ff_0000, 0x0000_ff00, 0x0000_00ff, 0),
            [[0x1a, 0x21, 0x22, 0xff]]);
        var r = DibDecoder.DecodeToRgba(d);

        Assert.NotNull(r);
        Assert.Equal(new byte[] { 0x22, 0x21, 0x1a, 0xff }, r!.Rgba);
    }

    [Fact]
    public void 二十四位BI_RGB按BGR读且行补齐被跳过()
    {
        // 宽 3 的 24bpp 行是 9 字节，要补到 12；补位不得混进像素
        byte[] row = [0, 1, 2, 10, 11, 12, 20, 21, 22]; // B,G,R ×3
        var d = DibSampleBuilder.Build(40, 3, 1, 24, DibSampleBuilder.BiRgb, (0, 0, 0, 0), [row]);
        var r = DibDecoder.DecodeToRgba(d);

        Assert.NotNull(r);
        Assert.Equal(3, r!.Width);
        Assert.Equal(new byte[] { 2, 1, 0, 255 }, r.Rgba[0..4]);
        Assert.Equal(new byte[] { 22, 21, 20, 255 }, r.Rgba[8..12]);
    }

    [Fact]
    public void 负高度是自上而下_行序不颠倒()
    {
        byte[][] rows = [[1, 0, 0, 255], [2, 0, 0, 255]];
        var up = DibSampleBuilder.Build(40, 1, -2, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0), rows);
        var down = DibSampleBuilder.Build(40, 1, 2, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0), rows);
        var a = DibDecoder.DecodeToRgba(up)!;
        var b = DibDecoder.DecodeToRgba(down)!;

        Assert.Equal(new byte[] { 0, 0, 1, 255 }, a.Rgba[0..4]); // 自上而下：第一行就是给的第一行
        Assert.Equal(new byte[] { 0, 0, 2, 255 }, b.Rgba[0..4]); // 自下而上：给的最后一行才是第一行
        // 两种方向的行序正好相反：a 的第二行就是 b 的第一行
        Assert.Equal(b.Rgba[0..4], a.Rgba[4..8]);
    }

    [Fact]
    public void 头40的32bppBI_RGB_无掩码按BGR补齐且alpha为255()
    {
        // CF_DIB 常态：BITMAPINFOHEADER + BI_RGB，掩码全零 → B,G,R 补齐，A=255
        var d = DibSampleBuilder.Build(40, 1, 1, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0),
            [[0x1a, 0x21, 0x22, 0xcc]]);
        var r = DibDecoder.DecodeToRgba(d);

        Assert.NotNull(r);
        // 无 alpha 掩码时第四字节强制 255（legacy a_field 缺省 255）
        Assert.Equal(new byte[] { 0x22, 0x21, 0x1a, 0xff }, r!.Rgba);
    }

    [Theory]
    [InlineData(52, 255)]  // V2 头没有 alpha 掩码字段：A 写不进头，legacy 强制 a_mask=0 → A=255
    [InlineData(56, 128)]
    [InlineData(108, 128)]
    [InlineData(124, 128)]
    public void V2到V5头_BI_BITFIELDS_掩码在头内_像素起点不加12字节(int headerSize, byte expectedAlpha)
    {
        // 工单重点：V4/V5 掩码本来就在头里，像素起点=头大小，
        // 不得再按 BITMAPINFOHEADER 的规矩在头后跳 12 字节（arboard/image 的 bug 正在此）
        var d = DibSampleBuilder.Build(
            headerSize, 1, 1, 32, DibSampleBuilder.BiBitfields,
            (0x00ff_0000, 0x0000_ff00, 0x0000_00ff, 0xff00_0000),
            [[0x1a, 0x21, 0x22, 0x80]]);
        var r = DibDecoder.DecodeToRgba(d);

        Assert.NotNull(r);
        Assert.Equal(new byte[] { 0x22, 0x21, 0x1a, expectedAlpha }, r!.Rgba);
    }

    [Fact]
    public void 五十六字节头_BI_RGB_带alpha掩码_alpha保留()
    {
        // V3(56)：alpha 掩码在头内 52..56；PixPin 旧形状之一
        var d = DibSampleBuilder.Build(
            56, 1, 1, 32, DibSampleBuilder.BiRgb,
            (0x00ff_0000, 0x0000_ff00, 0x0000_00ff, 0xff00_0000),
            [[0x1a, 0x21, 0x22, 0x7f]]);
        var r = DibDecoder.DecodeToRgba(d);

        Assert.NotNull(r);
        Assert.Equal(new byte[] { 0x22, 0x21, 0x1a, 0x7f }, r!.Rgba);
    }

    [Fact]
    public void 五十二字节头_BI_RGB_无alpha位_alpha强制255()
    {
        // V2(52)：legacy 规则——补齐 BGR 时若 header_size < 56 则 a_mask 置 0，A=255
        var d = DibSampleBuilder.Build(
            52, 1, 1, 32, DibSampleBuilder.BiRgb,
            (0x00ff_0000, 0x0000_ff00, 0x0000_00ff, 0x0000_0000),
            [[0x1a, 0x21, 0x22, 0xcc]]);
        var r = DibDecoder.DecodeToRgba(d);

        Assert.NotNull(r);
        Assert.Equal(new byte[] { 0x22, 0x21, 0x1a, 0xff }, r!.Rgba);
    }

    [Fact]
    public void 二十四位_负高度_下行序_正确翻转()
    {
        byte[][] rows =
        {
            [0, 0, 1, 0, 0, 2], // 视觉第一行（下行序）：R=1 / R=2
            [0, 0, 3, 0, 0, 4], // 视觉第二行：R=3 / R=4
        };
        var d = DibSampleBuilder.Build(40, 2, -2, 24, DibSampleBuilder.BiRgb, (0, 0, 0, 0), rows);
        var r = DibDecoder.DecodeToRgba(d)!;

        Assert.Equal(new byte[] { 1, 0, 0, 255, 2, 0, 0, 255 }, r.Rgba[0..8]);
        Assert.Equal(new byte[] { 3, 0, 0, 255, 4, 0, 0, 255 }, r.Rgba[8..16]);
    }
}
