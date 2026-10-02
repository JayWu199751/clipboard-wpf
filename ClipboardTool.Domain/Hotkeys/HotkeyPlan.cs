namespace ClipboardTool.Domain.Hotkeys;

/// <summary>热键修饰键，取值对齐 Win32 MOD_*，注册执行方（Infrastructure）可原样透传。</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>一个全局热键组合（修饰键 + 虚拟键）。</summary>
public readonly record struct HotkeyCombo(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>
    /// 键位注册表风格的展示文本（legacy「提示 = 行为」硬约束：chip/菜单一律由计划推导，不写死键名）。
    /// 修饰键顺序固定 Ctrl+Shift+Alt+Win；主键覆盖 Esc/数字/字母/F1–F24，其余按 VK 码兜底。
    /// </summary>
    public string DisplayName
    {
        get
        {
            var text = VirtualKey switch
            {
                0x1B => "Esc",
                >= 0x30 and <= 0x39 => ((char)VirtualKey).ToString(),
                >= 0x41 and <= 0x5A => ((char)VirtualKey).ToString(),
                >= 0x70 and <= 0x87 => $"F{VirtualKey - 0x6F}",
                _ => $"VK_{VirtualKey:X2}",
            };
            // 前置拼接：后拼的在串首，故按 Win→Alt→Shift→Ctrl 逆序，得到固定的 Ctrl+Shift+Alt+Win
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) text = "Win+" + text;
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) text = "Alt+" + text;
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) text = "Shift+" + text;
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) text = "Ctrl+" + text;
            return text;
        }
    }
}

/// <summary>
/// 目标键集合对已生效键集合的差量。计划层只推导，不注册；
/// 注册/注销执行归 Infrastructure（已生效键集合由执行注册者维护，legacy ADR-0006）。
/// </summary>
public readonly record struct HotkeyDiff(
    IReadOnlyList<HotkeyCombo> ToRegister,
    IReadOnlyList<HotkeyCombo> ToUnregister)
{
    public bool IsEmpty => ToRegister.Count == 0 && ToUnregister.Count == 0;
}

/// <summary>
/// 面板键位计划（F18–F20 完整四态键位归 T04；当前骨架只含呼出键）。
/// </summary>
public sealed class HotkeyPlan
{
    public const uint VirtualKeyV = 0x56;
    public const uint VirtualKeyEscape = 0x1B;

    /// <summary>默认呼出键：Ctrl+Shift+V。</summary>
    public static HotkeyCombo SummonDefault { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeyV);

    /// <summary>浏览态停靠键：裸 Esc（F18）。仅在面板呼出期间进入计划，停靠后让位注销。</summary>
    public static HotkeyCombo BrowseDock { get; } = new(HotkeyModifiers.None, VirtualKeyEscape);

    private readonly IReadOnlyList<HotkeyCombo> _targets;

    public HotkeyPlan(IReadOnlyList<HotkeyCombo> targets) =>
        _targets = [.. targets.Distinct()];

    public IReadOnlyList<HotkeyCombo> Targets => _targets;

    /// <summary>呼出键单键计划。</summary>
    public static HotkeyPlan Default { get; } = new([SummonDefault]);

    /// <summary>对已生效键集合推导差量：缺的补注册、多的注销，一致的不动。</summary>
    public HotkeyDiff PlanDiff(IEnumerable<HotkeyCombo> effectiveKeys)
    {
        var effective = new HashSet<HotkeyCombo>(effectiveKeys);
        var toRegister = _targets.Where(key => !effective.Contains(key)).ToList();
        var toUnregister = effectiveKeys.Where(key => !_targets.Contains(key)).Distinct().ToList();
        return new HotkeyDiff(toRegister, toUnregister);
    }
}
