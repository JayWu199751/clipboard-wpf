using ClipboardTool.Domain.PanelModes;

namespace ClipboardTool.Tests.PanelModes;

/// <summary>
/// F17 外部点击停靠判定（legacy panel_modes::hides_on_click + modes::hide_if_clicked_outside
/// 的移植对齐）：面板可见 + 点击晚于呼出 + 明确落在面板外，三者齐备才停靠；
/// 时间窗语义（早于呼出/从未呼出）与「判不出来不动作」逐条钉住。
/// </summary>
public class ExternalClickRulesTests
{
    // —— 时间窗（legacy hides_on_click） ——

    [Fact]
    public void 点击早于呼出不收起_晚于呼出才收起()
    {
        // 同一单调钟：按下在前、呼出在后（托盘那一下抬起先呼出、按下后到达的真实顺序）
        var clicked = 1000L;
        var shown = 1020L;
        Assert.False(ExternalClickRules.HidesOnClick(shown, clicked),
            "抬起先呼出、按下后到达：这一下点击就是把面板开出来的，不能反过来收起它");
        Assert.True(ExternalClickRules.HidesOnClick(shown, 1021));
        // 恰等于呼出时刻不算晚于（legacy clicked_at > shown 严格比较）
        Assert.False(ExternalClickRules.HidesOnClick(shown, shown));
    }

    [Fact]
    public void 从未呼出没有时刻可比_不收起()
    {
        Assert.False(ExternalClickRules.HidesOnClick(null, 1000));
    }

    [Fact]
    public void 时间窗随最近一次呼出滚动()
    {
        // 第二次呼出覆盖第一次：旧呼出之后的点击也早于新呼出
        Assert.False(ExternalClickRules.HidesOnClick(5000, 3000));
        Assert.True(ExternalClickRules.HidesOnClick(5000, 5001));
    }

    // —— 外部停靠三步判定（legacy hide_if_clicked_outside） ——

    [Fact]
    public void 面板可见_点击晚于呼出_明确在面板外_才停靠()
    {
        Assert.True(ExternalClickRules.ShouldDockOnOutsideClick(
            panelVisible: true, shownAtMs: 1000, clickedAtMs: 1001, clickInsidePanel: false));
    }

    [Fact]
    public void 面板不可见不动作()
    {
        Assert.False(ExternalClickRules.ShouldDockOnOutsideClick(
            panelVisible: false, shownAtMs: 1000, clickedAtMs: 1001, clickInsidePanel: false));
    }

    [Fact]
    public void 点击早于呼出不动作_时间窗防护()
    {
        Assert.False(ExternalClickRules.ShouldDockOnOutsideClick(
            panelVisible: true, shownAtMs: 1000, clickedAtMs: 999, clickInsidePanel: false));
    }

    [Fact]
    public void 从未呼出不动作()
    {
        Assert.False(ExternalClickRules.ShouldDockOnOutsideClick(
            panelVisible: true, shownAtMs: null, clickedAtMs: 1001, clickInsidePanel: false));
    }

    [Fact]
    public void 点击在面板内不动作()
    {
        Assert.False(ExternalClickRules.ShouldDockOnOutsideClick(
            panelVisible: true, shownAtMs: 1000, clickedAtMs: 1001, clickInsidePanel: true));
    }

    [Fact]
    public void 几何读不到不动作_判不出来免得面板莫名收起()
    {
        Assert.False(ExternalClickRules.ShouldDockOnOutsideClick(
            panelVisible: true, shownAtMs: 1000, clickedAtMs: 1001, clickInsidePanel: null));
    }

    // —— 坐标 vs 窗口物理矩形（legacy hit_test 矩形口径；圆角不参与隐藏决策） ——

    [Fact]
    public void 矩形包含判定_左闭右开边界()
    {
        // 左/上闭、右/下开（legacy contains_point 同口径：x >= 左 且 x < 右）
        Assert.True(ExternalClickRules.ContainsPoint(100, 200, 500, 800, 100, 200));
        Assert.True(ExternalClickRules.ContainsPoint(100, 200, 500, 800, 499, 799));
        Assert.False(ExternalClickRules.ContainsPoint(100, 200, 500, 800, 500, 400));
        Assert.False(ExternalClickRules.ContainsPoint(100, 200, 500, 800, 400, 800));
        Assert.False(ExternalClickRules.ContainsPoint(100, 200, 500, 800, 99, 400));
        Assert.False(ExternalClickRules.ContainsPoint(100, 200, 500, 800, 400, 199));
    }

    [Fact]
    public void 矩形包含判定_负坐标成立()
    {
        // 副屏/停靠位可能落在负坐标区（P5 移窗负坐标同款）
        Assert.True(ExternalClickRules.ContainsPoint(-300, -200, -100, 0, -200, -100));
        Assert.False(ExternalClickRules.ContainsPoint(-300, -200, -100, 0, -99, -100));
    }
}
