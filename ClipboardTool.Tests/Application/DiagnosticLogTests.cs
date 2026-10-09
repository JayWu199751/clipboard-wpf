using ClipboardTool.Application;

namespace ClipboardTool.Tests.Application;

public sealed class DiagnosticLogTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void 关键读数无条件写而详细事件只接受精确门禁值(string? setting, bool verbose)
    {
        var sink = new RecordingSink();
        var log = new DiagnosticLog(sink, setting, 1234);
        log.Vital("start ui=wpf");
        log.Verbose("clipboard-round settled=True");
        Assert.Equal(verbose ? 2 : 1, sink.Diagnostic.Count);
        Assert.Contains(" vital pid=1234 start ui=wpf", sink.Diagnostic[0]);
        Assert.True(DateTimeOffset.TryParse(sink.Diagnostic[0].Split(' ')[0], out _));
        if (verbose)
        {
            Assert.Contains(" verbose pid=1234 clipboard-round", sink.Diagnostic[1]);
        }
    }

    [Fact]
    public void 事件换行不能伪造成另一条日志()
    {
        var sink = new RecordingSink();
        new DiagnosticLog(sink, null, 1).Vital("start\r\nfake-exit");
        Assert.Single(sink.Diagnostic[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("start  fake-exit", sink.Diagnostic[0]);
    }

    [Fact]
    public void 异常保留类型堆栈和嵌套原因但不泄漏异常消息中的正文()
    {
        var sink = new RecordingSink();
        var log = new DiagnosticLog(sink, null, 42);
        try
        {
            ThrowWithPrivateMessage();
        }
        catch (Exception exception)
        {
            log.Panic("executor", new InvalidOperationException("private-clipboard", exception));
        }
        var panic = Assert.Single(sink.Panic);
        Assert.Contains("panic pid=42 source=executor", panic);
        Assert.Contains("System.InvalidOperationException", panic);
        Assert.Contains("System.IO.IOException", panic);
        Assert.Contains(nameof(ThrowWithPrivateMessage), panic);
        Assert.DoesNotContain("private-clipboard", panic);
    }

    [Fact]
    public void 诊断端口抛异常不改变调用流程()
    {
        var log = new DiagnosticLog(new ThrowingSink(), "1", 1);
        log.Vital("start");
        log.Verbose("clipboard-round");
        log.Panic("executor", new Exception());
    }

    private static void ThrowWithPrivateMessage() => throw new IOException("private-clipboard");

    private sealed class RecordingSink : IDiagnosticSink
    {
        public List<string> Diagnostic { get; } = [];
        public List<string> Panic { get; } = [];
        public void AppendDiagnostic(string entry) => Diagnostic.Add(entry);
        public void AppendPanic(string entry) => Panic.Add(entry);
    }

    private sealed class ThrowingSink : IDiagnosticSink
    {
        public void AppendDiagnostic(string entry) => throw new IOException();
        public void AppendPanic(string entry) => throw new IOException();
    }
}
