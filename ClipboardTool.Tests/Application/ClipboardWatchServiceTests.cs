using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.PasteChain;
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

        public bool WriteImage(string pngPath) => Result;
    }

    /// <summary>来源采集替身（票 14）：可配置返回值/异常，记录调用次数供「无变化轮不采集」断言。</summary>
    private sealed class FakeForegroundSource : IForegroundSource
    {
        public SourceApp? Source { get; set; } = new SourceApp(ExePath: @"C:\app\notes.exe", AppName: "notes");

        public Exception? Throw { get; set; }

        public int CaptureCalls { get; private set; }

        public SourceApp? Capture()
        {
            CaptureCalls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Source;
        }
    }

    private static ClipboardSnapshot Snapshot(byte[]? png, string text = "") =>
        new(text, png);

    private static HistoryService NewHistory(FakeImageFiles? images = null) =>
        new(new HistoryStore(
            HistoryStore.DefaultMaxHistory, images ?? new FakeImageFiles(),
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000));

    [Fact]
    public void 图片入史_一轮落库并接受基线_同内容不再重录()
    {
        var reader = new FakeReader();
        var images = new FakeImageFiles();
        var history = new HistoryService(new HistoryStore(
            HistoryStore.DefaultMaxHistory, images,
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000));
        var watch = new ClipboardWatchService(
            reader, new FakeSequence(), history, new FakeWriter(), new FakeForegroundSource());

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
            reader, new FakeSequence(), history, new FakeWriter(), new FakeForegroundSource());

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
            reader, new FakeSequence(), history, new FakeWriter(), new FakeForegroundSource());

        Assert.False(watch.PollRound());
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void WriteAndSyncImage_写失败基线不动_写成功基线跟上()
    {
        // 粘贴链第 2/3 步的图片侧（与文字同一条原子化规则）：
        // 写失败返回 false 且基线不动（欠账）；写成功后基线立即认作当前内容，
        // 否则自写内容会在下一轮被当成新复制重复提升（粘贴后列表闪烁）。
        var reader = new FakeReader();
        var seq = new FakeSequence();
        var history = new HistoryService(new HistoryStore(
            HistoryStore.DefaultMaxHistory, new FakeImageFiles(),
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000));
        var writer = new FakeWriter();
        var watch = new ClipboardWatchService(reader, seq, history, writer, new FakeForegroundSource());

        writer.Result = false;
        Assert.False(watch.WriteAndSyncImage(@"C:\x\missing.png"));

        writer.Result = true;
        Assert.True(watch.WriteAndSyncImage(@"C:\x\ok.png"));

        // 基线已跟上：同序列号下一轮短路，不会把自写内容当新复制
        reader.Outcome = new ClipboardReadOutcome.Known(Snapshot(null, "自写内容"));
        Assert.True(watch.PollRound());
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void ContentOf_图片条目返回Png路径()
    {
        // 粘贴链第 1 步（content_of）图片侧：按需读盘的路径交给写侧（T06），
        // 条目不存在返回 null（EntryUnavailable 文案）。
        var history = new HistoryService(new HistoryStore(
            HistoryStore.DefaultMaxHistory, new FakeImageFiles(),
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000));
        var watch = new ClipboardWatchService(
            new FakeReader(), new FakeSequence(), history, new FakeWriter(), new FakeForegroundSource());
        var paste = new PasteService(
            history, watch,
            capturedFocus: () => null,
            restoreAndPaste: _ => null,
            hidePanel: () => { },
            reportFocusError: (_, _) => { });

        history.RecordImage("png"u8.ToArray());
        var entry = Assert.Single(history.Entries);

        Assert.Equal(new CopyContent.Image(entry.ImagePath!), paste.ContentOf(entry.Id));
        Assert.Null(paste.ContentOf("missing"));
    }

    // ---------- 来源应用采集（F06，票 14） ----------

    [Fact]
    public void 文字记录携带前台来源应用()
    {
        var reader = new FakeReader();
        var source = new FakeForegroundSource
        {
            Source = new SourceApp(
                ExePath: @"C:\Windows\System32\notepad.exe", AppName: "notepad", WindowTitle: "示例 - 记事本"),
        };
        var history = NewHistory();
        var watch = new ClipboardWatchService(reader, new FakeSequence(), history, new FakeWriter(), source);

        reader.Outcome = new ClipboardReadOutcome.Known(Snapshot(null, "新复制文字"));
        Assert.True(watch.PollRound());

        var entry = Assert.Single(history.Entries);
        Assert.Equal("notepad", entry.SourceApp!.AppName);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", entry.SourceApp.ExePath);
        Assert.Equal("示例 - 记事本", entry.SourceApp.WindowTitle);
        Assert.Equal(1, source.CaptureCalls);
    }

    [Fact]
    public void 图片记录携带前台来源应用()
    {
        var reader = new FakeReader();
        var source = new FakeForegroundSource { Source = new SourceApp(AppName: "pixpin") };
        var history = NewHistory();
        var watch = new ClipboardWatchService(reader, new FakeSequence(), history, new FakeWriter(), source);

        reader.Outcome = new ClipboardReadOutcome.Known(Snapshot("png-bytes"u8.ToArray()));
        Assert.True(watch.PollRound());

        var entry = Assert.Single(history.Entries);
        Assert.Equal(EntryKind.Image, entry.Type);
        Assert.Equal("pixpin", entry.SourceApp!.AppName);
        Assert.Equal(1, source.CaptureCalls);
    }

    [Fact]
    public void 采集不可得_按未知来源落库_SourceApp为null()
    {
        // legacy 口径：无前台/查不到进程 → None → 存档 sourceApp=null，卡片显示「未知来源」
        var reader = new FakeReader();
        var source = new FakeForegroundSource { Source = null };
        var history = NewHistory();
        var watch = new ClipboardWatchService(reader, new FakeSequence(), history, new FakeWriter(), source);

        reader.Outcome = new ClipboardReadOutcome.Known(Snapshot(null, "无来源文字"));
        Assert.True(watch.PollRound());

        var entry = Assert.Single(history.Entries);
        Assert.Null(entry.SourceApp);
    }

    [Fact]
    public void 采集抛异常_记录链路不阻塞_按未知来源落库()
    {
        // 判据 3：采集任何意外失败都不得阻塞记录链路——条目照常落库（丢来源不丢内容）
        var reader = new FakeReader();
        var source = new FakeForegroundSource { Throw = new InvalidOperationException("boom") };
        var history = NewHistory();
        var watch = new ClipboardWatchService(reader, new FakeSequence(), history, new FakeWriter(), source);

        reader.Outcome = new ClipboardReadOutcome.Known(Snapshot(null, "异常防护文字"));
        Assert.True(watch.PollRound());

        var entry = Assert.Single(history.Entries);
        Assert.Equal("异常防护文字", entry.Text);
        Assert.Null(entry.SourceApp);
    }

    [Fact]
    public void 无变化轮不采集_短路先行()
    {
        // 采集只发生在判定出真实变化的记录轮：序列号短路的空转轮一次都不取前台
        var reader = new FakeReader();
        var source = new FakeForegroundSource();
        var history = NewHistory();
        var watch = new ClipboardWatchService(reader, new FakeSequence(), history, new FakeWriter(), source);

        reader.Outcome = new ClipboardReadOutcome.Known(Snapshot(null, "初始内容"));
        Assert.True(watch.PollRound());
        Assert.Equal(1, source.CaptureCalls);

        // 同序列号再轮：短路，不采集、不重录
        Assert.True(watch.PollRound());
        Assert.Equal(1, source.CaptureCalls);
        _ = Assert.Single(history.Entries);
    }
}
