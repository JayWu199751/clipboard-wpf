using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Application;

/// <summary>
/// 呼出键注册重试（F32；legacy main.rs hotkey-retry）：呼出键没注册上=这个会话里热键全哑，
/// 而开机那一刻可能撞上瞬时拒绝（键还被正在退场的程序占着、插件刚起来）。差量注册是幂等的：
/// 注册上了就一次插件调用都不发，所以这里按间隔再问两遍，把「一次没成 → 整会话哑」堵掉。
/// 每次都**现读**设置里的键，不用启动时那份快照：用户在这 5s / 20s 里换过键的话，
/// 拿旧键去重试会把新键注册回来时顺手注销掉，而托盘与设置都还显示着新键——
/// 那种「设置说 A、系统里是 B」正是最难查的一类。
/// </summary>
public sealed class SummonKeyRetry : IDisposable
{
    /// <summary>重试间隔（legacy Duration::from_secs(5) / from_secs(20)）。</summary>
    public const int FirstRetryMs = 5000;
    public const int SecondRetryMs = 20000;

    private readonly IDelayScheduler _scheduler;
    private readonly Func<HotkeyCombo> _readCurrent;
    private readonly Action<HotkeyCombo> _apply;
    private readonly List<IDisposable> _timers = [];

    public SummonKeyRetry(IDelayScheduler scheduler, Func<HotkeyCombo> readCurrent, Action<HotkeyCombo> apply)
    {
        _scheduler = scheduler;
        _readCurrent = readCurrent;
        _apply = apply;
    }

    /// <summary>安排两次重试。应用动作落在宿主编排线程上（Dispatcher 调度器保证归队 UI）。</summary>
    public void Start()
    {
        if (_timers.Count > 0)
        {
            return; // 重复 Start 无操作（单实例语义下不会发生，防御重入）
        }
        foreach (var delay in new[] { FirstRetryMs, SecondRetryMs })
        {
            _timers.Add(_scheduler.Delay(delay, RetryOnce));
        }
    }

    private void RetryOnce() => _apply(_readCurrent()); // 现读现用：拿当下设置的键做幂等差量

    public void Dispose()
    {
        foreach (var timer in _timers)
        {
            timer.Dispose();
        }
        _timers.Clear();
    }
}
