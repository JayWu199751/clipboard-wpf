using System.IO;
using System.Runtime.InteropServices;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 独占段内的剪贴板写原语（测试替身 seam；真实实现逐条转发 user32）。
/// Empty 之后旧内容不可恢复——失败语义以此为界。
/// </summary>
public interface IClipboardWritePrimitives
{
    /// <summary>EmptyClipboard。false=清空失败（极罕见；剪贴板保持原样，写入放弃）。</summary>
    bool Empty();

    /// <summary>SetClipboardData：写入已备好的全局内存句柄。成功后所有权移交系统；
    /// 失败时句柄仍归调用方（需自行 GlobalFree）。</summary>
    bool SetHandle(uint format, IntPtr handle);
}

/// <summary>
/// 写剪贴板（F11）：文字 = CF_UNICODETEXT；图片 = 位图内容（T06，保透明通道，
/// 兼容截图工具/Office）——CF_DIBV5 主格式 + CF_DIB 兼容格式，绝不写文件路径。
/// 打开重试失败或载荷构造失败返回 false（调用方基线不动、链路报条目不可用）。
/// 失败语义（终审 Standards）：全局内存句柄在 EmptyClipboard 前全部备好——清空前失败
/// 剪贴板原样；清空后任一写入失败返回 false，此时旧内容已不可恢复，调用方按
/// 「剪贴板已被破坏」处理（明确不承诺还原旧内容）。
/// </summary>
public sealed class ClipboardWriter : IClipboardWriter
{
    private const int OpenAttempts = 8;

    private sealed class RealPrimitives : IClipboardWritePrimitives
    {
        public static readonly RealPrimitives Instance = new();

        public bool Empty() => NativeMethods.EmptyClipboard();

        public bool SetHandle(uint format, IntPtr handle) =>
            NativeMethods.SetClipboardData(format, handle) != IntPtr.Zero;
    }

    private readonly IClipboardWritePrimitives _primitives;

    public ClipboardWriter() : this(RealPrimitives.Instance)
    {
    }

    internal ClipboardWriter(IClipboardWritePrimitives primitives) => _primitives = primitives;

    public bool WriteText(string text)
    {
        using var lease = ClipboardOps.Acquire(OpenAttempts);
        if (lease is null)
        {
            return false;
        }

        var handle = AllocUtf16(text); // 句柄在清空前备好：分配失败时剪贴板原样
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        if (!_primitives.Empty())
        {
            _ = NativeMethods.GlobalFree(handle);
            return false;
        }
        if (_primitives.SetHandle(NativeMethods.CF_UNICODETEXT, handle))
        {
            return true;
        }
        _ = NativeMethods.GlobalFree(handle);
        return false; // 已清空但写入失败：旧内容不可恢复（见类头失败语义）
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
            return false; // 文件丢失/读不出 → 条目内容不可用（独占段之外，剪贴板未动）
        }
        if (dibV5 is null || dib is null)
        {
            return false; // 载荷构造失败（独占段之外，剪贴板未动）
        }

        using var lease = ClipboardOps.Acquire(OpenAttempts);
        if (lease is null)
        {
            return false;
        }

        // 两格式句柄在 EmptyClipboard 前全部备好：此后失败面只剩两次系统调用
        var v5Handle = GlobalAllocBytes(dibV5);
        if (v5Handle == IntPtr.Zero)
        {
            return false; // 尚未清空：剪贴板原样
        }
        var dibHandle = GlobalAllocBytes(dib);
        if (dibHandle == IntPtr.Zero)
        {
            _ = NativeMethods.GlobalFree(v5Handle);
            return false; // 尚未清空：剪贴板原样
        }

        if (!_primitives.Empty())
        {
            _ = NativeMethods.GlobalFree(v5Handle);
            _ = NativeMethods.GlobalFree(dibHandle);
            return false; // 清空失败（极罕见）：剪贴板保持原样
        }

        // 以下任一失败：剪贴板已被清空且旧内容不可恢复——返回 false，调用方按
        // 「剪贴板已被破坏」处理（基线不动、链路报条目不可用）；已成功格式的句柄
        // 已移交系统不可回收，失败格式未移交需 GlobalFree。
        if (!_primitives.SetHandle(NativeMethods.CF_DIBV5, v5Handle))
        {
            _ = NativeMethods.GlobalFree(v5Handle);
            _ = NativeMethods.GlobalFree(dibHandle);
            return false;
        }
        if (!_primitives.SetHandle(NativeMethods.CF_DIB, dibHandle))
        {
            _ = NativeMethods.GlobalFree(dibHandle);
            return false;
        }
        return true;
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
