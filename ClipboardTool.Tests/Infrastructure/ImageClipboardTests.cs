using ClipboardTool.Infrastructure.Windows;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 剪贴板 DIB → PNG 转换（T06 监听读侧）：解码与编码共用 ADR-0005 参数，
/// 是 ClipboardReader 在独占段外做的那个转换。像素必须无损往返。
/// </summary>
public class ImageClipboardTests
{
    [Fact]
    public void DIB解码后编码PNG_像素往返一致_含透明()
    {
        // PixPin 形状：V5 头 + BI_RGB + alphaMask，半透明像素
        var dib = DibSampleBuilder.Build(
            124, 2, 1, 32, DibSampleBuilder.BiRgb,
            (0, 0, 0, 0xff00_0000),
            [
                [0x10, 0x20, 0x30, 0x80, 0x00, 0x00, 0xff, 0xff],
            ]);
        var expected = DibDecoder.DecodeToRgba(dib)!;

        var png = ImageClipboard.PngFromDib(dib);

        Assert.NotNull(png);
        var decoded = DibDecoder.DecodePngPixels(png!)!;
        Assert.Equal((expected.Width, expected.Height), (decoded.Width, decoded.Height));
        Assert.Equal(expected.Rgba, decoded.Rgba); // 半透明 0x80 原样保留（Bgra32 非预乘）
    }

    [Fact]
    public void RLE压缩_转换失败返回null()
    {
        var dib = DibSampleBuilder.Build(
            40, 1, 1, 32, DibSampleBuilder.BiRle8, (0, 0, 0, 0), [[1, 2, 3, 4]]);
        Assert.Null(ImageClipboard.PngFromDib(dib));
    }

    [Fact]
    public void CORE头_转换失败返回null()
    {
        Assert.Null(ImageClipboard.PngFromDib(DibSampleBuilder.CoreHeader()));
    }

    [Fact]
    public void 空字节_返回null()
    {
        Assert.Null(ImageClipboard.PngFromDib([]));
    }
}
