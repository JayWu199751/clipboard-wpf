using ClipboardTool.Infrastructure.Windows;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// DibDecoder 拒绝面测试正式化（T06，P3 原型 DibDecodeRejectTests 迁入）。
/// 判据 1（BI_PNG 透传）与判据 4（掩码/长度/溢出校验拒绝畸形数据不崩溃）。
/// 拒绝用例与 legacy tests 对齐并扩充。
/// </summary>
public class DibDecodeRejectTests
{
    [Fact]
    public void BI_PNG_像素区原样透传()
    {
        // 内嵌 PNG：1×1 像素 (7,8,9,255)，用 WPF PngBitmapEncoder 生成
        var inner = PngEncodeOnePixel(7, 8, 9, 255);
        var d = DibSampleBuilder.Build(124, 1, 1, 32, DibSampleBuilder.BiPng, (0, 0, 0, 0), []);
        d = [.. d, .. inner];

        var passed = DibDecoder.TryExtractPng(d);
        Assert.NotNull(passed);
        Assert.Equal(inner, passed!);

        // 透传字节必须可解出像素（legacy 用 PngDecoder::new 验证可解性）
        var r = DibDecoder.DecodeToRgba(d);
        Assert.NotNull(r);
        Assert.Equal(new byte[] { 7, 8, 9, 255 }, r!.Rgba);
    }

    [Fact]
    public void BI_PNG_像素区不是PNG_拒绝()
    {
        var d = DibSampleBuilder.Build(124, 1, 1, 32, DibSampleBuilder.BiPng, (0, 0, 0, 0), []);
        d = [.. d, 0x89, 0x50, 0x4e]; // PNG 魔数前 3 字节就被截断
        Assert.Null(DibDecoder.TryExtractPng(d));
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void BI_PNG_像素区缺失_拒绝()
    {
        var d = DibSampleBuilder.Build(124, 1, 1, 32, DibSampleBuilder.BiPng, (0, 0, 0, 0), []);
        Assert.Null(DibDecoder.TryExtractPng(d));
    }

    [Theory]
    [InlineData(8, DibSampleBuilder.BiRgb)]   // 8bpp 调色板
    [InlineData(16, DibSampleBuilder.BiRgb)]  // 16bpp
    [InlineData(1, DibSampleBuilder.BiRgb)]   // 1bpp
    [InlineData(4, DibSampleBuilder.BiRgb)]   // 4bpp
    [InlineData(32, DibSampleBuilder.BiRle8)] // RLE8
    [InlineData(32, DibSampleBuilder.BiRle4)] // RLE4
    [InlineData(32, DibSampleBuilder.BiJpeg)] // BI_JPEG
    [InlineData(32, 5u)]                      // BI_CMYK(5)
    [InlineData(32, 11u)]                     // BI_CMYKRLE8(11)
    public void 不支持的格式与位深一律不接(ushort bitCount, uint compression)
    {
        var d = DibSampleBuilder.Build(40, 1, 1, bitCount, compression, (0, 0, 0, 0), [[1, 2, 3, 4]]);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void CORE头12字节不接()
    {
        Assert.Null(DibDecoder.DecodeToRgba(DibSampleBuilder.CoreHeader()));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(120)]
    [InlineData(128)]
    public void 头大小不在白名单_拒绝(uint headerSize)
    {
        // 白名单只有 40/52/56/108/124；120 不是 V4(108)/V5(124)，13/39/41/42 同理
        // 13/39 连 planes/bitCount 字段都放不下，第一关就该拒。
        var d = new byte[headerSize];
        DibSampleBuilder.WriteU32(d, 0, headerSize);
        if (headerSize >= 16)
        {
            DibSampleBuilder.WriteU16(d, 12, 1);
            DibSampleBuilder.WriteU16(d, 14, 32);
        }
        if (headerSize >= 20)
            DibSampleBuilder.WriteU32(d, 16, DibSampleBuilder.BiRgb);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void 掩码不连续_拒绝()
    {
        var d = DibSampleBuilder.Build(124, 1, 1, 32, DibSampleBuilder.BiBitfields,
            (0x00f0_00f0, 0, 0xff, 0), [[0, 0, 0, 0]]);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void 掩码超过16位_拒绝()
    {
        var d = DibSampleBuilder.Build(124, 1, 1, 32, DibSampleBuilder.BiBitfields,
            (0x00ff_ffff, 0x0000_ff00, 0x0000_00ff, 0), [[0, 0, 0, 0]]);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void alpha掩码坏_拒绝()
    {
        var d = DibSampleBuilder.Build(124, 1, 1, 32, DibSampleBuilder.BiBitfields,
            (0x00ff_0000, 0x0000_ff00, 0x0000_00ff, 0x00f0_00f0), [[0, 0, 0, 0]]);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void planes不等于1_拒绝()
    {
        var d = DibSampleBuilder.Build(40, 1, 1, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0),
            [[0, 0, 0, 255]]);
        DibSampleBuilder.WriteU16(d, 12, 2);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 宽度非正_拒绝(int width)
    {
        var d = DibSampleBuilder.Build(40, width, 1, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0),
            [[0, 0, 0, 255]]);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void 高度为0_拒绝()
    {
        var d = DibSampleBuilder.Build(40, 1, 0, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0), []);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void 像素数据不足时不分配也不崩()
    {
        // 声称 4000×4000，实际只给了头：必须在分配像素缓冲之前返回 None
        var d = new byte[124];
        DibSampleBuilder.WriteU32(d, 0, 124);
        DibSampleBuilder.WriteU32(d, 4, 4000);
        DibSampleBuilder.WriteU32(d, 8, 4000);
        DibSampleBuilder.WriteU16(d, 12, 1);
        DibSampleBuilder.WriteU16(d, 14, 32);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void 像素区截断一行_拒绝()
    {
        // 声称 2×2 32bpp（needed=16），像素区第二行只有 4 字节（4 字节对齐免补齐）：
        // 缓冲 40+8+4=52 < 40+16，必须拒绝而不是越界读
        var d = DibSampleBuilder.Build(40, 2, 2, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0),
            [[0, 0, 0, 255, 1, 1, 1, 255], [2, 2, 2, 255]]);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    [Fact]
    public void 最后一行无填充_仍可解()
    {
        // legacy 规则：needed 按最后一行实际字节算，允许最后缺 padding
        var d = DibSampleBuilder.Build(40, 1, 1, 24, DibSampleBuilder.BiRgb, (0, 0, 0, 0),
            [[1, 2, 3]]); // 3 字节，无 4 字节对齐填充
        var r = DibDecoder.DecodeToRgba(d);
        Assert.NotNull(r);
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, r!.Rgba);
    }

    [Fact]
    public void 空字节数组_拒绝不崩()
    {
        Assert.Null(DibDecoder.DecodeToRgba([]));
        Assert.Null(DibDecoder.TryExtractPng([]));
    }

    [Fact]
    public void 头声称比缓冲大_拒绝()
    {
        var d = DibSampleBuilder.Build(40, 1, 1, 32, DibSampleBuilder.BiRgb, (0, 0, 0, 0),
            [[0, 0, 0, 255]]);
        // 头改声称 124 但缓冲仍是 44 字节
        DibSampleBuilder.WriteU32(d, 0, 124);
        Assert.Null(DibDecoder.DecodeToRgba(d));
    }

    /// <summary>WPF PngBitmapEncoder 生成 1×1 PNG。</summary>
    internal static byte[] PngEncodeOnePixel(byte r, byte g, byte b, byte a)
    {
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            1, 1, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32, null,
            new byte[] { b, g, r, a }, 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
