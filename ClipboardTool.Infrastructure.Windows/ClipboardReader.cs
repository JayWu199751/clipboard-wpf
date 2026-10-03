using System.Runtime.InteropServices;
using System.Text;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 剪贴板独占读取（legacy clipboard.rs 的 read 移植，T02 文字段）：
/// OpenClipboard 小步重试、字节取完立即关闭、UTF-16 解码在独占段之外（F10 硬约束——
/// 抱着 CloseClipboard 解码的几十毫秒正是用户刚按下 Ctrl+C 的时刻，会挤掉别人的 OpenClipboard）。
/// 「打开失败（不可信）」与「打开了但没内容（可信）」三态分开。
/// </summary>
public sealed class ClipboardReader : IClipboardReader
{
    /// <summary>剪贴板随时可能被别的程序短暂占用：小步重试，实在拿不到就放弃本轮（600ms 后还会再来）。</summary>
    private const int OpenAttempts = 8;

    public ClipboardReadOutcome Read()
    {
        byte[]? bytes;
        using (var lease = ClipboardOps.Acquire(OpenAttempts))
        {
            if (lease is null)
            {
                return new ClipboardReadOutcome.Occupied();
            }

            // 字节在独占段内拷出；解码在段外做（F10 硬约束——抱着剪贴板解码的
            // 几十毫秒正是用户刚按下 Ctrl+C 的时刻，会挤掉别人的 OpenClipboard）
            bytes = ReadBytes(NativeMethods.CF_UNICODETEXT);
        }

        var text = bytes is null ? string.Empty : DecodeUtf16(bytes);
        return new ClipboardReadOutcome.Known(new ClipboardSnapshot(text, Png: null));
    }

    /// <summary>取某格式的原始字节拷贝。返回的内存句柄归剪贴板所有：只读不动、绝不释放。须在守卫内调用。</summary>
    private static byte[]? ReadBytes(uint format)
    {
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        var size = NativeMethods.GlobalSize(handle);
        if (size == UIntPtr.Zero)
        {
            return null;
        }
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            _ = NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>CF_UNICODETEXT 以双 NUL 结尾且可能带尾部填充：截到第一个 NUL 再解码（在独占段之外）。</summary>
    private static string DecodeUtf16(byte[] bytes)
    {
        var charCount = bytes.Length / 2;
        for (var i = 0; i < charCount; i++)
        {
            if (bytes[i * 2] == 0 && bytes[i * 2 + 1] == 0)
            {
                charCount = i;
                break;
            }
        }
        return Encoding.Unicode.GetString(bytes, 0, charCount * 2);
    }
}

/// <summary>剪贴板序列号读取（GetClipboardSequenceNumber，无需打开剪贴板）。</summary>
public sealed class ClipboardSequenceReader : IClipboardSequence
{
    public uint Current() => NativeMethods.GetClipboardSequenceNumber();
}
