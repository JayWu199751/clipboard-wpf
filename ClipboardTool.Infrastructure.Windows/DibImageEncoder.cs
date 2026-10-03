using System.Buffers.Binary;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 写剪贴板的位图载荷（T06 复制链路，F11：复制图片 = 写位图内容，不是文件路径）：
/// PNG 文件字节 → DIBV5（124 头 + BI_BITFIELDS + RGBA 掩码，alpha 完整保留，
/// 截图工具/Office 读它保透明）与 DIB（40 头 + 32bpp BI_RGB，兼容只认旧格式的读者）。
/// 行序自下而上（高度为正），像素紧跟头（V5 掩码在头内，不得再跳 12 字节）。
/// 解码复用 DibDecoder——「写出去的」与「读进来的」是同一条解码规则，往返必须无损。
/// </summary>
public static class DibImageEncoder
{
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;

    /// <summary>BGRA 像素行缓冲（自下而上）：RGB 平面共用。</summary>
    private static byte[]? DecodeToBgraRows(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;
        var decoded = DibDecoder.DecodePngPixels(png);
        if (decoded is null)
        {
            return null;
        }
        width = decoded.Width;
        height = decoded.Height;
        // RGBA → BGRA（解码产物的重排逆运算）
        var bgra = new byte[decoded.Rgba.Length];
        for (int i = 0; i < decoded.Rgba.Length; i += 4)
        {
            bgra[i] = decoded.Rgba[i + 2];
            bgra[i + 1] = decoded.Rgba[i + 1];
            bgra[i + 2] = decoded.Rgba[i];
            bgra[i + 3] = decoded.Rgba[i + 3];
        }
        // 自下而上：视觉第一行放缓冲最后
        int stride = width * 4;
        var rows = new byte[bgra.Length];
        for (int y = 0; y < height; y++)
        {
            var src = bgra.AsSpan(y * stride, stride);
            src.CopyTo(rows.AsSpan((height - 1 - y) * stride));
        }
        return rows;
    }

    /// <summary>PNG → DIBV5 载荷（SetClipboardData(CF_DIBV5) 用）；解不出返回 null。</summary>
    public static byte[]? BuildDibV5FromPng(byte[] png)
    {
        var rows = DecodeToBgraRows(png, out int width, out int height);
        if (rows is null)
        {
            return null;
        }
        var dib = new byte[124 + rows.Length];
        WriteU32(dib, 0, 124);                 // bV5Size
        WriteU32(dib, 4, (uint)width);         // bV5Width
        WriteU32(dib, 8, (uint)height);        // bV5Height（正 = 自下而上）
        WriteU16(dib, 12, 1);                  // bV5Planes
        WriteU16(dib, 14, 32);                 // bV5BitCount
        WriteU32(dib, 16, BiBitfields);        // bV5Compression
        WriteU32(dib, 20, (uint)rows.Length);  // bV5SizeImage
        WriteU32(dib, 40, 0x00ff_0000);        // bV5RedMask
        WriteU32(dib, 44, 0x0000_ff00);        // bV5GreenMask
        WriteU32(dib, 48, 0x0000_00ff);        // bV5BlueMask
        WriteU32(dib, 52, 0xff00_0000);        // bV5AlphaMask
        WriteU32(dib, 56, 0x5769_6e20);        // bV5CSType = 'Win '（LCS_WINDOWS_COLOR_SPACE）
        rows.CopyTo(dib, 124);
        return dib;
    }

    /// <summary>PNG → DIB 载荷（SetClipboardData(CF_DIB) 用，40 头 32bpp BI_RGB）；解不出返回 null。</summary>
    public static byte[]? BuildDibFromPng(byte[] png)
    {
        var rows = DecodeToBgraRows(png, out int width, out int height);
        if (rows is null)
        {
            return null;
        }
        var dib = new byte[40 + rows.Length];
        WriteU32(dib, 0, 40);                  // biSize
        WriteU32(dib, 4, (uint)width);
        WriteU32(dib, 8, (uint)height);
        WriteU16(dib, 12, 1);
        WriteU16(dib, 14, 32);
        WriteU32(dib, 16, BiRgb);
        WriteU32(dib, 20, (uint)rows.Length);  // biSizeImage
        rows.CopyTo(dib, 40);
        return dib;
    }

    private static void WriteU32(byte[] buf, int off, uint v) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), v);

    private static void WriteU16(byte[] buf, int off, ushort v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), v);
}
