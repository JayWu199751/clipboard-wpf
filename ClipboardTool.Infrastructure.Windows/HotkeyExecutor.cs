using System.Windows;
using System.Windows.Interop;
using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 热键注册的原生端口：把组合键交给系统、从系统收回。
/// 抽象出来是为了让「注册失败不上账」的记账规则可以脱离真窗口测试（ADR-0006/0010）。
/// </summary>
public interface IHotkeyNative
{
    /// <summary>注册组合键；系统拒绝（通常是被其他程序占用）返回 false。</summary>
    bool Register(IntPtr hwnd, int id, HotkeyCombo combo);

    /// <summary>注销组合键。表记的是「我们以为生效的键」，系统侧残留随进程退出消失。</summary>
    void Unregister(IntPtr hwnd, int id, HotkeyCombo combo);
}

/// <summary>RegisterHotKey/UnregisterHotKey 的真实端口。未挂接窗口（hwnd=Zero）时注册一律拒绝。</summary>
public sealed class NativeHotkeyMethods : IHotkeyNative
{
    public static readonly NativeHotkeyMethods Instance = new();

    private NativeHotkeyMethods() { }

    public bool Register(IntPtr hwnd, int id, HotkeyCombo combo) =>
        hwnd != IntPtr.Zero && NativeMethods.RegisterHotKey(hwnd, id, (uint)combo.Modifiers, combo.VirtualKey);

    public void Unregister(IntPtr hwnd, int id, HotkeyCombo combo)
    {
        if (hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.UnregisterHotKey(hwnd, id);
        }
    }
}

/// <summary>
/// RegisterHotKey 的执行注册者：持有已生效键集合（legacy ADR-0006/0010），
/// 按 HotkeyPlan 差量落注册/注销，WM_HOTKEY 触发回调。
/// 注册失败（被占用/未挂接窗口）不上账——失败就是没进表，下一次差量自然重试。
/// </summary>
public sealed class HotkeyExecutor : IDisposable
{
    private readonly IHotkeyNative _native;
    private readonly Dictionary<int, (HotkeyCombo Combo, Action OnTrigger)> _registered = new();
    private IntPtr _hwnd;
    private HwndSource? _source;
    private int _nextId = 1;

    public HotkeyExecutor(IHotkeyNative? native = null) => _native = native ?? NativeHotkeyMethods.Instance;

    /// <summary>是否已挂接窗口。未挂接时注册侧不向系统发起（WM_HOTKEY 无处投递）。</summary>
    public bool Attached => _hwnd != IntPtr.Zero;

    /// <summary>挂到窗口 HWND 接收 WM_HOTKEY。须在窗口句柄就绪后（SourceInitialized）调用。</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// 应用计划差量：先注销多余键（腾出系统侧槽位）、后补注册缺失键；一致的不动。
    /// 注册成功者进入已生效集合；注册被拒（被占用，或未挂接窗口时原生端口拒绝）不上账。
    /// </summary>
    public void ApplyPlan(HotkeyDiff diff, Action<HotkeyCombo> onTrigger)
    {
        foreach (var combo in diff.ToUnregister)
        {
            if (FindId(combo) is { } existing)
            {
                _native.Unregister(_hwnd, existing, combo);
                _registered.Remove(existing);
            }
        }

        foreach (var combo in diff.ToRegister)
        {
            var id = _nextId++;
            if (_native.Register(_hwnd, id, combo))
            {
                _registered[id] = (combo, () => onTrigger(combo));
            }
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
            _native.Unregister(_hwnd, id, _registered[id].Combo);
        }
        _registered.Clear();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
