using System.Windows;
using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Presentation.Wpf;

// 基类全限定：Application 层命名空间会遮蔽 System.Windows.Application
public partial class App : System.Windows.Application
{
    private HotkeyExecutor? _hotkeys;
    private PanelWindow? _panel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _panel = new PanelWindow();
        _hotkeys = new HotkeyExecutor();

        // 键位计划按面板状态重算（legacy ADR-0006/0010：已生效键集合由执行注册者维护，按模式让位）：
        // 停靠态 = {呼出键}；呼出浏览态 = {呼出键, Esc 停靠（F18）}。执行者按差量增删。
        // 注意顺序：面板自身在 SourceInitialized 里先 Dock，DockStateChanged 会在 Attach 之前触发一次，
        // ApplyKeys 里以 Attached 挡掉；初始计划由 Attach 之后的显式调用落地。
        _panel.DockStateChanged += docked => ApplyKeys(!docked);
        _panel.SourceInitialized += (_, _) =>
        {
            _hotkeys.Attach(_panel);
            ApplyKeys(isSummoned: !_panel.IsDocked);
        };

        // 临时托盘（F29/F30 雏形）：左键单击抬起呼出，右键菜单；呼出键展示由计划推导（提示=行为）
        var tray = new TrayIconHost(
            onSummonClick: SummonFromTray,
            menuItems:
            [
                ($"显示面板 {HotkeyPlan.SummonDefault.DisplayName}", SummonFromTray),
                ("退出", Shutdown),
            ],
            tooltip: "ClipboardTool");

        _panel.Show();
        return;

        void SummonFromTray()
        {
            if (_panel is { IsDocked: true } panel)
            {
                panel.Summon();
            }
        }

        void ApplyKeys(bool isSummoned)
        {
            var plan = isSummoned
                ? new HotkeyPlan([HotkeyPlan.SummonDefault, HotkeyPlan.BrowseDock])
                : HotkeyPlan.Default;
            _hotkeys.ApplyPlan(plan.PlanDiff(_hotkeys.EffectiveKeys), OnKeyTriggered);
        }

        void OnKeyTriggered(HotkeyCombo combo)
        {
            if (combo == HotkeyPlan.SummonDefault)
            {
                _panel?.ToggleSummon();
            }
            else if (combo == HotkeyPlan.BrowseDock)
            {
                _panel?.Dock();
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _panel = null;
        _hotkeys?.Dispose();
        base.OnExit(e);
    }
}
