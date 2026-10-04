using System.Collections.Concurrent;
using System.IO;
using System.Text;
using ClipboardTool.Application;
using ClipboardTool.Domain.History;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 前台应用信息采集（F06，票 14）：legacy source_app.rs 的进程内移植——
/// GetForegroundWindow → PID → QueryFullProcessImageNameW 取 exe 路径、GetWindowTextW 取窗口标题、
/// 文件名主干作应用名、ExtractAssociatedIcon 提图标转 PNG data URL。
/// 图标按 exePath 进程内缓存（含失败负缓存），避免同一应用反复提取。
/// 任一环节失败静默降级：无前台/无 PID → null（未知来源）；exe 查不到 → 空串字段照常返回
/// （legacy 同样 Some(ForegroundAppInfo) 原样保留中间态）。采集永不抛出、快速返回——
/// 调用方在监听线程上，不得阻塞记录链路。
/// </summary>
public sealed class ForegroundSource : IForegroundSource
{
    /// <summary>exePath → data URL（null=提取失败的负缓存）。监听线程串行访问，并发容器只为测试线程安全。</summary>
    private readonly ConcurrentDictionary<string, string?> _iconCache = new();

    public SourceApp? Capture()
    {
        try
        {
            return CaptureCore();
        }
        catch (Exception)
        {
            // 采集任何意外失败都按「不可得」降级——丢来源不丢内容
            return null;
        }
    }

    private SourceApp? CaptureCore()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return null;
        }

        var exePath = GetExePath(pid);

        // 窗口标题（辅助识别，如浏览器标签页）
        var title = new StringBuilder(512);
        _ = NativeMethods.GetWindowTextW(hwnd, title, 512);

        var appName = exePath.Length == 0
            ? string.Empty
            : Path.GetFileNameWithoutExtension(exePath);

        var iconDataUrl = exePath.Length == 0 ? null : GetIconDataUrl(exePath);

        return new SourceApp(exePath, appName, title.ToString(), iconDataUrl);
    }

    /// <summary>进程 exe 完整路径（OpenProcess + QueryFullProcessImageNameW，可处理部分受保护进程）。</summary>
    private static string GetExePath(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }
        try
        {
            var buf = new StringBuilder(1024);
            var size = (uint)buf.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(handle, NativeMethods.PROCESS_NAME_WIN32, buf, ref size)
                ? buf.ToString(0, (int)size)
                : string.Empty;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>图标 data URL：缓存命中直接返回（含负缓存），未命中提取后写入缓存。</summary>
    private string? GetIconDataUrl(string exePath)
    {
        if (_iconCache.TryGetValue(exePath, out var cached))
        {
            return cached;
        }
        var icon = ExtractIconPng(exePath) is { } png
            ? "data:image/png;base64," + Convert.ToBase64String(png)
            : null;
        _iconCache[exePath] = icon;
        return icon;
    }

    /// <summary>ExtractAssociatedIcon 取关联图标 → PNG 字节；失败返回 null（负缓存）。</summary>
    private static byte[]? ExtractIconPng(string exePath)
    {
        // ExtractAssociatedIconW 的路径缓冲按定长 128 处理（含 NUL），超长截断（legacy 同款）
        var pathBuf = new StringBuilder(128);
        pathBuf.Append(exePath);
        ushort index = 0;
        var hinstance = NativeMethods.GetModuleHandleW(null);
        var hicon = NativeMethods.ExtractAssociatedIconW(hinstance, pathBuf, ref index);
        if (hicon == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            return HIconToPng(hicon);
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(hicon);
        }
    }

    /// <summary>HICON → PNG 字节：GetIconInfo 拿色层位图，GetDIBits 读 32bpp top-down BGRA 转 RGBA。</summary>
    private static byte[]? HIconToPng(IntPtr hicon)
    {
        var info = new NativeMethods.ICONINFO();
        if (!NativeMethods.GetIconInfo(hicon, ref info) || info.hbmColor == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var bm = new NativeMethods.BITMAP();
            if (NativeMethods.GetObjectW(info.hbmColor, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAP>(), ref bm) == 0
                || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            {
                return null;
            }
            var (width, height) = (bm.bmWidth, bm.bmHeight);

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var memDc = NativeMethods.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    // 负高度 = top-down 行序（PNG 自上而下，省一次翻转）
                    var bmi = new NativeMethods.BITMAPINFO
                    {
                        bmiHeader = new NativeMethods.BITMAPINFOHEADER
                        {
                            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                            biWidth = width,
                            biHeight = -height,
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = NativeMethods.BI_RGB,
                        },
                    };
                    var pixels = new byte[width * height * 4];
                    if (NativeMethods.GetDIBits(memDc, info.hbmColor, 0, (uint)height, pixels, ref bmi, NativeMethods.DIB_RGB_COLORS) == 0)
                    {
                        return null;
                    }

                    // BGRA → RGBA
                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                    }
                    // 整图 alpha 全 0（GDI 图标常不带 alpha）→ 按 mask 全不透明处理（legacy 同款）
                    var hasAlpha = false;
                    for (var i = 3; i < pixels.Length; i += 4)
                    {
                        if (pixels[i] != 0)
                        {
                            hasAlpha = true;
                            break;
                        }
                    }
                    if (!hasAlpha)
                    {
                        for (var i = 3; i < pixels.Length; i += 4)
                        {
                            pixels[i] = 255;
                        }
                    }

                    return PngEncoder.Encode(width, height, pixels);
                }
                finally
                {
                    _ = NativeMethods.DeleteDC(memDc);
                }
            }
            finally
            {
                _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        finally
        {
            _ = NativeMethods.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(info.hbmMask);
            }
        }
    }
}
