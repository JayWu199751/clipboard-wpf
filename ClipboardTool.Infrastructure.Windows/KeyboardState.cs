namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 物理键态查询。Windows 全局热键没有松键事件（按下只发一次 WM_HOTKEY），
/// ↑↓ 长按重复期间轮询这里判定「松键即停」（F19；legacy global-shortcut 插件的 release 事件替代）。
/// </summary>
public static class KeyboardState
{
    /// <summary>虚拟键当前是否物理按住（GetAsyncKeyState 高位）。</summary>
    public static bool IsKeyDown(uint virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
