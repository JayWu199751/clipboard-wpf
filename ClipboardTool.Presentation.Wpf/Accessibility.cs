// F48 无障碍语义真源（T09）：可访问名称常量、toast 播报文本、减少动画策略。
// 本文件零视觉依赖（不含控件/模板），供 XAML 以 x:Static/Binding 引用、供 ClipboardTool.Tests 无头断言。

using System.Windows;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>
/// 交互元素可访问名称（F48）：UIA 树里的 Name 属性真源。XAML 侧用 x:Static 引用同一常量，
/// 名称变更只动这里一处；测试钉住非空、去重，避免出现「按钮无名」回归。
/// </summary>
public static class AccessibilityNames
{
    /// <summary>历史列表（ListBox 自带 SelectionItemPattern，名称补齐列表语义）。</summary>
    public const string HistoryList = "剪贴板历史列表";

    /// <summary>搜索输入框（浏览态只读、搜索态可输入，同一元素）。</summary>
    public const string SearchInput = "搜索剪贴板历史";

    /// <summary>搜索清除按钮（纯图形无文本，必须显式命名）。</summary>
    public const string SearchClear = "清除搜索关键词";

    /// <summary>搜索井内主题切换按钮（纯图形无文本，必须显式命名；三态循环 F26）。</summary>
    public const string ThemeToggle = "切换主题";

    /// <summary>内联备注编辑框。</summary>
    public const string NoteEditor = "编辑备注";

    /// <summary>换键捕获状态文本（覆盖层中央的判定结果）。</summary>
    public const string CaptureStatus = "快捷键捕获状态";
}

/// <summary>
/// toast 状态播报文本（F48）：错误消息带前缀、可选动作标签与次级说明并入一句，
/// 供 Narrator 经 LiveSetting=Polite 播报。纯函数，XAML 绑定 ToastViewModel.Announcement。
/// </summary>
public static class ToastAnnouncement
{
    public static string Build(string message, bool isError, string? actionLabel, string? dim = null)
    {
        var text = isError ? $"错误：{message}" : message;
        if (!string.IsNullOrWhiteSpace(actionLabel))
        {
            text += $"，{actionLabel}";
        }
        if (!string.IsNullOrWhiteSpace(dim))
        {
            text += $"，{dim}";
        }
        return text;
    }
}

/// <summary>
/// 减少动画策略（F48）：系统「减少动态」（SPI_GETCLIENTAREAANIMATION / 系统性能选项
/// 「在窗口内显示动画元素」关闭）时停用 toast 入场/离场过渡动画。
/// 直接读系统参数而不走 SystemParameters.ClientAreaAnimation：后者有静态缓存、
/// 仅在窗口收到 WM_SETTINGCHANGE 时失效，翻转设置后存在时滞（见测试注释）；
/// 本策略只在 toast 入栈时刻调用，每次一次 user32 调用无成本压力，且即时反映系统状态。
/// </summary>
public static class AccessibilityMotion
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    /// <summary>true=系统允许客户区动画，播放过渡；false=减少动态，直接落地不播动画。
    /// 读数失败降级为「允许动画」（读不出就按默认开动画兜底，绝不让 toast 链因读数异常中断）。</summary>
    public static bool PlayAnimations
    {
        get
        {
            return SystemParametersInfoGet(SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0)
                && enabled;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfoGet(uint action, uint uiParam, out bool pvParam, uint fWinIni);
}
