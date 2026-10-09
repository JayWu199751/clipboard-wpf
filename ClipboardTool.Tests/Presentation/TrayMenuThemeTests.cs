using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardTool.Application;
using ClipboardTool.Domain.Settings;
using ClipboardTool.Infrastructure.Windows;
using ClipboardTool.Presentation.Wpf;
using ClipboardTool.Tests.Accessibility;

namespace ClipboardTool.Tests.Presentation;

public class TrayMenuThemeTests
{
    static TrayMenuThemeTests()
    {
        // 测试宿主未创建 WPF Application；初始化其 pack 资源协议，不创建全局应用实例。
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
    }

    [Theory]
    [InlineData("Dark", "#FF141419", "#FFF2F2F5")]
    [InlineData("Light", "#FFFFFFFF", "#FF1A1A20")]
    public void 实际托盘菜单与主题子菜单背景文字匹配应用皮肤(string theme, string background, string foreground)
    {
        Sta.Run(() =>
        {
            using var menu = CreateMenu(theme);
            Prepare(menu);
            AssertColor(background, menu.Background);
            AssertColor(foreground, menu.Foreground);
            var parent = (MenuItem)menu.Items[1];
            AssertColor(foreground, parent.Foreground);
            var child = (MenuItem)parent.Items[0];
            AssertColor(foreground, child.Foreground);
            var popup = (Popup)parent.Template.FindName("PART_Popup", parent);
            AssertColor(background, ((Border)popup.Child).Background);
            Assert.Same(menu.FindResource("Brush.Border.Strong"), ((Border)popup.Child).BorderBrush);
        });
    }

    [Fact]
    public void 应用皮肤替换后已有菜单与子菜单同步更新()
    {
        Sta.Run(() =>
        {
            var resources = LoadTheme("Dark");
            using var menu = new TrayContextMenu(resources);
            menu.SetItems(Spec(), _ => { });
            Prepare(menu);
            AssertColor("#FF141419", menu.Background);
            resources.MergedDictionaries[0] = ThemeDictionary("Light");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            AssertColor("#FFFFFFFF", menu.Background);
            AssertColor("#FF1A1A20", ((MenuItem)menu.Items[0]).Foreground);
            var parent = (MenuItem)menu.Items[1];
            var popup = (Popup)parent.Template.FindName("PART_Popup", parent);
            AssertColor("#FFFFFFFF", ((Border)popup.Child).Background);
            AssertColor("#FF1A1A20", ((MenuItem)parent.Items[0]).Foreground);
        });
    }

    [Fact]
    public void 跟随系统时菜单跟应用模式_任务栏相反也不改变菜单皮肤()
    {
        Sta.Run(() =>
        {
            var theme = new ThemeService(new OppositeSystemTheme(), new NoopPersist(), ThemeKind.System);
            theme.RefreshFromSystem();
            Assert.True(theme.IsPanelDark);
            Assert.False(theme.IsTrayDark);
            using var menu = CreateMenu(theme.IsPanelDark ? "Dark" : "Light");
            Prepare(menu);
            AssertColor("#FF141419", menu.Background);
        });
    }

    [Fact]
    public void 保留分隔线勾选与子菜单_点击只分发一次原命令()
    {
        Sta.Run(() =>
        {
            using var menu = new TrayContextMenu(LoadTheme("Dark"));
            var commands = new List<string>();
            menu.SetItems(Spec(), commands.Add);
            Assert.Equal(3, menu.Items.Count);
            Assert.IsType<Separator>(menu.Items[2]);
            Assert.True(((MenuItem)menu.Items[0]).IsChecked);
            var parent = (MenuItem)menu.Items[1];
            Assert.Equal("主题", parent.Header);
            var child = (MenuItem)Assert.Single(parent.Items.Cast<object>());
            Assert.True(child.IsChecked);
            child.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(["theme-dark"], commands);
            Assert.False(menu.IsOpen);
            menu.SetItems([new("show", "显示剪贴板面板")], commands.Add);
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(["theme-dark", "show"], commands);
        });
    }

    private static TrayContextMenu CreateMenu(string theme)
    {
        var menu = new TrayContextMenu(LoadTheme(theme));
        menu.SetItems(Spec(), _ => { });
        return menu;
    }

    private static TrayMenuItem[] Spec() =>
    [
        new("autostart", "开机启动", Checked: true),
        new("theme", "主题", SubItems: [new("theme-dark", "暗色", Checked: true)]),
        new("sep", Separator: true),
    ];

    private static ResourceDictionary LoadTheme(string name)
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(ThemeDictionary(name));
        resources["Font.Sans"] = new FontFamily("Segoe UI");
        return resources;
    }

    private static ResourceDictionary ThemeDictionary(string name) => new()
    {
        Source = new Uri($"/ClipboardTool;component/Themes/{name}.xaml", UriKind.Relative),
    };

    private static void Prepare(TrayContextMenu menu)
    {
        menu.Measure(new Size(400, 400));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.ApplyTemplate();
            foreach (var child in item.Items.OfType<MenuItem>())
            {
                child.ApplyTemplate();
            }
        }
    }

    private static void AssertColor(string expected, Brush actual) =>
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(actual).Color.ToString());

    private sealed class OppositeSystemTheme : ThemeService.ISystemThemePort
    {
        public uint? ReadAppsUseLightTheme() => 0;
        public uint? ReadSystemUsesLightTheme() => 1;
        public event Action? ThemeChanged { add { } remove { } }
    }

    private sealed class NoopPersist : ThemeService.IPersistPort
    {
        public bool SaveTheme(ThemeKind theme) => true;
    }
}
