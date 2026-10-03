using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 单实例的 mutex/pipe 逻辑（F34；合成客户端走真实命名管道，不需窗口/托盘/热键）：
/// mutex 判「谁是首个实例」；第二实例经 named pipe 向原进程投递呼出请求；
/// 客户端对未就绪的服务端有限等待（超时放弃）；服务端只接受合法呼出动作的消息。
/// 普通↔提权完整性 ACL 的真机实测不在无头范围（真机步骤，工单真机判据④）。
/// </summary>
public class SingleInstanceTests : IDisposable
{
    private readonly List<string> _names = [];

    private string UniqueName(string kind)
    {
        var name = $"cliptest-{kind}-{Guid.NewGuid():N}";
        _names.Add(name);
        return name;
    }

    public void Dispose()
    {
        // mutex 由 using 释放；这里只兜底记录（无动作）
    }

    // —— mutex：谁是首个实例 ——
    [Fact]
    public void 首个实例获得所有权_第二个实例拒绝()
    {
        var name = UniqueName("mutex");
        using var first = new SingleInstanceGate(name);
        using var second = new SingleInstanceGate(name);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire(), "同名 mutex 已被首个实例持有，第二实例必须让位");
    }

    [Fact]
    public void 未获所有权时重试仍拒绝_释放后新实例可获得()
    {
        var name = UniqueName("mutex2");
        using (var first = new SingleInstanceGate(name))
        {
            first.TryAcquire();
            using var second = new SingleInstanceGate(name);
            Assert.False(second.TryAcquire());
            Assert.False(second.TryAcquire(), "重复请求不改变所有权判定");
        } // first 释放

        using var third = new SingleInstanceGate(name);
        Assert.True(third.TryAcquire(), "首个实例退出后（abandoned）新进程应可获得所有权");
    }

    // —— pipe：投递与校验 ——
    [Fact]
    public async Task 合法呼出请求送达回调()
    {
        var name = UniqueName("pipe");
        using var server = new SummonServer(name);
        server.Start();
        var summoned = new TaskCompletionSource();
        server.SummonReceived += () => summoned.TrySetResult();

        var delivered = await SummonClient.SummonAsync(name, timeoutMs: 5000);

        Assert.True(delivered);
        var finished = await Task.WhenAny(summoned.Task, Task.Delay(3000));
        Assert.Equal(summoned.Task, finished);
    }

    [Fact]
    public async Task 服务端未就绪时客户端有限等待_就绪即送达()
    {
        var name = UniqueName("pipe-wait");
        var summoned = new TaskCompletionSource();
        // 服务端延迟 300ms 才起来：客户端必须等它，而不是立即报失败
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            using var server = new SummonServer(name);
            server.SummonReceived += () => summoned.TrySetResult();
            server.Start();
            // Start 后 keep alive 到 Dispose 由 using 保证（闭包生命周期到测试结束）
            await summoned.Task;
        });

        var delivered = await SummonClient.SummonAsync(name, timeoutMs: 5000);

        Assert.True(delivered, "服务端短暂未就绪应在有限等待内等到，而不是放弃");
    }

    [Fact]
    public async Task 服务端始终未就绪_超时放弃()
    {
        var name = UniqueName("pipe-timeout"); // 没有任何服务端会监听这个名字

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var delivered = await SummonClient.SummonAsync(name, timeoutMs: 250);
        sw.Stop();

        Assert.False(delivered);
        Assert.True(sw.ElapsedMilliseconds >= 200, "超时前必须真的等过（有限等待语义）");
    }

    [Fact]
    public void 非法请求拒绝()
    {
        // 协议判定：只有标准呼出串合法；其他一律拒绝（改动其他进程行为的口子不开）
        Assert.True(SummonServer.IsValidRequest(SummonProtocol.SummonRequest));
        foreach (var bad in new[]
                 {
                     "", "summon", "SUMMON", SummonProtocol.SummonRequest + " extra",
                     "exit", "clear", "CLIPBOARDTOOL summon v1",
                 })
        {
            Assert.False(SummonServer.IsValidRequest(bad), $"'{bad}' 不该被当成合法呼出");
        }
    }
}
