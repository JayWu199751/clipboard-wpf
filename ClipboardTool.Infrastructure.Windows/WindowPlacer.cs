using ClipboardTool.Domain.Geometry;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 窗口落位。呼出几何在 Domain 算 DIP，这里做与目标屏 DPI 一致的物理换算。
/// 呼出落地按 legacy landing_verdict 契约回读 OS 真值判定（可见、矩形与意图相符、落在目标屏工作区内），
/// 失败重试一次；停靠只落位不做落地判定（屏外驻留是有意为之）。
/// </summary>
public static class WindowPlacer
{
    private const int TolerancePx = 2;

    /// <summary>停靠等不验落地的落位：DIP 意图 → 该屏物理像素。</summary>
    public static void PlaceDip(IntPtr hwnd, ScreenMetrics? screen, DipPoint positionDip, double widthDip, double heightDip)
    {
        if (screen is { } metrics)
        {
            var origin = metrics.ToPhysical(positionDip);
            _ = PlacePhysical(
                hwnd,
                (int)Math.Round(origin.X), (int)Math.Round(origin.Y),
                (int)Math.Round(metrics.ToPhysical(widthDip)), (int)Math.Round(metrics.ToPhysical(heightDip)));
        }
        else
        {
            // 无显示器兜底：DIP 即像素
            _ = PlacePhysical(hwnd, (int)positionDip.X, (int)positionDip.Y, (int)widthDip, (int)heightDip);
        }
    }

    /// <summary>呼出落地：按意图落位并回读验证；失败重试一次，仍失败返回 false（处置归调用方）。</summary>
    public static bool PlaceSummonDip(IntPtr hwnd, ScreenMetrics target, DipPoint positionDip, double widthDip, double heightDip)
    {
        var origin = target.ToPhysical(positionDip);
        var x = (int)Math.Round(origin.X);
        var y = (int)Math.Round(origin.Y);
        var width = (int)Math.Round(target.ToPhysical(widthDip));
        var height = (int)Math.Round(target.ToPhysical(heightDip));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            _ = PlacePhysical(hwnd, x, y, width, height);
            if (LandingVerdict(hwnd, target, x, y, width, height)) return true;
        }
        return false;
    }

    public static bool PlacePhysical(IntPtr hwnd, int x, int y, int width, int height) =>
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    public static (int Left, int Top, int Right, int Bottom) GetPhysicalRect(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return (0, 0, 0, 0);
        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>呼出落地三条件（legacy landing_verdict）：可见、位置尺寸与意图相符、整体落在目标屏工作区内。</summary>
    private static bool LandingVerdict(IntPtr hwnd, ScreenMetrics target, int x, int y, int width, int height)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;

        if (Math.Abs(rect.Left - x) > TolerancePx || Math.Abs(rect.Top - y) > TolerancePx
            || Math.Abs(rect.Right - rect.Left - width) > TolerancePx
            || Math.Abs(rect.Bottom - rect.Top - height) > TolerancePx)
        {
            return false;
        }

        return rect.Left >= target.WorkAreaRect.Left - TolerancePx
            && rect.Top >= target.WorkAreaRect.Top - TolerancePx
            && rect.Right <= target.WorkAreaRect.Right + TolerancePx
            && rect.Bottom <= target.WorkAreaRect.Bottom + TolerancePx;
    }
}
