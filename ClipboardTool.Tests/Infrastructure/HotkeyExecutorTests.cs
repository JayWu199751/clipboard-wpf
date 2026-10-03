using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// HotkeyExecutor：RegisterHotKey 的执行注册者。「注册失败不上账」（ADR-0006/0010）——
/// 已生效键集合只记真实注册成功的键；差量只动需要动的键。用假原生端口脱离真窗口测试。
/// </summary>
public class HotkeyExecutorTests
{
    private static readonly HotkeyCombo Toggle = HotkeyPlan.SummonDefault;
    private static readonly HotkeyCombo Esc = HotkeyPlan.BrowseDock;

    /// <summary>假原生注册端口：可编程成败与调用记录。</summary>
    private sealed class FakeNative : IHotkeyNative
    {
        public HashSet<HotkeyCombo> Occupied = [];
        public List<HotkeyCombo> RegisterCalls = [];
        public List<HotkeyCombo> UnregisterCalls = [];

        public bool Register(IntPtr hwnd, int id, HotkeyCombo combo)
        {
            RegisterCalls.Add(combo);
            return !Occupied.Contains(combo);
        }

        public void Unregister(IntPtr hwnd, int id, HotkeyCombo combo) => UnregisterCalls.Add(combo);
    }

    [Fact]
    public void RegisterSuccess_EntersEffectiveKeys()
    {
        var native = new FakeNative();
        using var executor = new HotkeyExecutor(native);

        executor.ApplyPlan(new HotkeyDiff([Toggle], []), _ => { });

        Assert.Equal([Toggle], executor.EffectiveKeys);
    }

    [Fact]
    public void RegisterRejected_NotRecorded()
    {
        // 注册失败不上账：被占用的键不进已生效集合，下一次差量自然重试
        var native = new FakeNative { Occupied = [Toggle] };
        using var executor = new HotkeyExecutor(native);

        executor.ApplyPlan(new HotkeyDiff([Toggle], []), _ => { });

        Assert.Empty(executor.EffectiveKeys);
    }

    [Fact]
    public void NativeHotkeyMethods_WithoutWindow_Refuses()
    {
        // 未挂接窗口（hwnd=Zero）时真实端口拒绝注册（WM_HOTKEY 无处投递），不发起系统调用
        Assert.False(NativeHotkeyMethods.Instance.Register(IntPtr.Zero, 1, Toggle));
        NativeHotkeyMethods.Instance.Unregister(IntPtr.Zero, 1, Toggle); // 不抛异常即可
    }

    [Fact]
    public void ReapplySamePlan_DoesNotReRegister()
    {
        // 差量：调用方按已生效集合推导 diff（协调器流），已生效的键不重复注册
        var native = new FakeNative();
        using var executor = new HotkeyExecutor(native);
        var plan = new HotkeyPlan([Toggle]);

        executor.ApplyPlan(plan.PlanDiff(executor.EffectiveKeys), _ => { });
        executor.ApplyPlan(plan.PlanDiff(executor.EffectiveKeys), _ => { });

        Assert.Single(native.RegisterCalls);
    }

    [Fact]
    public void Unregister_RemovesFromTable_AndCallsNative()
    {
        var native = new FakeNative();
        using var executor = new HotkeyExecutor(native);
        executor.ApplyPlan(new HotkeyDiff([Toggle, Esc], []), _ => { });

        executor.ApplyPlan(new HotkeyDiff([], [Esc]), _ => { });

        Assert.Equal([Toggle], executor.EffectiveKeys);
        Assert.Equal([Esc], native.UnregisterCalls);
    }

    [Fact]
    public void UnregisterUnknownKey_DoesNotCallNative()
    {
        var native = new FakeNative();
        using var executor = new HotkeyExecutor(native);

        executor.ApplyPlan(new HotkeyDiff([], [Toggle]), _ => { });

        Assert.Empty(native.UnregisterCalls);
    }

    [Fact]
    public void ReregisterAfterUnregister_Succeeds()
    {
        // 注销过的键要能再注册上：模式切换差量靠这一点复用键位
        var native = new FakeNative();
        using var executor = new HotkeyExecutor(native);
        executor.ApplyPlan(new HotkeyDiff([Esc], []), _ => { });
        executor.ApplyPlan(new HotkeyDiff([], [Esc]), _ => { });

        executor.ApplyPlan(new HotkeyDiff([Esc], []), _ => { });

        Assert.Equal([Esc], executor.EffectiveKeys);
        Assert.Equal(2, native.RegisterCalls.Count);
    }

    [Fact]
    public void EffectiveKeys_ReflectsFailedReapply()
    {
        // 第一次注册成功；键被占用后重试失败 → 仍只有一条（失败不上账）
        var native = new FakeNative();
        using var executor = new HotkeyExecutor(native);
        executor.ApplyPlan(new HotkeyDiff([Esc], []), _ => { });

        executor.ApplyPlan(new HotkeyDiff([], [Esc]), _ => { });
        native.Occupied.Add(Esc); // 系统侧被别人抢注
        executor.ApplyPlan(new HotkeyDiff([Esc], []), _ => { });

        Assert.Empty(executor.EffectiveKeys);
        Assert.Equal(2, native.RegisterCalls.Count);
    }
}
