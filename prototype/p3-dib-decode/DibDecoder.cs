using System.IO;
using System.Numerics;

namespace P3DibDecode;

/// <summary>解码结果：规范化 RGBA 像素（自上而下视觉行序，每像素 R,G,B,A）。</summary>
public sealed record DibDecodeResult(int Width, int Height, byte[] Rgba);

/// <summary>
/// DIB → RGBA 像素：剪贴板图片的解码判定。legacy dib.rs 的 C# 逐行移植
/// （docs/wpf-rewrite-kit/04-reference/legacy/tauri/src-tauri/src/dib.rs，基准提交 b05ba281）。
/// 为什么自己解：legacy 注释钉过——arboard 把 CF_DIBV5 直接喂 image 的 BMP 解码器，
/// 那个解码器在「BI_BITFIELDS + V4/V5 头」上把像素起点多算 12 字节（掩码本来就在头里），
/// 整幅图错 12 字节 → UnexpectedEof → 截图一条进不了历史。
/// 覆盖面：32bpp（BI_RGB / BI_BITFIELDS，含 alpha 掩码）、24bpp（BI_RGB）、BI_PNG 透传；
/// 调色板、16bpp、BI_JPEG、RLE、CMYK 一律 null，与改之前的实际覆盖面一致，不做隐性承诺。
/// </summary>
public static class DibDecoder
{
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;
    private const uint BiPng = 6;

    /// <summary>按通道位置取值并拉伸到 8 位。</summary>
    private readonly record struct Field(uint Shift, uint Bits);

    /// <summary>掩码必须是连续的一段 1，且不超过 16 位；否则视为坏头。</summary>
    private static Field? FieldOf(uint mask)
    {
        if (mask == 0)
            return null;
        uint shift = (uint)BitOperations.TrailingZeroCount(mask);
        uint bits = (uint)System.Numerics.BitOperations.PopCount(mask >> (int)shift);
        if (bits == 0 || bits > 16 || (((1u << (int)bits) - 1) << (int)shift) != mask)
            return null;
        return new Field(shift, bits);
    }


    private static byte Channel(uint px, Field f)
    {
        uint raw = (px >> (int)f.Shift) & ((1u << (int)f.Bits) - 1);
        return f.Bits >= 8
            ? (byte)(raw >> (int)(f.Bits - 8))
            : (byte)(raw * 255 / ((1u << (int)f.Bits) - 1));
    }

    private static uint U32At(byte[] b, int off)
    {
        if (off < 0 || off > b.Length - 4)
            return 0;
        return BitConverter.ToUInt32(b, off);
    }

    private static ushort U16At(byte[] b, int off)
    {
        if (off < 0 || off > b.Length - 2)
            return 0;
        return BitConverter.ToUInt16(b, off);
    }

    /// <summary>解 DIB 为 RGBA 像素；不是支持的形状就返回 null（不抛异常、不崩溃）。</summary>
    public static DibDecodeResult? DecodeToRgba(byte[] dib)
    {
        // BI_PNG 形状解为像素：透传字节经 PNG 解码后与直接解码等价（判据 1 的 BI_PNG 例）。
        if (U32At(dib, 16) == BiPng)
        {
            var png = TryExtractPng(dib);
            return png is null ? null : DecodePngPixels(png);
        }

        int headerSize = (int)U32At(dib, 0);
        // 只认 BITMAPINFOHEADER(40) 与 V2..V5(52/56/108/124)；CORE 头(12)不接
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || dib.Length < headerSize)
            return null;
        int width = (int)U32At(dib, 4);
        int rawHeight = (int)U32At(dib, 8);
        uint bitCount = U16At(dib, 14);
        uint compression = U32At(dib, 16);
        if (U16At(dib, 12) != 1 || width <= 0 || rawHeight == 0)
            return null;
        int height = Math.Abs(rawHeight);
        bool topDown = rawHeight < 0;

        // 掩码位置：BITMAPINFOHEADER 的 BI_BITFIELDS 把 12 字节掩码接在头后，V2 及以后在头里
        uint rMask, gMask, bMask, aMask;
        int pixelOffset;
        switch (headerSize)
        {
            case 40 when compression == BiBitfields:
                (rMask, gMask, bMask, aMask, pixelOffset) = (U32At(dib, 40), U32At(dib, 44), U32At(dib, 48), 0, 52);
                break;
            case 40:
                (rMask, gMask, bMask, aMask, pixelOffset) = (0, 0, 0, 0, 40);
                break;
            case 52:
                (rMask, gMask, bMask, aMask, pixelOffset) = (U32At(dib, 40), U32At(dib, 44), U32At(dib, 48), 0, 52);
                break;
            default:
                (rMask, gMask, bMask, aMask, pixelOffset) = (U32At(dib, 40), U32At(dib, 44), U32At(dib, 48), U32At(dib, 52), headerSize);
                break;
        }

