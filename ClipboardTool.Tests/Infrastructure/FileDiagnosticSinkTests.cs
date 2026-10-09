using System.Text;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

public sealed class FileDiagnosticSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clipdiag-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "diag.log");

    [Fact]
    public void 正好512KB不轮转_超过后下条写入轮转且仅保留一代()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(LogPath, new byte[512 * 1024]);
        var sink = new FileDiagnosticSink(_dir);
        sink.AppendDiagnostic("first\n");
        Assert.False(File.Exists(LogPath + ".1"));
        sink.AppendDiagnostic("second\n");
        Assert.Equal("second\n", File.ReadAllText(LogPath));
        Assert.Equal(512 * 1024 + 6, new FileInfo(LogPath + ".1").Length);
        File.WriteAllText(LogPath, new string('x', 512 * 1024 + 1));
        sink.AppendDiagnostic("third\n");
        Assert.Equal("third\n", File.ReadAllText(LogPath));
        Assert.Equal(new string('x', 512 * 1024 + 1), File.ReadAllText(LogPath + ".1"));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Unicode使用UTF8无BOM并且每次写完释放文件句柄()
    {
        var sink = new FileDiagnosticSink(_dir);
        sink.AppendDiagnostic("关键读数\n");
        Assert.Equal(Encoding.UTF8.GetBytes("关键读数\n"), File.ReadAllBytes(LogPath));
        using var exclusive = File.Open(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void 多写入器并发追加不截断行也不遗失读数()
    {
        Parallel.For(0, 2, writer =>
        {
            var sink = new FileDiagnosticSink(_dir);
            for (var i = 0; i < 40; i++)
            {
                sink.AppendDiagnostic($"writer={writer} item={i}\n");
            }
        });
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(80, lines.Length);
        Assert.Equal(80, lines.Distinct().Count());
    }

    [Fact]
    public void 路径不可写或轮转被占用均不抛异常_释放后仍能继续记录()
    {
        Directory.CreateDirectory(_dir);
        var sink = new FileDiagnosticSink(_dir);
        File.WriteAllBytes(LogPath, new byte[512 * 1024 + 1]);
        using (File.Open(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            sink.AppendDiagnostic("blocked\n");
        }
        sink.AppendDiagnostic("recovered\n");
        Assert.Equal("recovered\n", File.ReadAllText(LogPath));
        var invalid = new FileDiagnosticSink(LogPath); // 文件无法作为目录。
        invalid.AppendDiagnostic("ignored");
        invalid.AppendPanic("ignored");
        sink.AppendPanic("trace\n");
        sink.AppendPanic("next\n");
        Assert.Equal("trace\nnext\n", File.ReadAllText(Path.Combine(_dir, "panic.log")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
