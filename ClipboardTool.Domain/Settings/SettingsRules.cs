using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipboardTool.Domain.Settings;

/// <summary>主题偏好三态（legacy 术语表「主题偏好」）：存档里是小写字面量，缺省跟随系统。</summary>
public enum ThemeKind
{
    System,
    Light,
    Dark,
}

/// <summary>主题三态中文名（菜单与诊断用，与 legacy settings.rs Theme::label 一致）。</summary>
public static class ThemeLabels
{
    public const string System = "跟随系统";
    public const string Light = "亮色";
    public const string Dark = "暗色";

    public static string Of(ThemeKind theme) => theme switch
    {
        ThemeKind.Light => Light,
        ThemeKind.Dark => Dark,
        _ => System,
    };
}

/// <summary>应用设置（存档三键 autoStart/shortcut/theme；02-spec/02 §1）。</summary>
public sealed record AppSettings(bool AutoStart, string Shortcut, ThemeKind Theme)
{
    public static AppSettings Default { get; } = new(false, SettingsRules.DefaultShortcut, ThemeKind.System);
}

/// <summary>
/// 设置存档解析与写出唯一归属（F37；legacy settings.rs 移植）。
/// 键名契约：写出 camelCase（autoStart），读取兼容误写的 auto_start；
/// 两者都是合法布尔时 autoStart 优先。theme 只认小写 light/dark/system，缺失/非法回跟随系统；
/// shortcut 缺失/空串回默认，不把合法自定义字符串悄悄改名。
/// 字段类型坏不能连带丢其他合法字段（逐键宽容吸收，坏档退化为默认但不扩散）。
/// </summary>
public static class SettingsRules
{
    public const string DefaultShortcut = "Control+Shift+V";

    /// <summary>解析存档文本：任何情况下不抛错、不失败——非对象/不可解析一律回默认设置。</summary>
    public static AppSettings Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return AppSettings.Default;
        }
        JsonObject? obj = null;
        try
        {
            obj = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            // 整体不可解析 → 默认设置（调用方可选择先备份原件）
        }
        if (obj is null)
        {
            return AppSettings.Default;
        }

        // 取第一个真正是布尔的键：camelCase 优先，误写的 snake_case 兜底
        var autoStart = FirstBool(obj, "autoStart") ?? FirstBool(obj, "auto_start") ?? false;
        var shortcut = obj["shortcut"] is JsonValue shortcutNode
            && shortcutNode.TryGetValue(out string? shortcutValue)
                ? shortcutValue
                : string.Empty;
        var theme = obj["theme"] is JsonValue themeNode && themeNode.TryGetValue(out string? themeValue)
            ? ParseTheme(themeValue)
            : ThemeKind.System;
        return Normalize(new AppSettings(autoStart, shortcut, theme));
    }

    /// <summary>判定：存档文本 → 主题偏好。只认三个小写字面量，其余回落跟随系统。</summary>
    public static ThemeKind ParseTheme(string raw) => raw switch
    {
        "light" => ThemeKind.Light,
        "dark" => ThemeKind.Dark,
        _ => ThemeKind.System,
    };

    /// <summary>写出（存档键名契约）：camelCase、主题小写字面量、从不写 auto_start；
    /// 转义与 legacy serde_json 同形（快捷键里的 + 原样保留）。</summary>
    public static string Serialize(AppSettings settings) => new JsonObject
    {
        ["autoStart"] = settings.AutoStart,
        ["shortcut"] = settings.Shortcut,
        ["theme"] = ThemeLiteral(settings.Theme),
    }.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static AppSettings Normalize(AppSettings settings) =>
        settings.Shortcut.Length == 0
            ? settings with { Shortcut = DefaultShortcut }
            : settings;

    private static bool? FirstBool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out bool parsed) ? parsed : null;

    private static string ThemeLiteral(ThemeKind theme) => theme switch
    {
        ThemeKind.Light => "light",
        ThemeKind.Dark => "dark",
        _ => "system",
    };
}
