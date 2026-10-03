using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.PendingDeletion;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// 延迟删除服务（legacy App.tsx dispatchDeletion 效果执行侧）：立即摘除可见列表、6 秒撤销窗口、
/// 到期才真删、撤销取消计时、失败恢复可见并报错、强退保留条目。计时经 IDelayScheduler 注入可测。
/// </summary>
public class PendingDeletionServiceTests
{
    private readonly FakeScheduler _scheduler = new();
    private readonly List<string> _hiddenNotifications = [];
    private readonly List<PendingDeletionToast> _toasts = [];

    private HistoryService CreateHistory(params string[] texts)
    {
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory,
            new FakeImageStore(),
            () => Guid.NewGuid().ToString(),
            () => 1_000);
        foreach (var text in texts)
        {
            store.RecordText(text);
        }
        return new HistoryService(store);
    }

    private PendingDeletionService CreateService(HistoryService history)
    {
        var service = new PendingDeletionService(_scheduler, history);
        service.HiddenChanged += () => _hiddenNotifications.Add(string.Join(',', service.HiddenIds));
        service.ToastRequested += toast => _toasts.Add(toast);
        return service;
    }

    [Fact]
    public void Request_立即摘除_条目未删_安排6秒到期_给出撤销提示()
    {
        var history = CreateHistory("第一段内容");
        var entry = history.Entries[0];
        var service = CreateService(history);

        service.Request(entry.Id);

        Assert.Contains(entry.Id, service.HiddenIds);
        Assert.NotNull(history.Find(entry.Id)); // 到期才真删
        var timer = Assert.Single(_scheduler.Timers);
        Assert.Equal(PendingDeletionService.UndoWindowMs, timer.DelayMs);
        var toast = Assert.Single(_toasts);
        Assert.Equal("已删除", toast.Message);
        Assert.True(toast.IsError);
        Assert.Equal("撤销", toast.ActionLabel);
        Assert.Equal("第一段内容", toast.Dim);
        Assert.NotEmpty(_hiddenNotifications);
    }

    [Fact]
    public void Request_文字dim_压缩空白并截取18字符()
    {
        var text = string.Concat(Enumerable.Repeat("词 ", 30)); // 60+ 字符含空白
        var history = CreateHistory(text);
        var service = CreateService(history);

        service.Request(history.Entries[0].Id);

        var dim = Assert.Single(_toasts).Dim!;
        Assert.Equal(18, dim.Length);
        Assert.DoesNotContain('\n', dim);
    }

    [Fact]
    public void Request_同条重复_不重启计时()
    {
        var history = CreateHistory("内容");
        var service = CreateService(history);
        service.Request(history.Entries[0].Id);
        var timersAfterFirst = _scheduler.Timers.Count;

        service.Request(history.Entries[0].Id);

        Assert.Equal(timersAfterFirst, _scheduler.Timers.Count); // 未新增计时
        Assert.Single(_toasts); // 未重复弹撤销提示
    }

    [Fact]
    public void Undo_取消计时_恢复显示_条目保留()
    {
        var history = CreateHistory("内容");
        var entry = history.Entries[0];
        var service = CreateService(history);
        service.Request(entry.Id);

        service.Undo(entry.Id);

        Assert.False(_scheduler.Timers.Single().Running);
        Assert.DoesNotContain(entry.Id, service.HiddenIds);
        Assert.NotNull(history.Find(entry.Id));
    }

    [Fact]
    public void 到期触发_条目真删_隐藏集合清空()
    {
        var history = CreateHistory("内容");
        var entry = history.Entries[0];
        var service = CreateService(history);
        service.Request(entry.Id);

        _scheduler.Timers.Single().Fire();

        Assert.Null(history.Find(entry.Id));
        Assert.DoesNotContain(entry.Id, service.HiddenIds);
    }

    [Fact]
    public void 到期触发_删除失败_恢复可见并报错()
    {
        var history = CreateHistory("内容");
        var entry = history.Entries[0];
        var service = CreateService(history);
        service.Request(entry.Id);
        history.Remove(entry.Id); // 到期前条目已被别处删除 → Remove 返回 false

        _toasts.Clear();
        _scheduler.Timers.Single().Fire();

        Assert.DoesNotContain(entry.Id, service.HiddenIds); // 恢复可见（Restore：从隐藏集合摘除遮罩）
        var error = Assert.Single(_toasts);
        Assert.Equal("删除失败，请重试", error.Message);
        Assert.True(error.IsError);
        Assert.Null(error.ActionLabel);
    }

    [Fact]
    public void 服务释放_清全部计时_条目全部保留()
    {
        var history = CreateHistory("a", "b");
        var service = CreateService(history);
        service.Request(history.Entries[0].Id);
        service.Request(history.Entries[1].Id);

        service.Dispose();

        Assert.All(_scheduler.Timers, t => Assert.False(t.Running));
        Assert.Equal(2, history.Entries.Count); // 未到期强退：条目保留（6 秒撤销语义）
        Assert.Empty(service.HiddenIds);
    }

    [Fact]
    public void 多条删除_各自独立计时_逐条到期逐条删()
    {
        var history = CreateHistory("a", "b");
        var service = CreateService(history);
        var first = history.Entries[0].Id;
        var second = history.Entries[1].Id;
        service.Request(first);
        service.Request(second);

        _scheduler.Timers[0].Fire(); // 先请求的先到期

        Assert.Null(history.Find(first));
        Assert.Contains(second, service.HiddenIds); // 另一条不受影响

        _scheduler.Timers[1].Fire();
        Assert.Null(history.Find(second));
    }

    // —— 清空历史协调（F33；托盘清空入口在删除窗口内也要稳） ——
    [Fact]
    public void ClearAll_取消全部计时并清空隐藏集合_通知渲染层()
    {
        var history = CreateHistory("a", "b");
        var service = CreateService(history);
        service.Request(history.Entries[0].Id);
        service.Request(history.Entries[1].Id);
        var notificationsBefore = _hiddenNotifications.Count;

        service.ClearAll();

        Assert.Empty(service.HiddenIds);
        Assert.Equal(2, _scheduler.Timers.Count(t => !t.Running)); // 两条到期计时都必须取消
        Assert.True(_hiddenNotifications.Count > notificationsBefore, "隐藏集合清空要通知渲染层重载");
    }

    [Fact]
    public void ClearAll_之后到期不真删不误报()
    {
        // 清空历史把条目一并删了：若删除流程还挂着计时，到期会找不到条目而误报「删除失败」
        var history = CreateHistory("a", "b");
        var service = CreateService(history);
        service.Request(history.Entries[0].Id);
        service.Request(history.Entries[1].Id);
        _toasts.Clear();

        service.ClearAll();
        history.Clear(); // 托盘清空历史的落库动作
        foreach (var timer in _scheduler.Timers)
        {
            timer.Fire(); // 已取消的计时器即使被触发（防御）也不得有副作用
        }

        Assert.Empty(_toasts); // 清空后的到期路径不得再报删除失败
        Assert.Empty(service.HiddenIds);
    }

    /// <summary>假调度器：记录 Delay(ms, callback) 与取消；测试手动 Fire 触发到期。</summary>
    private sealed class FakeScheduler : IDelayScheduler
    {
        public List<TimerRecord> Timers { get; } = [];

        public IDisposable Delay(int milliseconds, Action callback)
        {
            var record = new TimerRecord(milliseconds, callback);
            Timers.Add(record);
            return record;
        }

        public sealed class TimerRecord(int delayMs, Action callback) : IDisposable
        {
            public int DelayMs { get; } = delayMs;
            public bool Running { get; private set; } = true;

            public void Fire()
            {
                if (!Running)
                {
                    return; // 已取消/已触发的计时器：Fire 无副作用（真实调度器同语义）
                }
                Running = false;
                callback();
            }

            public void Dispose() => Running = false;
        }
    }

    /// <summary>内存图片端口（文字测试不触图）。</summary>
    private sealed class FakeImageStore : IImageFileStore
    {
        public string? SavePng(byte[] png, string id) => null;
        public string HashFile(string path) => string.Empty;
        public string HashPng(byte[] png) => string.Empty;
        public void RemoveFile(string path) { }
        public bool FileExists(string path) => false;
    }
}
