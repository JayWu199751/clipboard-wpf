using System.Runtime.InteropServices;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>写剪贴板（T02 文字段）：打开重试 → Empty → SetClipboardData(CF_UNICODETEXT) → 关闭。</summary>
public sealed class ClipboardWriter : IClipboardWriter
{
    private const int OpenAttempts = 8;

    public bool WriteText(string text)
    {
        using var lease = ClipboardOps.Acquire(OpenAttempts);
        if (lease is null)
        {
            return false;
        }

        _ = NativeMethods.EmptyClipboard();
        var handle = AllocUtf16(text);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        // 成功后所有权移交系统，不再 GlobalFree；失败才需自行释放
        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) != IntPtr.Zero)
        {
            return true;
        }
        _ = NativeMethods.GlobalFree(handle);
        return false;
    }

    private static IntPtr AllocUtf16(string text)
    {
        var bytes = (text.Length + 1) * 2;
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            _ = NativeMethods.GlobalFree(handle);
            return IntPtr.Zero;
        }
        try
        {
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0);
            return handle;
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }
    }
}
