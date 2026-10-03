using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace P2Focus;

internal static class Paths
{
    // 提权与普通进程同一用户同一 %TEMP%，握手文件两侧可达
    public static string Dir => Path.Combine(Path.GetTempPath(), "p2-elevated-focus");
    public static string EnsureDir() { Directory.CreateDirectory(Dir); return Dir; }
}

/// <summary>靶窗内容指纹：len + SHA1(UTF-16)，由靶窗进程自记，跨完整性可读，替代跨进程读窗口文本。</summary>
internal static class Markers
{
    public static string Compute(string s)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(s));
        return $"len={s.Length} hash={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

/// <summary>探针用宿主窗口（面板/牺牲窗）：单 EDIT 子控件，WM_SETFOCUS 转焦到编辑框。</summary>
internal static class ProbeWindows
{
    static IntPtr hEdit;
    static readonly WndProc ProcRef = WndProcImpl;

    static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_SETFOCUS) { Win32.SetFocus(hEdit); return 0; }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public static IntPtr Create(string cls, string title, int w, int h, out IntPtr edit)
    {
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = ProcRef,
            hInstance = Win32.GetModuleHandleW(null),
            hbrBackground = (IntPtr)6,
            lpszClassName = cls,
        };
        Win32.RegisterClassExW(ref wc);
        var hwnd = Win32.CreateWindowExW(Win32.WS_EX_TOPMOST, cls, title, Win32.WS_OVERLAPPEDWINDOW,
            120, 120, w, h, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        hEdit = edit = Win32.CreateWindowExW(0, "EDIT", 0u,
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_VSCROLL |
            Win32.ES_MULTILINE | Win32.ES_AUTOVSCROLL | Win32.ES_WANTRETURN,
            8, 8, w - 24, h - 48, hwnd, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        Win32.UpdateWindow(hwnd);
        return hwnd;
    }
}
