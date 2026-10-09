using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClipboardTool.Application;

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
/// 图标随外壳存亡：Explorer 重启或自启早于任务栏就绪时 NIM_ADD 会丢/败，
/// 监听 TaskbarCreated 广播整只重加（SyncIcon 侧另有未加上的自愈兜底）。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const uint TrayCallbackId = 1;

    /// <summary>外壳重建托盘时的系统广播（值全系统一致，≥0xC000 不与 WM_APP 撞）。</summary>
    private static readonly uint TaskbarCreatedMessage = RegisterTaskbarCreatedMessage();

    private readonly string _tooltip;
    private readonly TrayIconSync _iconSync;
    private readonly Action<IReadOnlyList<TrayMenuItem>, Action<string>> _showMenu;
    private readonly List<TrayMenuItem> _menuItems = [];
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _added;
    private IntPtr _currentIcon;
    private readonly DiagnosticLog _diagnostics;

    /// <summary>左键抬起/双击呼出（decider OpensPanel 判定过才触发）。</summary>
    public event Action? SummonRequested;

    /// <summary>指针进到图标上——正是核一遍配色的时机（广播兜底）。</summary>
    public event Action? PointerEntered;

    /// <summary>菜单项选中（逻辑 id；非主题项的 id 宿主自行分发）。</summary>
    public event Action<string>? MenuItemSelected;

    public TrayIconHost(string tooltip,
        Action<IReadOnlyList<TrayMenuItem>, Action<string>> showMenu,
        TrayIconSync? iconSync = null,
        DiagnosticLog? diagnostics = null)
    {
        _tooltip = tooltip;
        _showMenu = showMenu;
        _iconSync = iconSync ?? new TrayIconSync(new PngIconFactory());
        _diagnostics = diagnostics ?? DiagnosticLog.None;

        _source = new HwndSource(0, 0, 0, 0, 0, 0, 0, "ClipboardToolTray", IntPtr.Zero);
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);

        var data = BuildFullIconData(NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512)); // IDI_APPLICATION 占位，SyncIcon 落正式图
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
        _diagnostics.Vital($"tray-ready added={_added}");
    }

    /// <summary>重建菜单（文案变了就整份重建）。空 spec 只清空。</summary>
    public void SetMenuItems(IReadOnlyList<TrayMenuItem> items)
    {
        _menuItems.Clear();
        _menuItems.AddRange(items);
    }

    /// <summary>按当前明暗与主屏缩放同步托盘图标（同键内部去重）。未加上时走整只添加自愈。</summary>
    public void SyncIcon(bool dark, double scale)
    {
        var hicon = _iconSync.Sync(dark, scale);
        if (hicon == IntPtr.Zero || hicon == _currentIcon)
        {
            return;
        }
        if (!_added)
        {
            // 初次 NIM_ADD 败了（如自启早于任务栏就绪且广播未至）：正式图直接整只补上
            var add = BuildFullIconData(hicon);
            _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref add);
            _diagnostics.Vital($"tray-readd source=sync added={_added}");
            if (_added)
            {
                _currentIcon = hicon;
            }
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

    /// <summary>完整形态的图标条目（消息+图+提示）：初次添加与外壳重建重加共用。</summary>
    private NativeMethods.NOTIFYICONDATAW BuildFullIconData(IntPtr hIcon) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayCallbackId,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
        uCallbackMessage = NativeMethods.WM_APP_TRAY,
        hIcon = hIcon,
        szTip = _tooltip,
    };

    /// <summary>外壳重建（TaskbarCreated）后整只重加：旧条目已随旧外壳蒸发，删是幂等清账。</summary>
    private void ReaddIcon()
    {
        if (_added)
        {
            var remove = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TrayCallbackId,
            };
            _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref remove);
            _added = false;
        }
        var hicon = _currentIcon != IntPtr.Zero
            ? _currentIcon // 句柄归本进程所有，外壳重启不影响其有效性
            : NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512);
        var add = BuildFullIconData(hicon);
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref add);
        _diagnostics.Vital($"tray-readd source=taskbar-created added={_added}");
    }

    private static uint RegisterTaskbarCreatedMessage()
    {
        try
        {
            return NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        }
        catch (Exception)
        {
            return 0; // 注册失败按「永不广播」处理：退化回旧行为，不崩
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (TaskbarCreatedMessage != 0 && (uint)msg == TaskbarCreatedMessage)
        {
            ReaddIcon(); // Explorer 重启托盘清空：整只重加，否则图标随旧外壳蒸发
            handled = true;
            return IntPtr.Zero;
        }
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
        // 先 SetForegroundWindow，点击菜单外区域才能让菜单收起（托盘菜单经典要求）
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        // 菜单由渲染层呈现，才能复用应用主题；命令仍通过同一逻辑 id 分发。
        _showMenu(_menuItems, id => MenuItemSelected?.Invoke(id));
        _ = NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
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
        _iconSync.Dispose();
    }
}
