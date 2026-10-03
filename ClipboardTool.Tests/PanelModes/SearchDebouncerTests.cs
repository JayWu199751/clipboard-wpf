using ClipboardTool.Application;

namespace ClipboardTool.Tests.PanelModes;

/// <summary>
/// 搜索防抖（F22；legacy App.tsx SEARCH_DEBOUNCE_MS = 120）：
/// 查询变化取消旧定时器；旧查询不得覆盖新结果；提交值去首尾空白。
/// </summary>
public class SearchDebouncerTests
{
    [Fact]
    public void QueryChanged_SchedulesAfter120ms()
    {
        var scheduler = new FakeScheduler();
        var debouncer = new SearchDebouncer(scheduler);

        debouncer.QueryChanged("a");

        var (delayMs, _) = Assert.Single(scheduler.Scheduled);
        Assert.Equal(120, delayMs);
    }

    [Fact]
    public void RapidChanges_OnlyLatestCommits()
    {
        var scheduler = new FakeScheduler();
        var debouncer = new SearchDebouncer(scheduler);
        string? committed = null;
        debouncer.QueryCommitted += q => committed = q;

        debouncer.QueryChanged("a");
        debouncer.QueryChanged("ab");
        debouncer.QueryChanged("abc");
        scheduler.FireAll();

        Assert.Equal("abc", committed);
    }

    [Fact]
    public void CancelledCallback_DoesNotCommit()
    {
        // 旧查询不得覆盖新结果：即使回调在取消落地前已开始执行（竞态迟到），也不许提交旧值
        var scheduler = new FakeScheduler();
        var debouncer = new SearchDebouncer(scheduler);
        var committed = new List<string>();
        debouncer.QueryCommitted += committed.Add;

        debouncer.QueryChanged("a");
        debouncer.QueryChanged("ab");

        scheduler.ForceFire(0); // 模拟迟到的旧回调
        Assert.Empty(committed);

        scheduler.FireAll(); // 新回调正常提交
        Assert.Equal(["ab"], committed);
    }

    [Fact]
    public void CommitValue_Trimmed()
    {
        var scheduler = new FakeScheduler();
        var debouncer = new SearchDebouncer(scheduler);
        string? committed = null;
        debouncer.QueryCommitted += q => committed = q;

        debouncer.QueryChanged("  hello  ");
        scheduler.FireAll();

        Assert.Equal("hello", committed);
    }

    [Fact]
    public void EmptyQuery_AlsoDebounced()
    {
        // 清空查询同样走防抖（legacy 对每次 query 变化都设定时器）
        var scheduler = new FakeScheduler();
        var debouncer = new SearchDebouncer(scheduler);

        debouncer.QueryChanged("");

        Assert.Single(scheduler.Scheduled);
    }
}
