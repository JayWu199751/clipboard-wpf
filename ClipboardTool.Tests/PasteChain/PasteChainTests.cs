using ClipboardTool.Domain.PasteChain;
using Xunit;

namespace ClipboardTool.Tests.PasteChainRules;

/// <summary>
/// 粘贴链路顺序契约测试（legacy paste_chain.rs 9 例的移植）：假端口记录调用顺序与脚本化结果，
/// 顺序与文案因此可断言。改动链路前先看这些测试。
/// </summary>
public sealed class PasteChainTests
{
    private static FocusTarget Target() => new(Hwnd: 1, FocusHwnd: 2, Pid: 3, Tid: 4);

    /// <summary>假端口：记录调用顺序与脚本化结果，不碰剪贴板/Win32/面板。</summary>
    private sealed class FakePort : IPastePort
    {
        public List<string> Log { get; } = [];
        public List<(string Stage, string Reason)> Errors { get; } = [];
        public CopyContent? Content { get; set; } = new CopyContent.Text("你好");
        public bool WriteOk { get; set; } = true;
        public FocusTarget? Snapshot { get; set; } = Target();
        public RestoreFailure? PasteFailure { get; set; }

        public CopyContent? ContentOf(string id)
        {
            Log.Add($"content_of({id})");
            return Content;
        }

        public bool WriteClipboard(CopyContent content)
        {
            Log.Add(content switch
            {
                CopyContent.Text => "write_clipboard(text)",
                CopyContent.Image => "write_clipboard(image)",
                _ => "write_clipboard(?)",
            });
            return WriteOk;
        }

        public void SettleAfterCopy(string id) => Log.Add($"settle_after_copy({id})");

        public FocusTarget? FocusTarget()
        {
            Log.Add("focus_target");
            return Snapshot;
        }

        public RestoreFailure? RestoreAndPaste(FocusTarget target)
        {
            Log.Add("restore_and_paste");
            return PasteFailure;
        }

        public void HideAfterPaste() => Log.Add("hide_after_paste");

        public void ReportFocusError(string stage, string reason)
        {
            Log.Add($"report_focus_error({stage})");
            Errors.Add((stage, reason));
        }
    }

    [Fact]
    public void 成功链路_按五步顺序执行并隐藏面板()
    {
        var port = new FakePort();
        var result = PasteChain.Run(port, "abc");

        Assert.True(result.Ok);
        Assert.Equal(PasteChain.PastedOk, result.Message);
        Assert.Equal(
        [
            "content_of(abc)",
            "write_clipboard(text)",
            "settle_after_copy(abc)",
            "focus_target",
            "restore_and_paste",
            "hide_after_paste",
        ], port.Log);
        Assert.Empty(port.Errors);
    }

    [Fact]
    public void 图片条目_按文件路径写剪贴板()
    {
        var port = new FakePort { Content = new CopyContent.Image("C:/tmp/a.png") };
        var result = PasteChain.Run(port, "img");

        Assert.True(result.Ok);
        Assert.Equal("write_clipboard(image)", port.Log[1]);
    }

    [Fact]
    public void 条目取不到_不写剪贴板也不碰焦点()
    {
        var port = new FakePort { Content = null };
        var result = PasteChain.Run(port, "gone");

        Assert.False(result.Ok);
        Assert.Equal(PasteChain.EntryUnavailable, result.Message);
        Assert.Equal(["content_of(gone)"], port.Log);
    }

    [Fact]
    public void 写剪贴板失败_不落位不粘贴()
    {
        var port = new FakePort { WriteOk = false };
        var result = PasteChain.Run(port, "abc");

        Assert.False(result.Ok);
        Assert.Equal(PasteChain.EntryUnavailable, result.Message);
        Assert.Equal(["content_of(abc)", "write_clipboard(text)"], port.Log);
    }

    [Fact]
    public void 落位先于注入_粘贴失败列表仍反映这次复制()
    {
        var port = new FakePort { PasteFailure = new RestoreFailure("restore", "restore_failed") };
        var result = PasteChain.Run(port, "abc");

        Assert.False(result.Ok);
        var settle = port.Log.IndexOf("settle_after_copy(abc)");
        var paste = port.Log.IndexOf("restore_and_paste");
        Assert.True(settle < paste, $"落位必须在注入之前：{string.Join(",", port.Log)}");
    }

    [Fact]
    public void 无焦点快照_中止注入且不隐藏面板()
    {
        var port = new FakePort { Snapshot = null };
        var result = PasteChain.Run(port, "abc");

        Assert.False(result.Ok);
        Assert.Equal(PasteChain.RestoreFailedMessage, result.Message);
        Assert.Equal(
        [
            "content_of(abc)",
            "write_clipboard(text)",
            "settle_after_copy(abc)",
            "focus_target",
            "report_focus_error(restore)",
        ], port.Log);
        Assert.Equal([("restore", "no_focus_target")], port.Errors);
        Assert.DoesNotContain("hide_after_paste", port.Log);
    }

    [Fact]
    public void 恢复焦点失败_文案按restore_不隐藏面板()
    {
        var port = new FakePort { PasteFailure = new RestoreFailure("restore", "restore_failed") };
        var result = PasteChain.Run(port, "abc");

        Assert.False(result.Ok);
        Assert.Equal(PasteChain.RestoreFailedMessage, result.Message);
        Assert.DoesNotContain("hide_after_paste", port.Log);
        Assert.Equal([("restore", "restore_failed")], port.Errors);
    }

    [Fact]
    public void 注入失败_文案按paste_内容已在剪贴板()
    {
        var port = new FakePort { PasteFailure = new RestoreFailure("paste", "paste_send_failed") };
        var result = PasteChain.Run(port, "abc");

        Assert.False(result.Ok);
        Assert.Equal(PasteChain.PasteFailedMessage, result.Message);
        Assert.DoesNotContain("hide_after_paste", port.Log);
        Assert.Equal([("paste", "paste_send_failed")], port.Errors);
    }

    /// <summary>同源约束：CopyResult.message 必须等于焦点错误事件里那个 stage 的文案，
    /// 两处只经 FocusErrorMessage 这一个函数。</summary>
    [Fact]
    public void 失败文案与面板事件同源()
    {
        foreach (var stage in (string[])["restore", "paste"])
        {
            var port = new FakePort { PasteFailure = new RestoreFailure(stage, "x") };
            var result = PasteChain.Run(port, "abc");
            var eventStage = port.Errors[0].Stage;
            Assert.Equal(stage, eventStage);
            Assert.Equal(PasteChain.FocusErrorMessage(eventStage), result.Message);
        }
    }
}
