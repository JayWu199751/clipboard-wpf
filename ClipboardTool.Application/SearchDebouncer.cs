namespace ClipboardTool.Application;

/// <summary>
/// 延迟调度端口：安排一次延迟回调，返回的取消器 Dispose 即撤销。
/// 实现方（Infrastructure/Presentation）须在约定线程上触发回调（协调器线程模型见其类型注释）。
/// </summary>
public interface IDelayScheduler
{
    IDisposable Delay(int milliseconds, Action callback);
}

/// <summary>
/// 搜索防抖（F22；legacy App.tsx SEARCH_DEBOUNCE_MS = 120）：
/// 每次查询变化取消未触发的旧定时器、安排 120ms 后提交；
/// 提交前校验代次——旧查询不得覆盖新结果（取消与执行竞态时以代次兜底）；提交值去首尾空白。
/// </summary>
public sealed class SearchDebouncer
{
    public const int DebounceMs = 120;

    private readonly IDelayScheduler _scheduler;
    private IDisposable? _pending;
    private long _generation;

    public SearchDebouncer(IDelayScheduler scheduler) => _scheduler = scheduler;

    /// <summary>防抖后真正生效的查询（已 trim；每次提交恰好一次）。</summary>
    public event Action<string>? QueryCommitted;

    public void QueryChanged(string query)
    {
        _pending?.Dispose();
        _pending = null;
        var generation = ++_generation;
        _pending = _scheduler.Delay(DebounceMs, () =>
        {
            // 代次校验：期间又有新的 QueryChanged（旧查询不得覆盖新结果）
            if (generation != Interlocked.Read(ref _generation))
            {
                return;
            }
            QueryCommitted?.Invoke(query.Trim());
        });
    }

    /// <summary>丢弃挂起中的提交（呼出重置等场景：清查询不触发一次迟到的过滤）。</summary>
    public void Reset()
    {
        _pending?.Dispose();
        _pending = null;
        _generation++;
    }
}
