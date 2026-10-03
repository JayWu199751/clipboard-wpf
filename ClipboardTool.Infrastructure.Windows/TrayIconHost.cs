using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>托盘菜单一条项：id 为逻辑 id（回调按它分发）；Checked 打勾；Separator 分隔线；
/// SubItems 子菜单（主题三项）。文案由 TrayIconDecider/宿主推导，本类不写死键名。</summary>
public sealed record TrayMenuItem(
    string Id,
    string? Label = null,
    bool Checked = false,
    bool Separator = false,
    IReadOnlyList<TrayMenuItem>? SubItems = null);

/// <summary>
/// 托盘（F29/F30/F33；Shell_NotifyIcon 直呼，legacy tray.rs 效果侧移植）：
/// 左键只认抬起呼出（双击认，decider 判定），右键弹菜单，悬停核配色；
/// 菜单按 TrayMenuSpec 重建（文案随设置变化，托盘没有「就地改一条文案」的 seam）；
/// 图标经 TrayIconSync（同键不重设、HICON 生命周期）+ PngIconFactory（五档精确尺寸）。
/// 回调经隐藏的顶层窗口消息到达，宿主自行归队（与模式线程封闭模型一致）。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const uint TrayCallbackId = 1;

    private readonly string _tooltip;
    private readonly TrayIconSync _iconSync;
    private readonly List<TrayMenuItem> _menuItems = [];
    private readonly Dictionary<uint, string> _commandMap = []; // Win32 菜单 id → 逻辑 id
    private uint _nextCommandId = 1;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _added;
    private IntPtr _currentIcon;

    /// <summary>左键抬起/双击呼出（decider OpensPanel 判定过才触发）。</summary>
    public event Action? SummonRequested;

    /// <summary>指针进到图标上——正是核一遍配色的时机（广播兜底）。</summary>
    public event Action? PointerEntered;

    /// <summary>菜单项选中（逻辑 id；非主题项的 id 宿主自行分发）。</summary>
    public event Action<string>? MenuItemSelected;

    public TrayIconHost(string tooltip, TrayIconSync? iconSync = null)
    {
        _tooltip = tooltip;
        _iconSync = iconSync ?? new TrayIconSync(new PngIconFactory());

        _source = new HwndSource(0, 0, 0, 0, 0, 0, 0, "ClipboardToolTray", IntPtr.Zero);
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);

        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayCallbackId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_APP_TRAY,
            hIcon = NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512), // IDI_APPLICATION 占位，SyncIcon 落正式图
            szTip = tooltip,
        };
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
    }

    /// <summary>重建菜单（文案变了就整份重建）。空 spec 只清空。</summary>
    public void SetMenuItems(IReadOnlyList<TrayMenuItem> items)
    {
        _menuItems.Clear();
        _menuItems.AddRange(items);
    }

    /// <summary>按当前明暗与主屏缩放同步托盘图标（同键内部去重）。</summary>
    public void SyncIcon(bool dark, double scale)
    {
        var hicon = _iconSync.Sync(dark, scale);
        if (hicon == IntPtr.Zero || hicon == _currentIcon)
        {
            return;
        }
        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayCallbackId,
            uFlags = NativeMethods.NIF_ICON,
            hIcon = hicon,
        };
        if (NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data))
        {
            _currentIcon = hicon; // 旧句柄已由 TrayIconSync 换图时销毁，这里只跟踪当前句柄
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_APP_TRAY)
        {
            return IntPtr.Zero;
        }
        var @event = (uint)lParam.ToInt64() switch
        {
            NativeMethods.WM_LBUTTONUP => TrayMouseEvent.LeftUp,
            NativeMethods.WM_LBUTTONDOWN => TrayMouseEvent.LeftDown,
            NativeMethods.WM_LBUTTONDBLCLK => TrayMouseEvent.LeftDoubleClick,
            NativeMethods.WM_RBUTTONUP => TrayMouseEvent.RightUp,
            NativeMethods.WM_MOUSEMOVE => TrayMouseEvent.Enter,
            _ => (TrayMouseEvent?)null,
        };
        if (@event is null)
        {
            return IntPtr.Zero;
        }
        if (TrayIconDecider.OpensPanel(@event.Value))
        {
            SummonRequested?.Invoke();
            handled = true;
        }
        else if (TrayIconDecider.PointerEntered(@event.Value))
        {
            PointerEntered?.Invoke();
        }
        else if (@event == TrayMouseEvent.RightUp)
        {
            ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        _commandMap.Clear();
        _nextCommandId = 1;
        var menu = BuildMenu(_menuItems);
        if (menu == IntPtr.Zero)
        {
            return;
        }
        _ = NativeMethods.GetCursorPos(out var point);
        // 先 SetForegroundWindow，点击菜单外区域才能让菜单收起（托盘菜单经典要求）
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        var command = NativeMethods.TrackPopupMenuEx(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RIGHTBUTTON,
            point.X, point.Y, _hwnd, IntPtr.Zero);
        _ = NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        _ = NativeMethods.DestroyMenu(menu);

        if (command != 0 && _commandMap.TryGetValue((uint)command, out var id))
        {
            MenuItemSelected?.Invoke(id);
        }
    }

    /// <summary>递归构建 Win32 菜单（分隔线/勾选/子菜单）。失败返回 IntPtr.Zero。</summary>
    private IntPtr BuildMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = NativeMethods.CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.Separator)
            {
                _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, string.Empty);
                continue;
            }
            if (item.SubItems is { } subItems)
            {
                var sub = BuildMenu(subItems);
                if (sub == IntPtr.Zero)
                {
                    continue;
                }
                var flags = NativeMethods.MF_POPUP | (item.Checked ? NativeMethods.MF_CHECKED : 0u);
                _ = NativeMethods.AppendMenuW(menu, flags, (UIntPtr)sub.ToInt64(), item.Label ?? string.Empty);
                continue;
            }
            var command = _nextCommandId++;
            _commandMap[command] = item.Id;
            var itemFlags = NativeMethods.MF_STRING | (item.Checked ? NativeMethods.MF_CHECKED : 0u);
            _ = NativeMethods.AppendMenuW(menu, itemFlags, (UIntPtr)command, item.Label ?? string.Empty);
        }
        return menu;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TrayCallbackId,
            };
            _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }
        if (_currentIcon != IntPtr.Zero)
        {
            _currentIcon = IntPtr.Zero; // 句柄由 TrayIconSync.Dispose 统一销毁
        }
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
