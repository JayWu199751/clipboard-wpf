using System.Runtime.InteropServices;
using ClipboardTool.Domain.Geometry;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>物理像素矩形（虚拟屏幕坐标）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>单个显示器的度量：全屏与工作区矩形（物理像素、虚拟屏幕坐标）+ 有效 DPI。</summary>
/// <param name="MonitorRect">全屏矩形（物理像素）。</param>
/// <param name="WorkAreaRect">工作区矩形（物理像素，不含任务栏）。</param>
/// <param name="DpiScale">有效 DPI 缩放（96 = 1.0）。</param>
public sealed record ScreenMetrics(IntPtr Monitor, PixelRect MonitorRect, PixelRect WorkAreaRect, double DpiScale)
{
    /// <summary>工作区按该屏 DPI 折算成 DIP（PanelGeometry 的输入）。</summary>
    public WorkAreaDip WorkAreaDip => new(
        WorkAreaRect.Left / DpiScale,
        WorkAreaRect.Top / DpiScale,
        WorkAreaRect.Width / DpiScale,
        WorkAreaRect.Height / DpiScale);

    /// <summary>该屏 DIP 坐标 → 物理（虚拟屏幕）像素。</summary>
    public DipPoint ToPhysical(DipPoint dip) => new(dip.X * DpiScale, dip.Y * DpiScale);

    /// <summary>DIP 长度 → 该屏物理像素。</summary>
    public double ToPhysical(double dipLength) => dipLength * DpiScale;
}

/// <summary>显示器度量来源：光标所在屏、主屏、窗口所在屏。</summary>
public sealed class ScreenMetricsProvider
{
    public ScreenMetrics? GetCursorScreen()
    {
        if (!NativeMethods.GetCursorPos(out var point)) return null;
        return GetFromPoint(point.X, point.Y);
    }

    public ScreenMetrics? GetFromPoint(int x, int y) =>
        FromMonitor(NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST));

    public ScreenMetrics? GetPrimary() =>
        FromMonitor(NativeMethods.MonitorFromPoint(new NativeMethods.POINT(), NativeMethods.MONITOR_DEFAULTTOPRIMARY));

    public ScreenMetrics? GetFromWindow(IntPtr hwnd) =>
        FromMonitor(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));

    private static ScreenMetrics? FromMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;

        var info = new NativeMethods.MONITORINFOEXW
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>(),
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return null;

        var dpiScale = GetDpiScale(monitor);
        return new ScreenMetrics(
            monitor,
            new PixelRect(
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top),
            new PixelRect(
                info.rcWork.Left, info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top),
            dpiScale);
    }

    private static double GetDpiScale(IntPtr monitor)
    {
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) != 0 || dpiX == 0)
            return 1.0;
        return dpiX / 96.0;
    }
}
