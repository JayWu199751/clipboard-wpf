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
    public const uint CF_DIB = 8;
    public const uint CF_DIBV5 = 17;
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
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x0001;
    public const uint NIF_ICON = 0x0002;
    public const uint NIF_TIP = 0x0004;
    public const int WM_APP_TRAY = 0x8000 + 1; // WM_APP + 1
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const int WM_NULL = 0x0000;

    // 托盘右键菜单
    public const uint MF_STRING = 0x0000;
    public const uint MF_POPUP = 0x0010;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_CHECKED = 0x0008;
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

    // —— 全局低级鼠标钩子（F17；legacy click_watcher.rs 同款 API 面） ——

    public const int WH_MOUSE_LL = 14;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_XBUTTONDOWN = 0x020B;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>LL 钩子的按下信息：物理像素坐标 + 注入标志等（本进程只读 pt）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    /// <summary>安装钩子。LL 钩子 hMod 传 Zero（钩子过程在本进程内）、线程 id 传 0。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

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

    // PostThreadMessageW 在 user32（不在 kernel32；声明错 DLL 会 EntryPointNotFound）
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessageW(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_QUIT = 0x0012;
    public const uint WM_TIMER = 0x0113;

    // ---------- 注册表主题键读取与监听（F26–F28） ----------

    public static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    public const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    // RegGetValueW 在本机环境对所有调用约定返回 1630（.NET 自家的 Registry 类走的是
    // RegQueryValueExW 且可用）——主题读数改用 RegOpenKeyW + RegQueryValueExW + RegCloseKey。
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegQueryValueExW(IntPtr hkey, string lpValueName, IntPtr lpReserved,
        out uint lpType, out uint lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegOpenKeyW(IntPtr hkey, string lpSubKey, out IntPtr phkResult);

    [DllImport("advapi32.dll")]
    public static extern int RegCloseKey(IntPtr hkey);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegNotifyChangeKeyValue(IntPtr hkey, bool watchSubtree,
        int notifyFilter, IntPtr manualResetEvent, bool asynchronous);

    // ---------- 托盘图标（PNG → HICON，F29） ----------

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bitmapInfo, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo); // 返回 HICON（非 BOOL）

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight; // 正值 = 自底向上
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // 32bpp BI_RGB 不需要颜色表
    }

    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    /// <summary>创建 32bpp 自底向上 DIB 并写入 BGRA 像素（托盘图标色层）。</summary>
    public static IntPtr CreateGDIBitmap(int width, int height, byte[] bgraPixels)
    {
        var hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        try
        {
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = height, // 正值 = bottom-up：BGRA 行序需倒序写入
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };
            var bits = CreateDIBSection(hdc, ref info, DIB_RGB_COLORS, out var ppv, IntPtr.Zero, 0);
            if (bits == IntPtr.Zero || ppv == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            // 自底向上：最后一行先放。CopyPixels 是 top-down 行序，倒行距拷入
            for (var row = 0; row < height; row++)
            {
                var source = row * width * 4;
                var target = (height - 1 - row) * width * 4;
                Marshal.Copy(bgraPixels, source, ppv + target, width * 4);
            }
            return bits;
        }
        finally
        {
            _ = DeleteDC(hdc);
        }
    }

    /// <summary>创建 1bpp 单色掩码位图（32bpp BGRA 带 alpha，掩码全 0 = 全 opaque 即可）。</summary>
    public static IntPtr CreateGDIMask(int width, int height)
    {
        // 1bpp 行宽按 16 位对齐（GDI 要求 WORD 对齐）；全 0 = opaque
        var stride = (width + 15) / 16 * 2;
        var mask = new byte[stride * height];
        return CreateBitmap(width, height, 1, 1, mask);
    }

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
