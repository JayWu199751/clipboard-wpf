using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardTool.Presentation.Wpf;
using ClipboardTool.Infrastructure.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
        if (args.Contains("--smoke"))
        {
            Smoke();
            return;
        }
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../evidence"));
        Directory.CreateDirectory(output);
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/ClipboardTool;component/Themes/{theme}.xaml", UriKind.Relative),
            });
            resources["Font.Sans"] = new FontFamily("Inter, Segoe UI");
            using var menu = new TrayContextMenu(resources);
            menu.SetItems(
            [
                new("show", "显示剪贴板面板"),
                new("shortcut", "更换呼出快捷键（Control+Shift+V）"),
                new("sep1", Separator: true),
                new("autostart", "开机启动", Checked: true),
                new("theme", $"主题（{(theme == "Dark" ? "暗色" : "亮色")}）", SubItems:
                [
                    new("theme-system", "跟随系统"),
                    new("theme-light", "亮色", Checked: theme == "Light"),
                    new("theme-dark", "暗色", Checked: theme == "Dark"),
                ]),
                new("clear", "清空历史"),
                new("sep2", Separator: true),
                new("quit", "退出"),
            ], _ => { });
            Layout(menu);
            var themeItem = (MenuItem)menu.Items[4];
            themeItem.ApplyTemplate();
            var popup = (Popup)themeItem.Template.FindName("PART_Popup", themeItem);
            var submenu = (FrameworkElement)popup.Child;
            Layout(submenu);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(new VisualBrush(menu), null, new Rect(new Point(), menu.DesiredSize));
                context.DrawRectangle(new VisualBrush(submenu), null,
                    new Rect(new Point(menu.DesiredSize.Width + 8, 112), submenu.DesiredSize));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.DesiredSize.Width + submenu.DesiredSize.Width + 8),
                (int)Math.Ceiling(Math.Max(menu.DesiredSize.Height, 112 + submenu.DesiredSize.Height)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(output, $"tray-menu-{theme.ToLowerInvariant()}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine(path);
        }
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(800, 800));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    // 隔离桌面 smoke：真实托盘消息 → 宿主 → WPF Popup，不修改用户设置，不重启已有实例。
    private static void Smoke()
    {
        var foreground = GetForegroundWindow();
        try
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/ClipboardTool;component/Themes/{theme}.xaml", UriKind.Relative),
                });
                resources["Font.Sans"] = new FontFamily("Segoe UI");
                using var menu = new TrayContextMenu(resources);
                using var host = new TrayIconHost("ClipboardTool 主题验证", menu.Show);
                var commands = new List<string>();
                host.MenuItemSelected += commands.Add;
                host.SetMenuItems([new("show", "显示剪贴板面板"),
                    new("theme", "主题", SubItems: [new("theme-dark", "暗色", Checked: true)])]);
                var hwnd = (IntPtr)typeof(TrayIconHost).GetField("_hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                SendMessageW(hwnd, 0x8001, IntPtr.Zero, new IntPtr(0x0205));
                Pump();
                Check(menu.IsOpen, $"{theme}: 原生托盘右键消息打开 WPF 菜单");
                Check(menu.Background == resources["Brush.Card"], $"{theme}: 弹出菜单接入应用背景");
                var parent = (MenuItem)menu.Items[1];
                parent.Focus();
                var source = (HwndSource)PresentationSource.FromVisual(menu);
                SendMessageW(source.Handle, 0x0100, new IntPtr(0x27), IntPtr.Zero); // Right
                Pump();
                Check(parent.IsSubmenuOpen, $"{theme}: 右方向键展开主题子菜单");
                var child = (MenuItem)parent.Items[0];
                child.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Pump();
                Check(!menu.IsOpen && commands.SequenceEqual(["theme-dark"]), $"{theme}: 子菜单命令分发一次并关闭菜单");
                SendMessageW(hwnd, 0x8001, IntPtr.Zero, new IntPtr(0x0205));
                Pump();
                source = (HwndSource)PresentationSource.FromVisual(menu);
                SendMessageW(source.Handle, 0x0100, new IntPtr(0x1B), IntPtr.Zero); // Escape
                Pump();
                Check(!menu.IsOpen, $"{theme}: Esc 关闭菜单");
            }
        }
        finally
        {
            SetForegroundWindow(foreground);
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(label);
        }
        Console.WriteLine($"PASS {label}");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
