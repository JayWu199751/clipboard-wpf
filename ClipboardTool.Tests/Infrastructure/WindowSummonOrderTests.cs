using System.Runtime.InteropServices;
using ClipboardTool.Domain.Geometry;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

public sealed class WindowSummonOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 被遮挡的面板呼出后实际在前且不抢焦点(bool panelStillMarkedTopmost)
    {
        var oldDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr panel = IntPtr.Zero, cover = IntPtr.Zero;
        try
        {
            var screen = new ScreenMetricsProvider().GetPrimary()!;
            var position = new DipPoint(screen.WorkAreaDip.X + 80, screen.WorkAreaDip.Y + 80);
            var origin = screen.ToPhysical(position);
            var x = (int)Math.Round(origin.X);
            var y = (int)Math.Round(origin.Y);
            var width = (int)Math.Round(screen.ToPhysical(160));
            var height = (int)Math.Round(screen.ToPhysical(120));
            panel = CreateWindowExW(0x08000080, "BUTTON", "summon-order-panel", 0x80000000,
                x, y, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            cover = CreateWindowExW(0x08000080, "BUTTON", "summon-order-cover", 0x80000000,
                x, y, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, panel);
            Assert.NotEqual(IntPtr.Zero, cover);
            var foreground = NativeMethods.GetForegroundWindow();
            Assert.True(NativeMethods.SetWindowPos(panel, new IntPtr(panelStillMarkedTopmost ? -1 : -2),
                x, y, width, height, 0x50)); // SHOWWINDOW | NOACTIVATE
            Assert.True(NativeMethods.SetWindowPos(cover, new IntPtr(-1), x, y, width, height, 0x50));
            var center = new NativeMethods.POINT { X = x + width / 2, Y = y + height / 2 };
            Assert.Equal(cover, WindowFromPoint(center)); // 与故障相同：可见矩形正确，实际被遮挡。

            Assert.True(WindowPlacer.PlaceSummonDip(panel, screen, position, 160, 120));

            Assert.Equal(panel, WindowFromPoint(center));
            Assert.Equal(foreground, NativeMethods.GetForegroundWindow());
            Assert.NotEqual(0, NativeMethods.GetWindowLongPtrW(panel, -20).ToInt64() & 8);
        }
        finally
        {
            if (cover != IntPtr.Zero) DestroyWindow(cover);
            if (panel != IntPtr.Zero) DestroyWindow(panel);
            SetThreadDpiAwarenessContext(oldDpi);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
