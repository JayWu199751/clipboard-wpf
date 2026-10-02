using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 临时托盘（F29/F30 雏形；五档精确图标与双击行为归 T07）：
/// 左键单击抬起呼出面板，右键弹出菜单（显示面板/退出）。
/// 图标临时用系统应用图标，经由隐藏的顶层窗口接收回调消息。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const uint TrayCallbackId = 1;
    private const int IconApplication = 32512; // IDI_APPLICATION，临时图标

    private readonly List<(int Id, string Label, Action OnClick)> _menuItems = [];
    private readonly Action _onSummonClick;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _added;

    public TrayIconHost(Action onSummonClick, IReadOnlyList<(string Label, Action OnClick)> menuItems, string tooltip)
    {
        _onSummonClick = onSummonClick;
        for (var i = 0; i < menuItems.Count; i++)
        {
            _menuItems.Add((i + 1, menuItems[i].Label, menuItems[i].OnClick));
        }

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
            hIcon = NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)IconApplication),
            szTip = tooltip,
        };
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_APP_TRAY) return IntPtr.Zero;

        switch ((uint)lParam.ToInt64())
        {
            case NativeMethods.WM_LBUTTONUP:
                _onSummonClick();
                handled = true;
                break;
            case NativeMethods.WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        foreach (var item in _menuItems)
        {
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)item.Id, item.Label);
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

        if (command != 0)
        {
            _menuItems.FirstOrDefault(item => item.Id == command).OnClick?.Invoke();
        }
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
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
