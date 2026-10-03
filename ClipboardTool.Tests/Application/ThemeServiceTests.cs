using ClipboardTool.Application;
using ClipboardTool.Domain.Settings;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// ThemeService：主题的单一权威（F26–F28）。三态偏好经它落盘并分派两条独立明暗判定——
/// 面板内容看应用模式键、托盘图标看任务栏键；落盘失败不推进偏好（可重试）；
/// 跟随系统时经代次化的重新读取响应系统广播，迟到的读取不覆盖手选。
/// </summary>
public class ThemeServiceTests
{
    private sealed class FakeSystemPort : ThemeService.ISystemThemePort
    {
        public uint? AppsUseLightTheme { get; set; } = 1; // 默认亮应用
        public uint? SystemUsesLightTheme { get; set; } = 1;
        public uint? ReadAppsUseLightTheme() => AppsUseLightTheme;
        public uint? ReadSystemUsesLightTheme() => SystemUsesLightTheme;
        public event Action? ThemeChanged;
        public void RaiseChanged() => ThemeChanged?.Invoke();
    }

    private sealed class FakePersistPort : ThemeService.IPersistPort
    {
        public bool Result { get; set; } = true;
        public ThemeKind? Saved { get; private set; }
        public bool SaveTheme(ThemeKind theme)
        {
            Saved = theme;
            return Result;
        }
    }

    private static (ThemeService Service, FakeSystemPort System, FakePersistPort Persist, List<bool> Panel, List<bool> Tray, List<string> Menus)
        Make(ThemeKind initial = ThemeKind.System)
    {
        var system = new FakeSystemPort();
        var persist = new FakePersistPort();
        var service = new ThemeService(system, persist, initial);
        var panel = new List<bool>();
        var tray = new List<bool>();
        var menus = new List<string>();
        service.PanelThemeChanged += dark => panel.Add(dark);
        service.TrayThemeChanged += dark => tray.Add(dark);
        service.MenuChanged += () => menus.Add("menu");
        return (service, system, persist, panel, tray, menus);
    }

    [Fact]
    public void 手选暗色_落盘_偏好推进_两路事件齐发()
    {
        var (service, _, persist, panel, tray, menus) = Make();

        var ok = service.SetTheme(ThemeKind.Dark);

        Assert.True(ok);
        Assert.Equal(ThemeKind.Dark, persist.Saved);
        Assert.Equal(ThemeKind.Dark, service.Preference);
        Assert.Equal([true], panel);
        Assert.Equal([true], tray);
        Assert.Single(menus);
    }

    [Fact]
    public void 跟随系统时_面板看应用键_托盘看任务栏键()
    {
        // 「自定义」模式：暗任务栏 + 亮应用 → 面板浅色、托盘白图，互不跟错
        var (service, system, _, panel, tray, _) = Make();
        system.AppsUseLightTheme = 1;
        system.SystemUsesLightTheme = 0;

        service.SetTheme(ThemeKind.System);

        Assert.Equal([false], panel);
        Assert.Equal([true], tray);
    }

    [Fact]
    public void 手动亮色_不读系统键_两路都浅()
    {
        var (service, system, _, panel, tray, _) = Make();
        system.AppsUseLightTheme = 0;
        system.SystemUsesLightTheme = 0;

        service.SetTheme(ThemeKind.Light);

        Assert.Equal([false], panel);
        Assert.Equal([false], tray);
    }

    [Fact]
    public void 落盘失败_偏好不推进_无事件_可重试()
    {
        var (service, _, persist, panel, tray, menus) = Make();
        persist.Result = false;

        var ok = service.SetTheme(ThemeKind.Dark);

        Assert.False(ok);
        Assert.Equal(ThemeKind.System, service.Preference);
        Assert.Empty(panel);
        Assert.Empty(tray);
        Assert.Empty(menus);

        persist.Result = true; // 重试成功
        Assert.True(service.SetTheme(ThemeKind.Dark));
        Assert.Equal([true], panel);
    }

    [Fact]
    public void 循环切换_亮到暗_走同一权威入口()
    {
        var (service, _, persist, _, _, _) = Make(ThemeKind.Light);

        Assert.True(service.Toggle());

        Assert.Equal(ThemeKind.Dark, service.Preference);
        Assert.Equal(ThemeKind.Dark, persist.Saved);
    }

    [Fact]
    public void 系统广播_跟随系统时经代次读取响应()
    {
        var (service, system, _, panel, tray, _) = Make();
        service.SetTheme(ThemeKind.System);
        panel.Clear();
        tray.Clear();
        system.AppsUseLightTheme = 0; // 系统切到暗色

        system.RaiseChanged();

        Assert.Equal([true], panel);
        Assert.Equal(ThemeKind.System, service.Preference);
    }

    [Fact]
    public void 系统广播_手动模式下不推进偏好()
    {
        var (service, system, persist, panel, _, _) = Make();
        service.SetTheme(ThemeKind.Light);
        panel.Clear();

        system.RaiseChanged();

        Assert.Empty(panel);
        Assert.Equal(ThemeKind.Light, service.Preference);
    }

    [Fact]
    public void 系统广播读取走代次_与手选共享同一真源()
    {
        // 广播链路经 ThemeSync 的 BeginRead/AcceptRead 落地（迟到读取由 ThemeSyncTests 钉死）；
        // 这里验证同一权威入口：广播后的重读与手选推进的是同一份偏好
        var (service, system, _, panel, tray, _) = Make();
        service.SetTheme(ThemeKind.System);
        panel.Clear();
        tray.Clear();

        // 亮任务栏 + 暗应用：托盘翻白图、面板保持浅色（两路各判各的）
        system.SystemUsesLightTheme = 1;
        system.AppsUseLightTheme = 1;
        system.RaiseChanged();

        Assert.Empty(panel); // 仍浅色：无变化不发事件
        Assert.Empty(tray);  // 键值未变：图标不重设

        system.AppsUseLightTheme = 0; // 应用模式切暗
        system.RaiseChanged();

        Assert.Equal([true], panel);
        Assert.Empty(tray); // 任务栏键没变：托盘无事件
        Assert.Equal(ThemeKind.System, service.Preference);
    }
}
