using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 托盘 PNG → HICON 的真实工厂（F29；TrayIconSync.ITrayIconFactory 实现）。
/// 图标不是一张图缩放出来的：Assets/Tray/ 下 16/20/24/28/32 五档 × 亮暗两套，按
/// 「恰好物理尺寸」取档零重采样（非整数缩放下 1:1 才不糊）；找不到对应档位回退 32px 基图。
/// HICON 用 BGRA32 非预乘 + CreateIconIndirect 落成（alpha 保留，托盘合成器认 32bpp alpha）；
/// 调用方（TrayIconSync）负责 Destroy 旧句柄，本类只管生成。
/// </summary>
public sealed class PngIconFactory : TrayIconSync.ITrayIconFactory
{
    private const string ResourceBase = "ClipboardTool.Infrastructure.Windows.Assets.Tray.";

    public IntPtr? Load(bool dark, int size)
    {
        // 深色任务栏用浅色（白色）描边图：dark 对应 light 资产（与 legacy icon_image 同口径）
        var stem = dark ? "tray-icon-light" : "tray-icon";
        var bytes = ReadEmbeddedResource($"{ResourceBase}{stem}-{size}.png")
            ?? ReadEmbeddedResource($"{ResourceBase}{stem}.png"); // 缺档回退 32px 基图
        if (bytes is null)
        {
            return null;
        }
        return HiconFromPng(bytes);
    }

    public void Destroy(IntPtr hicon)
    {
        if (hicon != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyIcon(hicon);
        }
    }

    /// <summary>读嵌入资源；缺失返回 null（托盘降级为旧图，不崩）。</summary>
    private static byte[]? ReadEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>PNG 字节 → HICON：WIC 解码为 BGRA32 非预乘（P3 结论：预乘不可逆），GDI 落图标。</summary>
    private static IntPtr? HiconFromPng(byte[] png)
    {
        BitmapSource source;
        try
        {
            source = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception)
        {
            return null;
        }
        var converted = new FormatConvertedBitmap(
            source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var colorBits = NativeMethods.CreateGDIBitmap(width, height, pixels);
        if (colorBits == IntPtr.Zero)
        {
            return null;
        }
        // 掩码位图：32bpp BGRA 自带 alpha，掩码全 0（opaque）即可；CreateIconIndirect 合成
        var mask = NativeMethods.CreateGDIMask(width, height);
        if (mask == IntPtr.Zero)
        {
            _ = NativeMethods.DeleteObject(colorBits);
            return null;
        }

        var info = new NativeMethods.ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = mask,
            hbmColor = colorBits,
        };
        var hicon = NativeMethods.CreateIconIndirect(ref info);
        _ = NativeMethods.DeleteObject(colorBits);
        _ = NativeMethods.DeleteObject(mask);
        return hicon != IntPtr.Zero ? hicon : null;
    }
}
