using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// PNG 编码（T06 图片落盘）：WPF PngBitmapEncoder（WIC），像素源固定非预乘 Bgra32
/// （docs/adr/0005-DIB解码与PNG编码参数.md 第 3 条）。不追求与 legacy image crate
/// 字节级兼容（filter/压缩参数不同，哈希不同）；身份兼容由规范化像素比较维持。
/// Interlace 默认 Off，与 legacy png crate 默认一致。
/// </summary>
public static class PngEncoder
{
    /// <summary>把规范化 RGBA 像素（自上而下，每像素 R,G,B,A）编码为 PNG 字节。</summary>
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        // RGBA → BGRA（Bgra32 非预乘，WIC 编码器原样转 RGBA PNG）
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        var source = BitmapSource.Create(
            width, height, 96, 96,
            PixelFormats.Bgra32, null,
            bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
