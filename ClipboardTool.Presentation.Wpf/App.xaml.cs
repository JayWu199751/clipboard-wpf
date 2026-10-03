using System.IO;
using System.Windows;
using System.Windows.Interop;
using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Domain.PasteChain;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Presentation.Wpf;

// 基类全限定：Application 层命名空间会遮蔽 System.Windows.Application
public partial class App : System.Windows.Application
{
    private HotkeyExecutor? _hotkeys;
    private PanelWindow? _panel;
    private HistoryService? _history;
    private ClipboardWatchService? _watch;
    private PasteService? _paste;
    private ModeExecutor? _executor;
    private ClipboardMessageSource? _clipboardSource;

    /// <summary>本次呼出捕获的焦点快照（呼出处理线程上捕获，F13/F14；停靠后消费/失效）。</summary>
    private FocusTarget? _capturedFocus;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _panel = new PanelWindow();
        _hotkeys = new HotkeyExecutor();
        _executor = new ModeExecutor();

        // 存档目录 %APPDATA%\ClipboardTool（02-spec/02 §1 契约）：历史 JSON、图片、设置同目录。
        // T03 起接 JsonStore：启动读旧档（坏档先备份），变更自动落盘。
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool");
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory,
            new ImageFileStore(dataDir),
            // 新 id UUID 带连字符形状（与 legacy uuid::Uuid::new_v4、旧档样例一致，02-spec/02 §1）
            () => Guid.NewGuid().ToString(),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _history = new HistoryService(store, new JsonStore(dataDir));
        _history.LoadFromStorage();
        _watch = new ClipboardWatchService(
            new ClipboardReader(), new ClipboardSequenceReader(), _history, new ClipboardWriter());
        _paste = new PasteService(
            _history,
            _watch,
            capturedFocus: () => _capturedFocus,
            restoreAndPaste: target => FocusPasteRestore.RestoreAndPaste(target, paste: true),
            hidePanel: HidePanelAfterPaste,
            reportFocusError: (stage, _) => ShowStatus(PasteChain.FocusErrorMessage(stage)));

        // 历史变更（监听线程/链路线程触发）归队 UI 渲染
        _history.EntriesChanged += () => Dispatcher.BeginInvoke(() => _panel?.ReloadEntries(_history!.Entries));
        _panel.CardPasteRequested += id => RequestPaste(id);
        _panel.ReloadEntries(_history.Entries);

        _clipboardSource = new ClipboardMessageSource();
        _watch.Start(_clipboardSource);

        // 键位计划按面板状态重算（legacy ADR-0006/0010：已生效键集合由执行注册者维护，按模式让位）：
        // 停靠态 = {呼出键}；呼出浏览态 = {呼出键, Esc 停靠, Enter 复制并粘贴（F11/F18）}。执行者按差量增删。
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

        // 呼出保存快照（F14）：此刻前台仍是用户原窗口（浏览态面板不激活）
        void SummonWithSnapshot(PanelWindow panel)
        {
            _capturedFocus = FocusPasteRestore.Capture();
            panel.Summon();
        }

        void SummonFromTray()
        {
            if (_panel is { IsDocked: true } panel)
            {
                SummonWithSnapshot(panel);
            }
        }

        void ApplyKeys(bool isSummoned)
        {
            var plan = isSummoned
                ? new HotkeyPlan([HotkeyPlan.SummonDefault, HotkeyPlan.BrowseDock, HotkeyPlan.BrowseEnter])
                : HotkeyPlan.Default;
            _hotkeys.ApplyPlan(plan.PlanDiff(_hotkeys.EffectiveKeys), OnKeyTriggered);
        }

        void OnKeyTriggered(HotkeyCombo combo)
        {
            if (combo == HotkeyPlan.SummonDefault)
            {
                if (_panel is { IsDocked: true })
                {
                    // 呼出保存快照（F14）：此刻前台仍是用户原窗口（浏览态面板不激活）
                    _capturedFocus = FocusPasteRestore.Capture();
                    _panel.Summon();
                }
                else
                {
                    _capturedFocus = null;
                    _panel?.Dock();
                }
            }
            else if (combo == HotkeyPlan.BrowseDock)
            {
                _capturedFocus = null;
                _panel?.Dock();
            }
            else if (combo == HotkeyPlan.BrowseEnter)
            {
                if (_panel?.SelectedCardId is { } id)
                {
                    RequestPaste(id);
                }
            }
        }

        void RequestPaste(string id)
        {
            if (_panel is not { IsDocked: false })
            {
                return; // 停靠态无粘贴意图
            }
            // 链路整体进模式执行线程（本仓库 ADR-0004）：UI 只发具名意图、渲染事实
            _executor?.Post(() =>
            {
                var result = _paste!.Paste(id);
                Dispatcher.BeginInvoke(() => _panel?.ShowResult(result));
            });
        }

        void HidePanelAfterPaste()
        {
            // 粘贴已把焦点归还原窗口，隐藏时不再重复恢复（F12）；归队 UI 线程执行停靠
            Dispatcher.BeginInvoke(() =>
            {
                if (_panel is { IsDocked: false } panel)
                {
                    panel.Dock();
                }
            });
        }

        void ShowStatus(string message)
        {
            Dispatcher.BeginInvoke(() => _panel?.ShowStatus(message));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _panel = null;
        _hotkeys?.Dispose();
        _clipboardSource?.Dispose();
        _executor?.Dispose();
        base.OnExit(e);
    }
}
