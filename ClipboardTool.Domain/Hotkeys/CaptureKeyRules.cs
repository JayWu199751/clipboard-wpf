namespace ClipboardTool.Domain.Hotkeys;

/// <summary>捕获校验结论（F31）。</summary>
public enum CaptureVerdict
{
    /// <summary>合格：可交给试注册。</summary>
    Ok,

    /// <summary>主键认不出（码表外按键）。</summary>
    UnknownKey,

    /// <summary>只按了 Shift 或裸主键：Ctrl/Alt/Win 至少要含一。</summary>
    MissingModifier,
}

/// <summary>
/// 捕获覆盖层的组合键校验（F31；legacy App.tsx shortcutCapture keydown 判定）：
/// Ctrl / Alt / Win 至少含一 + 主键可识别；Shift 单独不合格（Shift+X 只是打字）；
/// 裸主键也不合格。Esc 不进校验——它是取消键，捕获层先行消费。
/// </summary>
public static class CaptureKeyRules
{
    public static CaptureVerdict Validate(HotkeyCombo combo)
    {
        var hasModifier = combo.Modifiers.HasFlag(HotkeyModifiers.Control)
            || combo.Modifiers.HasFlag(HotkeyModifiers.Alt)
            || combo.Modifiers.HasFlag(HotkeyModifiers.Win);
        return hasModifier ? CaptureVerdict.Ok : CaptureVerdict.MissingModifier;
    }

    /// <summary>校验失败的用户可读文案（措辞在此收口，覆盖层只管照打）。</summary>
    public static string MessageOf(CaptureVerdict verdict) => verdict switch
    {
        CaptureVerdict.MissingModifier => "请包含 Ctrl / Alt / Win 修饰键",
        _ => "无法识别的按键",
    };
}
