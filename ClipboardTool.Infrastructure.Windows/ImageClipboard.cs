namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 剪贴板图片的 DIB → PNG 转换（T06 监听读侧）：解码（DibDecoder，ADR-0005 第 1/2 条）
/// 与编码（PngEncoder，第 3 条）的组合。调用约定：DIB 字节必须在剪贴板独占段内拷出，
/// 本转换在独占段之外执行（解码几十毫秒正是别人抢 OpenClipboard 的时刻，F10 硬约束）。
/// </summary>
public static class ImageClipboard
{
    /// <summary>DIB（CF_DIBV5/CF_DIB 载荷）→ PNG 字节；不支持的形状返回 null（拒绝面与 legacy 一致）。</summary>
    public static byte[]? PngFromDib(byte[] dib)
    {
        var decoded = DibDecoder.DecodeToRgba(dib);
        return decoded is null ? null : PngEncoder.Encode(decoded.Width, decoded.Height, decoded.Rgba);
    }
}
