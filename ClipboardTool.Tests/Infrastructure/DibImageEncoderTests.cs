using ClipboardTool.Infrastructure.Windows;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 写剪贴板的位图载荷（T06 复制链路）：PNG 文件 → DIBV5（保透明主格式）+ DIB（兼容旧读者）。
/// 载荷必须能被自家 DibDecoder 无损回解——「写出去的内容」与「读进来的内容」同一条解码规则。
/// </summary>
public class DibImageEncoderTests
{
    /// <summary>构造 2×1 半透明 PNG（WIC 编码，与落盘产物同参数）。</summary>
    private static byte[] SamplePng(out byte[] rgba)
    {
        rgba = [0x10, 0x20, 0x30, 0x80, 0x00, 0x00, 0xff, 0xff];
        return PngEncoder.Encode(2, 1, rgba);
    }

    [Fact]
    public void DIBV5载荷_可被DibDecoder回解出同像素_含透明()
    {
        var png = SamplePng(out var rgba);
        var dib = DibImageEncoder.BuildDibV5FromPng(png)!;

        var decoded = DibDecoder.DecodeToRgba(dib)!;
        Assert.Equal((2, 1), (decoded.Width, decoded.Height));
        Assert.Equal(rgba, decoded.Rgba); // 半透明 0x80 原样保留 → 粘贴保透明通道
    }

    [Fact]
    public void DIBV5头_字段形状正确()
    {
        var png = SamplePng(out _);
        var dib = DibImageEncoder.BuildDibV5FromPng(png)!;

        // BITMAPV5HEADER：头 124、2×1、planes 1、32bpp、BI_BITFIELDS(3)、
        // 高度为正（自下而上，最大兼容）、掩码 RGBA、CSType = 'Win '
        Assert.Equal(124u, BitConverter.ToUInt32(dib, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(dib, 4));
        Assert.Equal(1u, BitConverter.ToUInt32(dib, 8));
        Assert.Equal(1u, BitConverter.ToUInt16(dib, 12));
        Assert.Equal(32u, BitConverter.ToUInt16(dib, 14));
        Assert.Equal(3u, BitConverter.ToUInt32(dib, 16));
        Assert.Equal(8u, BitConverter.ToUInt32(dib, 20)); // sizeImage = 2×1 像素 × 4 字节
        Assert.Equal(0x00ff_0000u, BitConverter.ToUInt32(dib, 40)); // R
        Assert.Equal(0x0000_ff00u, BitConverter.ToUInt32(dib, 44)); // G
        Assert.Equal(0x0000_00ffu, BitConverter.ToUInt32(dib, 48)); // B
        Assert.Equal(0xff00_0000u, BitConverter.ToUInt32(dib, 52)); // A
        Assert.Equal(0x5769_6e20u, BitConverter.ToUInt32(dib, 56)); // 'Win '
        // 像素紧跟 V5 头（掩码已在头内，不得再跳 12 字节——arboard 的 bug 形状）
        Assert.Equal(124 + 2 * 4, dib.Length);
    }

    [Fact]
    public void DIB载荷_40头32bpp_可被回解出同像素()
    {
        var png = SamplePng(out var rgba);
        var dib = DibImageEncoder.BuildDibFromPng(png)!;

        var decoded = DibDecoder.DecodeToRgba(dib)!;
        Assert.Equal((2, 1), (decoded.Width, decoded.Height));
        // 40 头 BI_RGB 无 alpha 掩码字段：legacy 规则强制 A=255（半透明像素 alpha 语义
        // 由 DIBV5 主格式承担，40 头只是兼容格式）；颜色通道必须原样
        Assert.Equal(rgba[0], decoded.Rgba[0]);
        Assert.Equal(rgba[1], decoded.Rgba[1]);
        Assert.Equal(rgba[2], decoded.Rgba[2]);
        Assert.Equal(255, decoded.Rgba[3]);
        Assert.Equal(rgba[4..8], decoded.Rgba[4..8]);
        // BITMAPINFOHEADER：40、BI_RGB(0)
        Assert.Equal(40u, BitConverter.ToUInt32(dib, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(dib, 16));
    }

    [Fact]
    public void 坏PNG_编码失败返回null()
    {
        Assert.Null(DibImageEncoder.BuildDibV5FromPng("not-png"u8.ToArray()));
        Assert.Null(DibImageEncoder.BuildDibFromPng("not-png"u8.ToArray()));
    }
}
