using ClipboardTool.Domain.Settings;
using Xunit;

namespace ClipboardTool.Tests.Settings;

/// <summary>设置存档容错测试（F37；legacy settings.rs 测试语义逐条移植）。</summary>
public sealed class SettingsRulesTests
{
    [Fact]
    public void 写出使用camelCase键名_主题小写字面量()
    {
        var json = SettingsRules.Serialize(new AppSettings(true, "Ctrl+Alt+V", ThemeKind.Dark));
        Assert.Contains("\"autoStart\":true", json);
        Assert.DoesNotContain("auto_start", json);
        Assert.Contains("\"shortcut\":\"Ctrl+Alt+V\"", json);
        Assert.Contains("\"theme\":\"dark\"", json);
    }

    [Fact]
    public void 往返序列化保持意图快捷键与主题()
    {
        var original = new AppSettings(true, "Ctrl+Alt+V", ThemeKind.Light);
        var json = SettingsRules.Serialize(original);
        Assert.Equal(original, SettingsRules.Parse(json));
    }

    [Fact]
    public void 兼容误写的snake_case旧档()
    {
        var s = SettingsRules.Parse("""{"auto_start":true,"shortcut":"Ctrl+Alt+V"}""");
        Assert.True(s.AutoStart);
        Assert.Equal("Ctrl+Alt+V", s.Shortcut);
    }

    [Fact]
    public void 旧字段与未知键忽略_缺省回落默认值()
    {
        var s = SettingsRules.Parse("""{"autoStart":true,"elevatedPaste":true,"helperToken":"x"}""");
        Assert.True(s.AutoStart);
        Assert.Equal(SettingsRules.DefaultShortcut, s.Shortcut);
        Assert.Equal(ThemeKind.System, s.Theme);
    }

    [Fact]
    public void 主题值非法回落跟随系统_其余键不连带()
    {
        Assert.Equal(ThemeKind.Light, SettingsRules.Parse("""{"theme":"light"}""").Theme);
        Assert.Equal(ThemeKind.Dark, SettingsRules.Parse("""{"theme":"dark"}""").Theme);
        Assert.Equal(ThemeKind.System, SettingsRules.Parse("""{"theme":"Night"}""").Theme);
        var s = SettingsRules.Parse("""{"theme":"Night","autoStart":true,"shortcut":"Ctrl+Alt+B"}""");
        Assert.Equal(ThemeKind.System, s.Theme);
        Assert.True(s.AutoStart);
        Assert.Equal("Ctrl+Alt+B", s.Shortcut);
        Assert.Equal(ThemeKind.System, SettingsRules.Parse("""{"theme":true}""").Theme);
    }

    [Fact]
    public void autoStart写坏成字符串时_用auto_start兜底_其余键保住()
    {
        var s = SettingsRules.Parse("""{"theme":"light","autoStart":"yes","auto_start":true,"shortcut":"Ctrl+Alt+B"}""");
        Assert.Equal(ThemeKind.Light, s.Theme);
        Assert.True(s.AutoStart);
        Assert.Equal("Ctrl+Alt+B", s.Shortcut);
    }

    [Fact]
    public void 两键都为合法布尔时_autoStart优先()
    {
        Assert.False(SettingsRules.Parse("""{"autoStart":false,"auto_start":true}""").AutoStart);
        Assert.True(SettingsRules.Parse("""{"autoStart":true,"auto_start":false}""").AutoStart);
        Assert.True(SettingsRules.Parse("""{"auto_start":true}""").AutoStart);
        Assert.False(SettingsRules.Parse("""{"autoStart":"bad","auto_start":"bad"}""").AutoStart);
    }

    [Fact]
    public void 空快捷键归一为默认快捷键()
    {
        Assert.Equal(SettingsRules.DefaultShortcut, SettingsRules.Parse("""{"shortcut":""}""").Shortcut);
        Assert.Equal(SettingsRules.DefaultShortcut, SettingsRules.Parse("not json at all").Shortcut);
        Assert.False(SettingsRules.Parse("not json at all").AutoStart);
        Assert.Equal(ThemeKind.System, SettingsRules.Parse("not json at all").Theme);
    }

    [Fact]
    public void 非对象JSON与空文本回默认()
    {
        Assert.Equal(AppSettings.Default, SettingsRules.Parse("[1,2]"));
        Assert.Equal(AppSettings.Default, SettingsRules.Parse("null"));
        Assert.Equal(AppSettings.Default, SettingsRules.Parse(""));
        Assert.Equal(AppSettings.Default, SettingsRules.Parse(null));
    }

    [Fact]
    public void 默认值_开机启动关_默认呼出键_跟随系统()
    {
        Assert.False(AppSettings.Default.AutoStart);
        Assert.Equal("Control+Shift+V", AppSettings.Default.Shortcut);
        Assert.Equal(ThemeKind.System, AppSettings.Default.Theme);
    }

    [Fact]
    public void 主题三态标签唯一且非空()
    {
        var labels = new[] { ThemeLabels.System, ThemeLabels.Light, ThemeLabels.Dark };
        Assert.All(labels, l => Assert.False(string.IsNullOrEmpty(l)));
        Assert.Equal(3, labels.Distinct().Count());
    }
}
