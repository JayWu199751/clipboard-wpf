using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Domain.Settings;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>托盘鼠标事件的归一形态（从 WM_* 消息侧映射而来），判定函数在其上表驱动直测。</summary>
public enum TrayMouseEvent
{
    LeftDown,
    LeftUp,
    LeftDoubleClick,
    RightUp,
    Enter,
}

/// <summary>主题子菜单的一条项（菜单 id、文案、是否打勾）。</summary>
public readonly record struct ThemeMenuItem(string Id, string Label, bool Checked);

/// <summary>
/// 托盘图标与菜单的纯判定（F29/F30；legacy tray.rs 判定一~六的移植）：
/// 图标五档 16/20/24/28/32 × 亮暗两套，按主屏 scaleFactor 取「恰好物理尺寸」（非整数缩放下
/// 1:1 才不糊）；去重键取实际选中档位——display-metrics 高频触发重设，同键必须不动；
/// 左键只认抬起呼出（外壳对一次点击发 Down+Up 两条）；菜单三条随状态变化的文案在此收口。
/// </summary>
public static class TrayIconDecider
{
    /// <summary>阶梯档位（物理像素），与图标资产文件名一一对应。</summary>
    public static readonly int[] Sizes = [16, 20, 24, 28, 32];

    /// <summary>任务栏图标的逻辑尺寸：目标物理尺寸 = round(16 × scale)。</summary>
    public const double LogicalSize = 16.0;

    /// <summary>判定一：主屏缩放 → 阶梯里离目标物理尺寸最近的一档（同距取较小档位，与生成顺序一致）。</summary>
    public static int SizeForScale(double scale)
    {
        var target = (int)Math.Round(LogicalSize * scale);
        return Sizes.OrderBy(size => Math.Abs(size - target)).First();
    }

    /// <summary>
    /// 判定二：图标去重键。键取「实际选中的档位」而不是目标像素：两档之间的缩放变化
    /// 不改变图标，也就不该重设。深色任务栏用浅色（白色）图标，故 dark 对应 light 资产。
    /// </summary>
    public static string IconKey(bool dark, double scale) =>
        $"{(dark ? "light" : "dark")}@{SizeForScale(scale)}";

    /// <summary>判定四：哪一发托盘事件算「把面板呼出来」。左键仅认抬起；双击认（其最后一发是抬起）。</summary>
    public static bool OpensPanel(TrayMouseEvent @event) => @event
        is TrayMouseEvent.LeftUp
        or TrayMouseEvent.LeftDoubleClick;

    /// <summary>判定五：指针进到了图标上——正是核一遍配色的时机（广播可能收不到，悬停兜底）。</summary>
    public static bool PointerEntered(TrayMouseEvent @event) => @event == TrayMouseEvent.Enter;

    /// <summary>判定六：菜单文案（其余菜单项是常量）。快捷键走展示格式（AccelCodec.FormatDisplay）。</summary>
    public static (string Shortcut, string Autostart) MenuLabels(string shortcutAccel, bool autoStart)
    {
        var display = AccelCodec.FormatDisplay(shortcutAccel);
        return (
            $"更换快捷键(当前: {display})",
            $"开机启动 {(autoStart ? "✅" : "❌")}");
    }

    /// <summary>主题子菜单的标题。当前态写进标题是刻意的：多数人不看子菜单里勾在哪一项。</summary>
    public static string ThemeMenuTitle(ThemeKind theme) => $"主题(当前: {ThemeLabels.Of(theme)})";

    /// <summary>判定：给定当前偏好，三条主题项各自的 (id, 文案, 是否打勾)。顺序即菜单顺序。</summary>
    public static IReadOnlyList<ThemeMenuItem> ThemeMenuItems(ThemeKind selected) => new[]
    {
        new ThemeMenuItem("theme-system", ThemeLabels.System, selected == ThemeKind.System),
        new ThemeMenuItem("theme-light", ThemeLabels.Light, selected == ThemeKind.Light),
        new ThemeMenuItem("theme-dark", ThemeLabels.Dark, selected == ThemeKind.Dark),
    };

    /// <summary>判定：菜单 id → 偏好。不是主题项的 id 返回 null，调用方据此决定要不要改设置。</summary>
    public static ThemeKind? ThemeOfMenuId(string id) => id switch
    {
        "theme-system" => ThemeKind.System,
        "theme-light" => ThemeKind.Light,
        "theme-dark" => ThemeKind.Dark,
        _ => null,
    };
}
