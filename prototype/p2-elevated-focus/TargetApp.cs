using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace P2Focus;

/// <summary>
/// 粘贴靶窗（模拟记事本类编辑器）：顶层窗口 + 多行 EDIT 子控件。
/// 职责：写握手 JSON（hwnd/edit/pid/tid/完整性）、自记内容事件日志（注入到达的权威证据）、
/// 自到期退出（--exit-after，避免提权靶窗残留）。
/// </summary>
internal static class TargetApp
{
    static IntPtr hEdit;
    static string eventLog = "";
    static readonly WndProc ProcRef = WndProcImpl;

    public static int Run(string name, int exitAfterSec)
    {
        var dir = Paths.EnsureDir();
        eventLog = Path.Combine(dir, $"target-{name}-events.log");
        AppendEvent($"ready | il={Win32.IntegrityOfCurrent()}");

        string cls = $"P2TargetWnd-{name}";
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = ProcRef,
            hInstance = Win32.GetModuleHandleW(null),
            hbrBackground = (IntPtr)6,
            lpszClassName = cls,
        };
        if (Win32.RegisterClassExW(ref wc) == 0) return Fail("RegisterClassExW");

        var hwnd = Win32.CreateWindowExW(0, cls, $"P2 目标 {name}", Win32.WS_OVERLAPPEDWINDOW,
            80, 80, 640, 480, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return Fail("CreateWindowExW");

        hEdit = Win32.CreateWindowExW(0, "EDIT", 0u,
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_VSCROLL |
            Win32.ES_MULTILINE | Win32.ES_AUTOVSCROLL | Win32.ES_WANTRETURN,
            8, 8, 600, 420, hwnd, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        Win32.UpdateWindow(hwnd);

        uint tid = Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        var handshake = new
        {
            name,
            hwnd = (long)hwnd,
            editHwnd = (long)hEdit,
            pid = (long)pid,
            tid = (long)tid,
            integrity = Win32.IntegrityOfCurrent(),
            kind = "probe",
            exe = Environment.ProcessPath,
        };
        File.WriteAllText(Path.Combine(dir, $"target-{name}.json"),
            JsonSerializer.Serialize(handshake));
        AppendEvent($"text | {Markers.Compute("")}");

        if (exitAfterSec > 0)
        {
            new Thread(() =>
            {
                Thread.Sleep(exitAfterSec * 1000);
                Win32.PostMessageW(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            })
            { IsBackground = true }.Start();
        }

        new Thread(PollText) { IsBackground = true }.Start();

        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
        AppendEvent("closing");
        return 0;
    }

    static void PollText()
    {
        string last = "";
        int ticks = 0;
        while (true)
        {
            Thread.Sleep(50);
            if (hEdit == IntPtr.Zero || !Win32.IsWindow(hEdit)) return;
            // 心跳：区分「进程存活」与「UI 线程冻结」（心跳停=整进程问题；心跳在而文本事件停=UI 线程问题）
            if (++ticks % 40 == 0) AppendEvent("beat");
            string text = Win32.GetWindowText(hEdit) ?? "";
            if (text != last)
            {
                last = text;
                AppendEvent($"text | {Markers.Compute(text)}");
            }
        }
    }

    static void AppendEvent(string line)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                File.AppendAllText(eventLog, $"{DateTime.Now:HH:mm:ss.fff} | {line}\n");
                return;
            }
            catch (IOException) { Thread.Sleep(20); }
        }
    }

    static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_SETFOCUS: Win32.SetFocus(hEdit); return 0;
            case Win32.WM_CLOSE:
                AppendEvent("wmclose");
                Win32.DestroyWindow(hwnd);
                return 0;
            case Win32.WM_DESTROY:
                AppendEvent("destroy");
                Win32.PostQuitMessage(0);
                return 0;
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    static int Fail(string what)
    {
        AppendEvent($"error | {what}");
        return 3;
    }
}
