namespace P3DibDecode;

/// <summary>
/// 样例 DIB 字节构造器：移植 legacy dib.rs tests 模块的 dib() 蓝本
/// （docs/wpf-rewrite-kit/04-reference/legacy/tauri/src-tauri/src/dib.rs，基准提交 b05ba281）。
/// rows 按缓冲区的行序给出——自下而上的 DIB 里，给的最后一行才是视觉第一行。
/// </summary>
public static class DibSampleBuilder
{
    public const uint BiRgb = 0;
    public const uint BiBitfields = 3;
    public const uint BiPng = 6;
    public const uint BiRle8 = 1;
    public const uint BiRle4 = 2;
    public const uint BiJpeg = 4;

    /// <summary>按 legacy 蓝本构造 BITMAPINFOHEADER/V4/V5 形状的 DIB 字节。</summary>
    public static byte[] Build(
        int headerSize,
        int width,
        int height,
        ushort bitCount,
        uint compression,
        (uint R, uint G, uint B, uint A) masks,
        IReadOnlyList<byte[]> rows)
    {
        var buf = new byte[headerSize];
        WriteU32(buf, 0, (uint)headerSize);
        WriteU32(buf, 4, (uint)width);
        WriteU32(buf, 8, (uint)height);
        WriteU16(buf, 12, 1);
        WriteU16(buf, 14, bitCount);
        WriteU32(buf, 16, compression);
        // 掩码落在头里的位置（V2 及以后）；BITMAPINFOHEADER 时这里越界，改由下面接头后
        uint[] all = [masks.R, masks.G, masks.B, masks.A];
        for (int i = 0; i < all.Length; i++)
        {
            int off = 40 + i * 4;
            if (off + 4 <= buf.Length)
                WriteU32(buf, off, all[i]);
        }
        if (headerSize == 40 && compression == BiBitfields)
        {
            foreach (var m in new[] { masks.R, masks.G, masks.B })
                buf = [.. buf, .. BitConverter.GetBytes(m)];
        }
        foreach (var row in rows)
        {
            buf = [.. buf, .. row];
            // 行补齐到 4 字节
            int stride = (row.Length + 3) / 4 * 4;
            Array.Resize(ref buf, buf.Length + stride - row.Length);
        }
        return buf;
    }

    /// <summary>一行 BGRA 像素（32bpp）。</summary>
    public static byte[] BgraRow(params byte[] bgra) => bgra;

    /// <summary>一行 BGR 像素（24bpp）。</summary>
    public static byte[] BgrRow(params byte[] bgr) => bgr;

    /// <summary>造一个空的 BITMAPCOREHEADER(12)。</summary>
    public static byte[] CoreHeader()
    {
        var core = new byte[12];
        WriteU32(core, 0, 12);
        return core;
    }

    public static void WriteU32(byte[] buf, int off, uint v) =>
        BitConverter.GetBytes(v).CopyTo(buf, off);

    public static void WriteU16(byte[] buf, int off, ushort v) =>
        BitConverter.GetBytes(v).CopyTo(buf, off);
}
