using ClipboardTool.Domain.Hotkeys;

namespace ClipboardTool.Presentation.Wpf;

/// <summary>面板视图模型骨架：占位条目驱动虚拟化列表；真实剪贴历史流归 T02。</summary>
public sealed class PanelViewModel
{
    public IReadOnlyList<PlaceholderCard> Items { get; } =
        Enumerable.Range(1, 200).Select(index => new PlaceholderCard(index)).ToList();

    public string CountText => $"{Items.Count} 条";

    /// <summary>浏览态停靠键展示（F18），由键位计划推导（「提示 = 行为」硬约束，不写死键名）。</summary>
    public string DockKeyText => HotkeyPlan.BrowseDock.DisplayName;

    /// <summary>搜索键 chip 归 T04 键位注册表；为空时搜索井不显示 chip。</summary>
    public string SearchKeyText => string.Empty;
}

/// <summary>占位条目：无真实数据时撑起卡片与滚动。</summary>
public sealed record PlaceholderCard(int Index)
{
    public string Body => $"占位条目 {Index} —— 正文骨架，行高约 20.15，最多三行（T02 接入真实历史）";

    public string Meta => $"来源 · 2026-10-03 12:00 · 图钉 · 备注 {Index}";
}
