using System.Collections.Concurrent;
using ClipboardTool.Application;

namespace ClipboardTool.Tests.Application;

public sealed class ModeExecutorDiagnosticTests
{
    [Fact]
    public void 意图异常有关键读数和堆栈_后续意图仍执行()
    {
        var sink = new Sink();
        using var nextRan = new ManualResetEventSlim();
        using var executor = new ModeExecutor(new DiagnosticLog(sink, null, 123));
        executor.Post(() => throw new InvalidOperationException("private-text"));
        executor.Post(nextRan.Set);
        Assert.True(nextRan.Wait(TimeSpan.FromSeconds(3)));
        Assert.Contains(sink.Diagnostic, line => line.Contains("executor-failed"));
        Assert.Contains("source=mode-executor", Assert.Single(sink.Panic));
        Assert.DoesNotContain("private-text", Assert.Single(sink.Panic));
    }

    [Fact]
    public void 执行者已退出时投递失败有读数且保留原异常契约()
    {
        var sink = new Sink();
        var executor = new ModeExecutor(new DiagnosticLog(sink, null, 123));
        executor.Dispose();
        Assert.Throws<InvalidOperationException>(() => executor.Post(() => { }));
        Assert.Contains(sink.Diagnostic, line => line.Contains("executor-dead intent=rejected"));
    }

    private sealed class Sink : IDiagnosticSink
    {
        public ConcurrentQueue<string> Diagnostic { get; } = new();
        public ConcurrentQueue<string> Panic { get; } = new();
        public void AppendDiagnostic(string entry) => Diagnostic.Enqueue(entry);
        public void AppendPanic(string entry) => Panic.Enqueue(entry);
    }
}
