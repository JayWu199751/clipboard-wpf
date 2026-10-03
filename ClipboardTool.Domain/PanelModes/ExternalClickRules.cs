namespace ClipboardTool.Domain.PanelModes;

/// <summary>
/// 「单击面板外部 → 停靠」与点击时间窗判定（F17）。legacy panel_modes::hides_on_click 与
/// modes::hide_if_clicked_outside 的纯规则收口：鼠标钩子（Infrastructure）只上报坐标与按下
/// 时刻，本规则按「面板可见 + 点击晚于呼出 + 明确落在面板矩形外」三步给出应否停靠。
/// 圆角外透明区不参与本判定：该区点击由分层窗口 alpha 穿透到下层（P5/ADR-0006），
/// 与 legacy hit_test 的矩形口径一致。
/// </summary>
public static class ExternalClickRules
{
    /// <summary>
    /// 判定：面板正开着，这一下点击算不算「点了面板外」（算则收起）。
    /// 只认严格晚于最近一次呼出的点击。全局鼠标钩子那条链是异步的（钩子线程 → 转发 →
    /// UI 线程），而托盘那一下的呼出走另一条链：同一次物理点击的「按下」和「抬起」谁先
    /// 到达处理线程完全看调度——抬起先把面板呼出、按下随后被当成点了面板外，肉眼看就是
    /// 「点托盘没反应」。靠到达顺序判没有出路，靠点击发生的时刻判才有确定性：
    /// 时刻必须在钩子里就取好（详见 16 号票）。
    /// </summary>
    public static bool HidesOnClick(long? shownAtMs, long clickedAtMs) =>
        shownAtMs is { } shown && clickedAtMs > shown;

    /// <summary>
    /// 外部点击 → 应否停靠（legacy hide_if_clicked_outside 三步合一）：
    /// 1) 面板可见；2) 点击晚于本次呼出（时间窗防护）；3) 明确落在面板矩形之外。
    /// clickInsidePanel=null 表示窗口缺失或几何读不到——判不出来不动作
    /// （沿用 legacy 行为：免得面板莫名收起）。
    /// </summary>
    public static bool ShouldDockOnOutsideClick(
        bool panelVisible, long? shownAtMs, long clickedAtMs, bool? clickInsidePanel) =>
        panelVisible && HidesOnClick(shownAtMs, clickedAtMs) && clickInsidePanel == false;

    /// <summary>物理像素点是否在窗口物理矩形内：左/上闭、右/下开（legacy contains_point 口径）。</summary>
    public static bool ContainsPoint(long left, long top, long right, long bottom, long x, long y) =>
        x >= left && x < right && y >= top && y < bottom;
}