        if (compression != BiRgb && compression != BiBitfields)
            return null;

        // 三个颜色掩码全缺（BI_RGB 的常态）就按 B,G,R 补齐；alpha 掩码只在 V3 及以后、
        // 且非零时才认——这正是 PixPin 这类截图的写法：compression 仍写 BI_RGB，
        // 但 bV5AlphaMask 是 0xff000000，第四个字节确实是 alpha。
        if (rMask == 0 && gMask == 0 && bMask == 0)
        {
            (rMask, gMask, bMask) = (0x00ff_0000, 0x0000_ff00, 0x0000_00ff);
            if (headerSize < 56)
                aMask = 0;
        }
        Field? rField = FieldOf(rMask);
        Field? gField = FieldOf(gMask);
        Field? bField = FieldOf(bMask);
        if (rField is null || gField is null || bField is null)
            return null;
        Field? aField = aMask == 0 ? null : FieldOf(aMask);
        if (aMask != 0 && aField is null)
            return null;

        if (bitCount != 24 && bitCount != 32)
            return null;

        // 行按 4 字节对齐；最后一行允许没有补齐的填充，免得把边界算死
        long rowBytes = ((long)width * bitCount + 31) / 32 * 4;
        long lastRowBytes = ((long)width * bitCount + 7) / 8;
        long needed = checked((height - 1) * rowBytes + lastRowBytes);
        if (pixelOffset > dib.Length || needed > dib.Length - pixelOffset)
            return null;
        var pixels = dib.AsSpan(pixelOffset, (int)needed);

        var rgba = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            // DIB 默认自下而上，负高度才是自上而下
            int srcY = topDown ? y : height - 1 - y;
            var row = pixels.Slice(srcY * (int)rowBytes);
            for (int x = 0; x < width; x++)
            {
                int dst = (y * width + x) * 4;
                if (bitCount == 24)
                {
                    int o = x * 3;
                    if (o + 3 <= row.Length)
                    {
                        rgba[dst] = row[o + 2];
                        rgba[dst + 1] = row[o + 1];
                        rgba[dst + 2] = row[o];
                        rgba[dst + 3] = 255;
                    }
                }
                else
                {
                    uint v = U32At(row.ToArray(), x * 4);
                    rgba[dst] = Channel(v, rField.Value);
                    rgba[dst + 1] = Channel(v, gField.Value);
                    rgba[dst + 2] = Channel(v, bField.Value);
                    rgba[dst + 3] = aField is { } af ? Channel(v, af) : (byte)255;
                }
            }
        }

        return new DibDecodeResult(width, height, rgba);
    }

    /// <summary>BI_PNG 形状：像素区就是一个完整 PNG 文件，验头后原样透传；否则 null。</summary>
    public static byte[]? TryExtractPng(byte[] dib)
    {
        int headerSize = (int)U32At(dib, 0);
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || dib.Length < headerSize)
            return null;
        if (U16At(dib, 12) != 1)
            return null;
        if (U32At(dib, 16) != BiPng)
            return null;
        if (dib.Length <= headerSize)
            return null;
        var png = dib[headerSize..];
        // 验 PNG 头与 IHDR 存在（legacy 用 PngDecoder::new 验证可解）
        return PngDecoderCanDecode(png) ? png : null;
    }

    /// <summary>WPF PNG 解码器能否解出（对应 legacy PngDecoder::new 的验证强度）。</summary>
    private static bool PngDecoderCanDecode(byte[] png)
    {
        try
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                new MemoryStream(png),
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.OnDemand);
            return decoder.Frames.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>PNG 字节 → RGBA 像素（WPF 解码；判据 1 的 BI_PNG 例与判据 3 规范化比较共用）。</summary>
    public static DibDecodeResult? DecodePngPixels(byte[] png)
    {
        try
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                new MemoryStream(png),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null)
                return null;
            // 统一转 BGRA32 再翻成 RGBA，避免源 PNG 色彩类型差异
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                frame, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            var bgra = new byte[w * h * 4];
            converted.CopyPixels(bgra, w * 4, 0);
            var rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = bgra[i + 3];
            }
            return new DibDecodeResult(w, h, rgba);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
