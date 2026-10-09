using ClipboardTool.Application;
using ClipboardTool.Domain.Geometry;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

public sealed class WindowLandingDiagnosticTests
{
    [Fact]
    public void 无效窗口落地失败也记录回读失败与两次尝试结果()
    {
        var sink = new Sink();
        var target = new ScreenMetrics(IntPtr.Zero,
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1);
        var landed = WindowPlacer.PlaceSummonDip(IntPtr.Zero, target, new DipPoint(100, 100), 380, 600,
            new DiagnosticLog(sink, null, 123));
        Assert.False(landed);
        var entry = Assert.Single(sink.Entries);
        Assert.Contains("summon-landed first=False final=False repair=True", entry);
        Assert.Contains("intent=100,100,380,600 readable=False actual=0,0,0,0 visible=False", entry);
    }

    private sealed class Sink : IDiagnosticSink
    {
        public List<string> Entries { get; } = [];
        public void AppendDiagnostic(string entry) => Entries.Add(entry);
        public void AppendPanic(string entry) => throw new InvalidOperationException();
    }
}
