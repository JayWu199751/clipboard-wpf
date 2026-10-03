using ClipboardTool.Domain.History;
using Xunit;

namespace ClipboardTool.Tests.HistoryRules;

/// <summary>轮询基线语义测试（legacy poll_baseline.rs 9 例的移植）。纯数据表驱动，不碰剪贴板。</summary>
public sealed class PollBaselineTests
{
    private static byte[] Png(string tag) => System.Text.Encoding.UTF8.GetBytes(tag);

    /// <summary>observe + confirm(ok)：一次完整轮询。</summary>
    private static BaselineChange? Round(PollBaseline baseline, byte[]? image, string text)
    {
        var change = baseline.Observe(image, text);
        baseline.Confirm(true);
        return change;
    }

    [Fact]
    public void 新文字算一次复制_同文字与空文字不算()
    {
        var baseline = new PollBaseline();
        Assert.Equal("abc", Assert.IsType<BaselineChange.Text>(Round(baseline, null, "abc")).Value);
        Assert.Null(Round(baseline, null, "abc"));
        Assert.Null(Round(baseline, null, ""));
        Assert.Equal("abd", Assert.IsType<BaselineChange.Text>(Round(baseline, null, "abd")).Value);
    }

    [Fact]
    public void 图片按内容哈希判定_同图不重复报_换图才报()
    {
        var baseline = new PollBaseline();
        var first = Round(baseline, Png("img1"), "");
        var image = Assert.IsType<BaselineChange.Image>(first);
        Assert.Equal(PollBaseline.Sha1Hex(Png("img1")), image.Hash);

        Assert.Null(Round(baseline, Png("img1"), ""));
        Assert.IsType<BaselineChange.Image>(Round(baseline, Png("img2"), ""));
    }

    [Fact]
    public void 图片未变时文字基线仍跟上_但不清图片基线()
    {
        var baseline = new PollBaseline();
        Round(baseline, Png("img1"), "旧文字");

        // 同一张图 + 新文字：不报新复制（图片优先），但文字基线要跟上
        Assert.Null(Round(baseline, Png("img1"), "新文字"));
        Assert.Equal("新文字", baseline.TextForTest);
        Assert.Equal(PollBaseline.Sha1Hex(Png("img1")), baseline.ImageHashForTest);
    }

    [Fact]
    public void 图片写盘失败_基线不动并置重试_下一轮仍判定为新()
    {
        var baseline = new PollBaseline();
        baseline.NoteSeq(1);
        Assert.IsType<BaselineChange.Image>(baseline.Observe(Png("img1"), ""));
        baseline.Confirm(false);
        Assert.False(baseline.SkipUnchanged(1), "有待重试的写盘失败时不能短路");

        // 基线没动，所以同一张图仍会被再次尝试
        Assert.IsType<BaselineChange.Image>(baseline.Observe(Png("img1"), ""));
        baseline.Confirm(true);
        Assert.True(baseline.SkipUnchanged(1), "重试已落地，序列号未变即可短路");
        Assert.Null(baseline.Observe(Png("img1"), ""));
    }

    [Fact]
    public void 图片切回文字算新复制_并清空图片基线()
    {
        var baseline = new PollBaseline();
        Round(baseline, Png("img1"), "");
        Assert.Equal("文字", Assert.IsType<BaselineChange.Text>(Round(baseline, null, "文字")).Value);
        Assert.True(baseline.ImageHashForTest.Length == 0);

        // 图片基线清空后，同一张图再来仍算新复制
        Assert.IsType<BaselineChange.Image>(Round(baseline, Png("img1"), ""));
    }

    [Fact]
    public void 序列号短路_待重试与取不到序列号时不短路()
    {
        var baseline = new PollBaseline();
        baseline.NoteSeq(7);
        Assert.True(baseline.SkipUnchanged(7));
        Assert.False(baseline.SkipUnchanged(8));
        Assert.False(baseline.SkipUnchanged(0), "取不到序列号时保守照常轮询");

        baseline.Observe(Png("img"), "");
        baseline.Confirm(false);
        Assert.False(baseline.SkipUnchanged(7), "有待重试的写盘失败时不能短路");
    }

    [Fact]
    public void sync_now把自己写入的内容认作基线()
    {
        var baseline = new PollBaseline();
        baseline.SyncNow(null, "我刚复制的");
        Assert.Null(Round(baseline, null, "我刚复制的"));

        baseline.SyncNow(Png("img1"), "残留文字");
        Assert.Null(Round(baseline, Png("img1"), "残留文字"));

        // sync_now 同时清掉待重试标志：自己刚写入的内容就是新基线，旧的一次写盘不必再补
        baseline.Observe(Png("img2"), "");
        baseline.Confirm(false);
        baseline.NoteSeq(9);
        Assert.False(baseline.SkipUnchanged(9), "置了重试标志后不能短路");
        baseline.SyncNow(Png("img1"), "残留文字");
        Assert.True(baseline.SkipUnchanged(9), "sync_now 应清掉重试标志");
    }

    [Fact]
    public void 读不可信的一轮不推进序列号_下一轮同一内容仍算新复制()
    {
        var baseline = new PollBaseline();
        Round(baseline, null, "上一次复制");
        baseline.NoteSeq(41);

        // 用户复制了新内容（序列号 41 → 42），但这一轮剪贴板被别的程序占着、读不到。
        // 这里绝不能走 observe(null, "") 那条「空文字不算变化」的路——那会把基线记成空，
        // 这次复制就从基线上消失了，下一轮还会被序列号短路吃掉。
        baseline.NoteUntrusted();
        Assert.True(baseline.RetryPending, "读不可信必须留下待重试标记");
        Assert.False(baseline.SkipUnchanged(41), "读不可信的一轮即使序列号没变也不许短路");

        // 稍后重试读到真实内容：相对基线仍是新复制（这就是 P0 的回归点）
        Assert.Equal("这一次复制", Assert.IsType<BaselineChange.Text>(Round(baseline, null, "这一次复制")).Value);
        Assert.False(baseline.RetryPending, "重试成功后标记应清掉");
    }

    [Fact]
    public void 读不可信不得清空图片基线()
    {
        var baseline = new PollBaseline();
        Round(baseline, Png("img1"), "");

        baseline.NoteUntrusted();
        Assert.Equal(PollBaseline.Sha1Hex(Png("img1")), baseline.ImageHashForTest);

        // 重试读到同一张图：相对基线没变，不报新复制
        Assert.Null(Round(baseline, Png("img1"), ""));
    }
}
