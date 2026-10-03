using System.IO;
using System.Runtime.InteropServices;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 写剪贴板（F11）：文字 = CF_UNICODETEXT；图片 = 位图内容（T06，保透明通道，
/// 兼容截图工具/Office）——CF_DIBV5 主格式 + CF_DIB 兼容格式，绝不写文件路径。
/// 打开重试失败或载荷构造失败返回 false（调用方基线不动、链路报条目不可用）。
/// </summary>
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

    /// <summary>
    /// 写图片位图（T06）：PNG 文件按需读取 → DIBV5+DIB 载荷 → SetClipboardData。
    /// 文件读取与 DIB 构造都在独占段之外（载荷可能几十 MB，抱着剪贴板做正是 F10 要防的）。
    /// </summary>
    public bool WriteImage(string pngPath)
    {
        byte[]? dibV5;
        byte[]? dib;
        try
        {
            var png = File.ReadAllBytes(pngPath);
            dibV5 = DibImageEncoder.BuildDibV5FromPng(png);
            dib = DibImageEncoder.BuildDibFromPng(png);
        }
        catch (Exception)
        {
            return false; // 文件丢失/读不出 → 条目内容不可用
        }
        if (dibV5 is null)
        {
            return false;
        }

        using var lease = ClipboardOps.Acquire(OpenAttempts);
        if (lease is null)
        {
            return false;
        }

        _ = NativeMethods.EmptyClipboard();
        // 两个格式各占一个全局句柄；成功后所有权移交系统，失败才自行释放
        return SetData(NativeMethods.CF_DIBV5, dibV5) && SetData(NativeMethods.CF_DIB, dib!);
    }

    private static bool SetData(uint format, byte[] payload)
    {
        var handle = GlobalAllocBytes(payload);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        if (NativeMethods.SetClipboardData(format, handle) != IntPtr.Zero)
        {
            return true;
        }
        _ = NativeMethods.GlobalFree(handle);
        return false;
    }

    private static IntPtr GlobalAllocBytes(byte[] bytes)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
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
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return handle;
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }
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
