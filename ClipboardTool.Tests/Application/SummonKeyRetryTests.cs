using ClipboardTool.Application;
using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// 呼出键注册重试（F32；legacy main.rs hotkey-retry）：呼出键没注册上=这个会话里热键全哑，
/// 而开机那一刻可能撞上瞬时拒绝（键还被正在退场的程序占着）。按 5 秒 / 20 秒间隔再问两遍；
/// 每次都现读设置（用户在间隔里换过键的话，拿旧键去重试会把新键注销掉）；重试动作本身是
/// 差量幂等的（键已生效则一次系统调用都不发）。
/// </summary>
public class SummonKeyRetryTests
{
    private sealed class FakeScheduler : IDelayScheduler
    {
        public List<(int DelayMs, Action Fire)> Timers { get; } = [];
        public IDisposable Delay(int milliseconds, Action callback)
        {
            Timers.Add((milliseconds, callback));
            return new NopTimer();
        }

        private sealed class NopTimer : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void 安排两次重试_间隔五秒与二十秒()
    {
        var scheduler = new FakeScheduler();
        using var retry = new SummonKeyRetry(scheduler, () => HotkeyPlan.SummonDefault, _ => { });

        retry.Start();

        Assert.Equal([5000, 20000], scheduler.Timers.Select(t => t.DelayMs).ToList());
    }

    [Fact]
    public void 重试时现读设置_不是启动时快照()
    {
        // 用户在 5 秒内换过键：重试必须拿新键去问，否则会把新键注销（「设置说 A、系统里是 B」）
        var scheduler = new FakeScheduler();
        var current = HotkeyPlan.SummonDefault;
        var applied = new List<HotkeyCombo>();
        using var retry = new SummonKeyRetry(scheduler, () => current, combo => applied.Add(combo));
        retry.Start();

        current = new HotkeyCombo(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58); // Ctrl+Alt+X
        scheduler.Timers[0].Fire();
        scheduler.Timers[1].Fire();

        Assert.Equal([current, current], applied);
        Assert.DoesNotContain(HotkeyPlan.SummonDefault, applied);
    }

    [Fact]
    public void 到点应用的是当下读数_两次重试各自读()
    {
        var scheduler = new FakeScheduler();
        var current = HotkeyPlan.SummonDefault;
        var applied = new List<HotkeyCombo>();
        using var retry = new SummonKeyRetry(scheduler, () => current, combo => applied.Add(combo));
        retry.Start();

        scheduler.Timers[0].Fire(); // 5 秒：仍是默认键
        current = new HotkeyCombo(HotkeyModifiers.Win, 0x20); // 期间又换了一次
        scheduler.Timers[1].Fire(); // 20 秒：拿第二次的键

        Assert.Equal([HotkeyPlan.SummonDefault, current], applied);
    }

    private sealed class NopTimer
    {
    }
}
