using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace P2Focus;

internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

internal static class Win32
{
    public const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_V = 0x56;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint ASFW_ANY = 0xFFFF_FFFF;
    public const uint GA_ROOT = 2;
    public const int SW_SHOW = 5, SW_MINIMIZE = 6, SW_RESTORE = 9;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
    public const uint WS_EX_TOPMOST = 0x0000_0080;
    public const uint WS_CHILD = 0x4000_0000, WS_VISIBLE = 0x1000_0000, WS_VSCROLL = 0x0020_0000;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF_0000;
    public const uint ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040, ES_WANTRETURN = 0x1000;
    public const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_SETFOCUS = 0x0007;
    public const uint WM_SETTEXT = 0x000C, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E;
    public const uint PM_REMOVE = 0x0001;
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const int TOKEN_QUERY = 0x0008;
    public const int TokenIntegrityLevel = 25;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc? lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    // INPUT 联合体必须含 MOUSEINPUT，否则 x64 上 Marshal.SizeOf 少 8 字节，SendInput 以 ERROR_INVALID_PARAMETER 拒收
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public HARDWAREINPUT hi;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int cbSize);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowExW(uint exStyle, string cls, uint nullTitle, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hwnd, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll")] public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint length, out uint returned);
    [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutPtrW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutStrW(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder text, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageStrW(IntPtr hwnd, uint msg, IntPtr wParam, string text);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessagePtrW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // WM_GETTEXT/WM_GETTEXTLENGTH 跨进程由系统封送；SMTO_ABORTIFHUNG 防目标挂死拖住调用方，
    // 超时/失败返回 null（读不到 ≠ 空，调用方据此区分）
    public static string? GetWindowText(IntPtr hwnd)
    {
        if (SendMessageTimeoutPtrW(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 2000, out var lenResult) == IntPtr.Zero) return null;
        int len = unchecked((int)lenResult);
        if (len < 0) return null;
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        if (SendMessageTimeoutStrW(hwnd, WM_GETTEXT, (IntPtr)(len + 1), sb,
                SMTO_ABORTIFHUNG, 2000, out _) == IntPtr.Zero) return null;
        return sb.ToString();
    }

    public static bool SetWindowText(IntPtr hwnd, string text)
    {
        SendMessageStrW(hwnd, WM_SETTEXT, IntPtr.Zero, text);
        return true;
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, 256);
        return sb.ToString();
    }

    public static uint SendCtrlV()
    {
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, 0),
            KeyInput(VK_V, 0),
            KeyInput(VK_V, KEYEVENTF_KEYUP),
            KeyInput(VK_CONTROL, KEYEVENTF_KEYUP),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static uint SendInputKey(ushort vk, uint flags)
    {
        var inputs = new[] { KeyInput(vk, flags) };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = 1, // INPUT_KEYBOARD
        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    public static string IntegrityOfCurrent() => IntegrityOfProcessInner(-1);

    public static string IntegrityOfProcess(uint pid)
    {
        var proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == IntPtr.Zero) return "unknown(OpenProcess denied)";
        try { return IntegrityOfProcessInner(proc); } finally { CloseHandle(proc); }
    }

    static string IntegrityOfProcessInner(IntPtr proc)
    {
        if (!OpenProcessToken(proc, TOKEN_QUERY, out var token)) return "unknown(token denied)";
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var len);
            if (len == 0) return "unknown";
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _)) return "unknown";
                var sid = Marshal.ReadIntPtr(buf);
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                uint il = (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                string level = il >= 0x3000 ? "high" : il >= 0x2000 ? "medium" : il >= 0x1000 ? "low" : $"il0x{il:X}";
                return $"{level}(0x{il:X})";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
    }

    public static bool SetClipboardText(string text)
    {
        bool opened = false;
        for (int i = 0; i < 10; i++) { if (OpenClipboard(IntPtr.Zero)) { opened = true; break; } Thread.Sleep(8); }
        if (!opened) return false;
        try
        {
            EmptyClipboard();
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
            if (h == IntPtr.Zero) return false;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return false; }
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            // 成功后所有权移交系统；失败才需自行释放
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    public static string? GetClipboardText()
    {
        bool opened = false;
        for (int i = 0; i < 10; i++) { if (OpenClipboard(IntPtr.Zero)) { opened = true; break; } Thread.Sleep(8); }
        if (!opened) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    // 探针在 UI 线程串行执行，等待期间必须抽水，面板/靶窗才能响应激活与聚焦
    public static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(2);
        }
    }
}
