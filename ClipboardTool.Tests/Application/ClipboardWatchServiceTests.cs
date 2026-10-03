using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Tests.HistoryRules;
using Xunit;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// 剪贴板监听的图片链路（T06）：轮内读 → 基线判定 → 落库 → confirm。
/// 重点钉工单红绿两条：写图失败不确认新内容（欠账重试）、落库成功基线跟上不重录。
/// </summary>
public sealed class ClipboardWatchServiceTests
{
    private sealed class FakeReader : IClipboardReader
    {
        public ClipboardReadOutcome Outcome { get; set; } = new ClipboardReadOutcome.Known(new ClipboardSnapshot(string.Empty, null));

        public ClipboardReadOutcome Read() => Outcome;
    }

    private sealed class FakeSequence : IClipboardSequence
    {
        public uint Value { get; set; } = 100;

        public uint Current() => Value;
    }

    private sealed class FakeWriter : IClipboardWriter
    {
        public bool Result { get; set; } = true;

        public bool WriteText(string text) => Result;
    }

    private static ClipboardSnapshot Snapshot(byte[]? png, string text = "") =>
        new(text, png);

    [Fact]
    public void 图片入史_一轮落库并接受基线_同内容不再重录()
    {
        var reader = new FakeReader();
        var images = new FakeImageFiles();
        var history = new HistoryService(new HistoryStore(
            HistoryStore.DefaultMaxHistory, images,
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000));
        var watch = new ClipboardWatchService(
            reader, new FakeSequence(), history, new FakeWriter());

        reader.Outcome = new ClipboardReadOutcome.Known(Snapshot("png-bytes"u8.ToArray()));
        Assert.True(watch.PollRound());
        var entry = Assert.Single(history.Entries);
        Assert.Equal(EntryKind.Image, entry.Type);

        // 基线已接受：序列号不变时本轮短路，不会重复落库
        var count = history.Entries.Count;
        Assert.True(watch.PollRound());
        Assert.Equal(count, history.Entries.Count);
        Assert.Equal(1, images.SaveCalls);
    }

    [Fact]
    public void 写图失败_不确认新内容_欠账重试到成功()
    {
        var reader = new FakeReader
        {
            Outcome = new ClipboardReadOutcome.Known(Snapshot("png-bytes"u8.ToArray())),
        };
        var images = new FakeImageFiles { SaveOverride = (_, _) => null }; // 写盘恒失败
        var history = new HistoryService(new HistoryStore(
            HistoryStore.DefaultMaxHistory, images,
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000));
        var watch = new ClipboardWatchService(
            reader, new FakeSequence(), history, new FakeWriter());

        Assert.False(watch.PollRound()); // 失败轮：欠账未清，要求稍后再试
        Assert.Empty(history.Entries);

        // 内容原地不动，下一轮重试成功：落库一次、基线接受
        images.SaveOverride = null;
        Assert.True(watch.PollRound());
        var entry = Assert.Single(history.Entries);
        Assert.Equal(EntryKind.Image, entry.Type);
    }

    [Fact]
    public void 占用轮_不落库不推进基线()
    {
        var reader = new FakeReader { Outcome = new ClipboardReadOutcome.Occupied() };
        var history = new HistoryService(new HistoryStore(
            HistoryStore.DefaultMaxHistory, new FakeImageFiles(),
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000));
        var watch = new ClipboardWatchService(
            reader, new FakeSequence(), history, new FakeWriter());

        Assert.False(watch.PollRound());
        Assert.Empty(history.Entries);
    }
}
