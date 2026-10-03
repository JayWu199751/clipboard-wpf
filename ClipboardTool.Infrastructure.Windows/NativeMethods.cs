using System.Runtime.InteropServices;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>Infrastructure.Windows 内部互操作声明。仅 x64（ADR-0001 发布形态）。</summary>
internal static class NativeMethods
{
    // 窗口扩展样式（ADR-0002：浏览态 WS_EX_NOACTIVATE；任务栏/Alt+Tab 排除靠 WS_EX_TOOLWINDOW）
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    // 显示器
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;

    // SetWindowPos
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    // 热键
    public const int WM_HOTKEY = 0x0312;

    // 剪贴板（F09/F10：独占窗口、序列号短路、事件源）
    public const uint CF_UNICODETEXT = 13;
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>message-only 窗口的父句柄（HWND_MESSAGE）。</summary>
    public static readonly IntPtr HwndMessage = new(-3);

    // 恢复/注入（本仓库 ADR-0003）
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_V = 0x56;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint ASFW_ANY = 0xFFFFFFFF;
    public const uint GA_ROOT = 2;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint GMEM_MOVEABLE = 0x0002;

    // 托盘（Shell_NotifyIcon）
    public const uint NIM_ADD = 0;
    public const uint NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x0001;
    public const uint NIF_ICON = 0x0002;
    public const uint NIF_TIP = 0x0004;
    public const int WM_APP_TRAY = 0x8000 + 1; // WM_APP + 1
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const int WM_NULL = 0x0000;

    // 托盘右键菜单
    public const uint MF_STRING = 0x0000;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(uint vKey);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint flags, UIntPtr idNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr pTpmParams);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------- 剪贴板独占与事件源（F09/F10） ----------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    public static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapsed, IntPtr proc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PostThreadMessageW(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_QUIT = 0x0012;
    public const uint WM_TIMER = 0x0113;

    // ---------- 焦点恢复与注入（本仓库 ADR-0003；F12/F13） ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    // INPUT 联合体必须含 MOUSEINPUT，否则 x64 上 Marshal.SizeOf 少 8 字节，SendInput 以参数错误拒收
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public HARDWAREINPUT hi;
    }
}
