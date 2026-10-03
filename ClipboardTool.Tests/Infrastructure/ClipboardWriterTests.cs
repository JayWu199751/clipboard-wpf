using ClipboardTool.Infrastructure.Windows;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 写剪贴板的失败语义（终审 Standards 修复）：全局内存句柄全部在 EmptyClipboard 前备好，
/// 「已清空但报未写」的中间态消除；Empty 之后任一写入失败返回 false——此时旧内容已
/// 不可恢复，调用方按「剪贴板已被破坏」处理（明确不承诺还原）。写原语经
/// IClipboardWritePrimitives 替身注入钉住调用序列与失败分支。
/// </summary>
public class ClipboardWriterTests
{
    private static readonly string SamplePngPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "images", "sample.png"));

    private sealed class FakePrimitives : IClipboardWritePrimitives
    {
        public bool EmptyResult = true;
        public bool EmptyCalled;
        public uint FailOnFormat;
        public List<(uint Format, IntPtr Handle)> Sets = [];

        public bool Empty()
        {
            EmptyCalled = true;
            return EmptyResult;
        }

        public bool SetHandle(uint format, IntPtr handle)
        {
            if (format == FailOnFormat)
            {
                return false;
            }
            Sets.Add((format, handle));
            return true;
        }
    }

    [Fact]
    public void 图片写入成功_先清空后连续两格式_句柄均非零且不同()
    {
        var fake = new FakePrimitives();
        var writer = new ClipboardWriter(fake);

        Assert.True(writer.WriteImage(SamplePngPath));
        Assert.True(fake.EmptyCalled);
        Assert.Equal(2, fake.Sets.Count);
        Assert.Equal(NativeMethods.CF_DIBV5, fake.Sets[0].Format); // 主格式先行（legacy 口径）
        Assert.Equal(NativeMethods.CF_DIB, fake.Sets[1].Format);
        Assert.All(fake.Sets, s => Assert.NotEqual(IntPtr.Zero, s.Handle));
        Assert.NotEqual(fake.Sets[0].Handle, fake.Sets[1].Handle); // 两格式各占一个句柄
    }

    [Fact]
    public void 图片写入_兼容格式失败_仍报失败_句柄语义明确()
    {
        // DIBV5 成功、DIB 失败：剪贴板已被清空且只含部分格式——返回 false，
        // 调用方按剪贴板已被破坏处理（明确失败语义，不假装成功）
        var fake = new FakePrimitives { FailOnFormat = NativeMethods.CF_DIB };
        var writer = new ClipboardWriter(fake);

        Assert.False(writer.WriteImage(SamplePngPath));
        Assert.True(fake.EmptyCalled);
        Assert.Single(fake.Sets); // 主格式已写入（句柄已移交系统，不可回收）
    }

    [Fact]
    public void 图片写入_清空失败_放弃写入不碰后续()
    {
        var fake = new FakePrimitives { EmptyResult = false };
        var writer = new ClipboardWriter(fake);

        Assert.False(writer.WriteImage(SamplePngPath));
        Assert.True(fake.EmptyCalled);
        Assert.Empty(fake.Sets); // 未清空成功：一次 SetHandle 都不发生
    }

    [Fact]
    public void 文字写入成功_先备句柄再清空再写入()
    {
        var fake = new FakePrimitives();
        var writer = new ClipboardWriter(fake);

        Assert.True(writer.WriteText("测试文本"));
        Assert.True(fake.EmptyCalled);
        var set = Assert.Single(fake.Sets);
        Assert.Equal(NativeMethods.CF_UNICODETEXT, set.Format);
        Assert.NotEqual(IntPtr.Zero, set.Handle);
    }

    [Fact]
    public void 文字写入_清空失败_放弃写入()
    {
        var fake = new FakePrimitives { EmptyResult = false };
        var writer = new ClipboardWriter(fake);

        Assert.False(writer.WriteText("测试文本"));
        Assert.Empty(fake.Sets);
    }
}
