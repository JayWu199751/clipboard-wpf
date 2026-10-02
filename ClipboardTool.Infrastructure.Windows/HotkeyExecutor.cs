using System.Windows;
using System.Windows.Interop;
using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// RegisterHotKey 的执行注册者：持有已生效键集合（legacy ADR-0006/0010），
/// 按 HotkeyPlan 差量落注册/注销，WM_HOTKEY 触发回调。
/// </summary>
public sealed class HotkeyExecutor : IDisposable
{
    private readonly Dictionary<int, (HotkeyCombo Combo, Action OnTrigger)> _registered = new();
    private IntPtr _hwnd;
    private HwndSource? _source;
    private int _nextId = 1;

    /// <summary>是否已挂接窗口。未挂接时 ApplyPlan 是空操作（WM_HOTKEY 无处投递）。</summary>
    public bool Attached => _hwnd != IntPtr.Zero;

    /// <summary>挂到窗口 HWND 接收 WM_HOTKEY。须在窗口句柄就绪后（SourceInitialized）调用。</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>应用计划差量：注销多余键、补注册缺失键，注册成功者进入已生效集合。未挂接窗口时忽略。</summary>
    public void ApplyPlan(HotkeyDiff diff, Action<HotkeyCombo> onTrigger)
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var combo in diff.ToUnregister)
        {
            var id = FindId(combo);
            if (id is { } existing)
            {
                _ = NativeMethods.UnregisterHotKey(_hwnd, existing);
                _registered.Remove(existing);
            }
        }

        foreach (var combo in diff.ToRegister)
        {
            var id = _nextId++;
            if (NativeMethods.RegisterHotKey(_hwnd, id, (uint)combo.Modifiers, combo.VirtualKey))
                _registered[id] = (combo, () => onTrigger(combo));
        }
    }

    public IReadOnlyList<HotkeyCombo> EffectiveKeys => _registered.Values.Select(entry => entry.Combo).ToList();

    private int? FindId(HotkeyCombo combo)
    {
        foreach (var (id, entry) in _registered)
        {
            if (entry.Combo == combo) return id;
        }
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var entry))
        {
            entry.OnTrigger();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys)
        {
            _ = NativeMethods.UnregisterHotKey(_hwnd, id);
        }
        _registered.Clear();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
