using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Tests.Hotkeys;

/// <summary>HotkeyPlan：默认呼出键 Ctrl+Shift+V；目标键集合对已生效键集合的差量推导。</summary>
public class HotkeyPlanTests
{
    [Fact]
    public void DefaultSummon_IsCtrlShiftV()
    {
        var combo = HotkeyPlan.SummonDefault;

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(HotkeyPlan.VirtualKeyV, combo.VirtualKey);
    }

    [Fact]
    public void DefaultPlan_TargetsSummonOnly()
    {
        var plan = HotkeyPlan.Default;

        var combo = Assert.Single(plan.Targets);
        Assert.Equal(HotkeyPlan.SummonDefault, combo);
    }

    [Fact]
    public void PlanDiff_FromEmptyEffective_RegistersAllTargets()
    {
        var plan = HotkeyPlan.Default;

        var diff = plan.PlanDiff(effectiveKeys: []);

        Assert.Empty(diff.ToUnregister);
        Assert.Equal(plan.Targets, diff.ToRegister);
    }

    [Fact]
    public void PlanDiff_WhenEffectiveMatchesTargets_IsEmpty()
    {
        var plan = HotkeyPlan.Default;

        var diff = plan.PlanDiff(effectiveKeys: [HotkeyPlan.SummonDefault]);

        Assert.Empty(diff.ToRegister);
        Assert.Empty(diff.ToUnregister);
    }

    [Fact]
    public void PlanDiff_RegistersMissing_AndUnregistersStale()
    {
        var plan = HotkeyPlan.Default;
        var stale = new HotkeyCombo(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey: 0x58); // Ctrl+Shift+X

        var diff = plan.PlanDiff(effectiveKeys: [stale]);

        Assert.Equal([HotkeyPlan.SummonDefault], diff.ToRegister);
        Assert.Equal([stale], diff.ToUnregister);
    }

    [Fact]
    public void BrowseDock_IsBareEscape()
    {
        // F18：浏览态 Esc 停靠；无修饰键的裸 Esc 只在面板呼出期间注册（按模式让位）
        Assert.Equal(HotkeyModifiers.None, HotkeyPlan.BrowseDock.Modifiers);
        Assert.Equal(0x1Bu, HotkeyPlan.BrowseDock.VirtualKey);
    }

    [Fact]
    public void PlanDiff_BrowseTargetsVsDockedEffective_RegistersEscapeOnly()
    {
        var browsePlan = new HotkeyPlan([HotkeyPlan.SummonDefault, HotkeyPlan.BrowseDock]);

        var diff = browsePlan.PlanDiff(effectiveKeys: [HotkeyPlan.SummonDefault]);

        Assert.Equal([HotkeyPlan.BrowseDock], diff.ToRegister);
        Assert.Empty(diff.ToUnregister);
    }

    [Fact]
    public void DisplayName_MatchesRegistryStyle()
    {
        Assert.Equal("Ctrl+Shift+V", HotkeyPlan.SummonDefault.DisplayName);
        Assert.Equal("Esc", HotkeyPlan.BrowseDock.DisplayName);
        Assert.Equal("Ctrl+1", new HotkeyCombo(HotkeyModifiers.Control, 0x31).DisplayName);
        Assert.Equal("F1", new HotkeyCombo(HotkeyModifiers.None, 0x70).DisplayName);
        Assert.Equal("Alt+F4", new HotkeyCombo(HotkeyModifiers.Alt, 0x73).DisplayName);
    }
}
