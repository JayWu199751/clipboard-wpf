using System.IO;
using System.Windows;
using System.Windows.Interop;
using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Domain.PanelModes;
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
    private PanelCoordinator? _coordinator;
    private PendingDeletionService? _deletion;

    /// <summary>焦点快照的真源在协调器（FocusTargetSnapshot）：呼出时补拍、退出输入态复用、
    /// 隐藏面板时消费。粘贴链路经端口读取同一份，不再有第二份存储。</summary>
    private FocusTarget? CapturedFocus => _coordinator?.FocusTargetSnapshot;

    // —— 启动诊断日志（临时排查启动不可见/进程退出问题；%LOCALAPPDATA%\ClipboardTool\startup.log） ——
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardTool", "startup.log");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能阻塞启动
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("== OnStartup 开始 ==");
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"[UI线程异常] {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log($"[未处理异常 线程将终止={args.IsTerminating}] {args.ExceptionObject}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Log("[ProcessExit] 进程退出（托管路径）");
        // 心跳：后台线程每 5 秒一行；日志断点即进程死亡时刻（区分托管退出 vs 原生崩溃/外部终止）
        new Thread(() =>
        {
            for (var i = 1; ; i++)
            {
                Thread.Sleep(5000);
                Log($"[心跳] +{i * 5}s");
            }
        })
        { IsBackground = true }.Start();
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"[未观察任务异常] {args.Exception}");
            args.SetObserved();
        };

        base.OnStartup(e);

        _panel = new PanelWindow();
        _hotkeys = new HotkeyExecutor();
        _executor = new ModeExecutor();
        _coordinator = new PanelCoordinator(new PanelModesHost(this), new DispatcherDelayScheduler(Dispatcher));
        Log("核心对象构造完成");

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
        Log($"历史加载完成（{_history.Entries.Count} 条）");
        _watch = new ClipboardWatchService(
            new ClipboardReader(), new ClipboardSequenceReader(), _history, new ClipboardWriter());
        _paste = new PasteService(
            _history,
            _watch,
            capturedFocus: () => CapturedFocus,
            restoreAndPaste: target => FocusPasteRestore.RestoreAndPaste(target, paste: true),
            hidePanel: HidePanelAfterPaste,
            reportFocusError: (stage, _) => ShowStatus(PasteChain.FocusErrorMessage(stage)));

        // 延迟删除（F25）：摘除/撤销窗口/到期真删。计时经 Dispatcher 调度器在 UI 线程触发，
        // 与面板编排同线程封闭；跨停靠/呼出继续计时（秒表语义，不随面板隐藏重置）。
        // 订阅处的归队是防御性的（服务已承诺 UI 线程触发；与 HistoryService 的任意线程事件区分）。
        _deletion = new PendingDeletionService(new DispatcherDelayScheduler(Dispatcher), _history);
        _deletion.HiddenChanged += () => Dispatcher.BeginInvoke(() =>
            _panel?.ReloadEntries(_history!.Entries, _deletion!.HiddenIds));
        _deletion.ToastRequested += toast => Dispatcher.BeginInvoke(() =>
            _panel?.ShowToast(toast.Message, toast.Dim, toast.IsError, toast.ActionLabel, toast.OnAction));

        // 历史变更（监听线程/链路线程触发）归队 UI 渲染；遮罩随快照重注（到期收尾/撤销都会通知）
        _history.EntriesChanged += () => Dispatcher.BeginInvoke(() =>
            _panel?.ReloadEntries(_history!.Entries, _deletion!.HiddenIds));
        _panel.ReloadEntries(_history.Entries, _deletion.HiddenIds);

        // —— 面板事件 → 意图/编排（UI 线程封闭；协调器状态与全部效果调用都在该线程） ——
        _panel.CardPasteRequested += RequestPaste;
        _panel.SearchActivationRequested += EnterSearch;
        _panel.CompositionChanged += composing => _coordinator.SetComposing(composing);
        _panel.NoteEditExitRequested += () => _coordinator.ExitInput(PanelMode.NoteEdit, restoreFocus: true);
        _panel.PinRequested += id => _history.TogglePin(id); // 取消置顶保留 pinnedAt（存档契约）
        // 删除先放弃指向该条的编辑（legacy hide 分支清 noteEdit；无编辑则退态/放弃均无操作）
        _panel.DeleteRequested += id =>
        {
            _panel.AbandonNoteEditIfEditing(id);
            _coordinator!.ExitInput(PanelMode.NoteEdit, restoreFocus: false);
            _deletion!.Request(id);
        };
        _panel.NoteSaveRequested += SaveNote;

        _clipboardSource = new ClipboardMessageSource();
        _watch.Start(_clipboardSource);
        Log("剪贴板监听已启动");

        // 键位计划由协调器按四态推导差量（F18–F21）：停靠态={呼出键}；呼出浏览态=呼出键+八导航键；
        // 搜索/备注/捕获按让位矩阵增删。注册动作在宿主 RegisterKey 里按动作分发（呼出/导航）。
        // 注意顺序：面板自身在 SourceInitialized 里先 Dock，此前协调器尚未给键；初始计划由
        // Attach 之后的 SetToggleShortcut 落地（停靠态目标集合）。
        _panel.SourceInitialized += (_, _) =>
        {
            _hotkeys.Attach(_panel);
            _coordinator.SetToggleShortcut(HotkeyPlan.SummonDefault);
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
        Log("托盘创建完成");

        _panel.Show();
        Log("== OnStartup 完成，面板已 Show ==");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"== OnExit 退出码={e.ApplicationExitCode} ==");
        _panel = null;
        _deletion?.Dispose(); // 未到期条目保留存档（6 秒内强退不提交删除，F25）
        _hotkeys?.Dispose();
        _clipboardSource?.Dispose();
        _executor?.Dispose();
        base.OnExit(e);
    }

    // —— 呼出/停靠编排 ——

    /// <summary>
    /// 呼出时序（legacy show_on capture=true）：先拍焦点快照（失败静默）→ 协调器 Show
    /// （重置搜索/备注态 + 键位差量 + panel:shown 呼出重置）→ 面板几何落地。
    /// </summary>
    private void SummonPanel()
    {
        if (_panel is not { IsDocked: true })
        {
            return;
        }
        _coordinator!.EnsureFocusSnapshot();
        _coordinator.Show();
        _panel.Summon();
    }

    private void SummonFromTray()
    {
        if (_panel is { IsDocked: true })
        {
            SummonPanel();
        }
    }

    /// <summary>
    /// 停靠编排（Esc 停靠 / 呼出键收起 / 粘贴成功收起共用）：先退输入态并注销导航键，
    /// 再几何落位；restoreFocus=true 时快照由协调器归还并消费。
    /// </summary>
    private void DockPanel(bool restoreFocus)
    {
        if (_panel is not { IsDocked: false })
        {
            return;
        }
        _coordinator!.Hide(restoreFocus);
        _panel.Dock();
    }

    /// <summary>浏览态 Esc 停靠（宿主事件入口）。</summary>
    private void DockFromPanel() => DockPanel(restoreFocus: true);

    private void EnterSearch() => _ = _coordinator!.EnterInput(PanelMode.Search);

    /// <summary>
    /// 备注保存（F07）：编辑器侧只 trim，对比与写库都在服务侧（事实源）；空串等同移除。
    /// 保存即退输入态（legacy endNoteEdit → 主进程 exit_input：面板回浏览态并归还前台；
    /// 强退路径协调器已先退，此处 no-op）。成功 toast「备注已保存（dim 前 18 字符）」/
    /// 「备注已移除」，失败提示（F07 保存失败提示）。
    /// </summary>
    private void SaveNote(string id, string rawDraft)
    {
        _coordinator!.ExitInput(PanelMode.NoteEdit, restoreFocus: true);
        var text = rawDraft.Trim();
        var previous = _history!.Find(id)?.Note ?? string.Empty;
        if (text == previous)
        {
            return; // 无差异不写库（legacy finishNoteEditing）
        }
        if (_history.SetNote(id, text))
        {
            _panel?.ShowToast(
                text.Length > 0 ? "备注已保存" : "备注已移除",
                text.Length > 0 ? CompressNoteDim(text) : null,
                isError: false, actionLabel: null, onAction: null);
        }
        else
        {
            _panel?.ShowToast("备注保存失败", null, isError: true, actionLabel: null, onAction: null);
        }
    }

    /// <summary>保存成功 toast 的次级说明（legacy dim：text.slice(0, 18)）。</summary>
    private static string CompressNoteDim(string text) =>
        text.Length > 18 ? text[..18] : text;

    /// <summary>热键触发：呼出走停靠编排，导航动作按「真实按下」分发并武装长按连发（F19）。</summary>
    private void OnKeyAction(PanelKeyAction action)
    {
        if (action.Kind == PanelKeyKind.Toggle)
        {
            if (_panel is { IsDocked: true })
            {
                SummonPanel();
            }
            else
            {
                DockPanel(restoreFocus: true);
            }
        }
        else
        {
            _coordinator?.OnNavPressed(action.NavAction);
        }
    }

    private void RequestPaste(string id)
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

    private void HidePanelAfterPaste()
    {
        // 粘贴已把焦点归还原窗口，隐藏时不再重复恢复（F12）；归队 UI 线程执行停靠
        Dispatcher.BeginInvoke(() => DockPanel(restoreFocus: false));
    }

    private void ShowStatus(string message)
    {
        Dispatcher.BeginInvoke(() => _panel?.ShowStatus(message));
    }

    /// <summary>
    /// 四态状态机的效果宿主（IPanelModesHost 实现）。全部效果都在 UI 线程上执行
    /// （协调器线程封闭保证），窗与执行者的调用不再各自投递。
    /// </summary>
    private sealed class PanelModesHost(App app) : IPanelModesHost
    {
        public bool RegisterKey(HotkeyCombo combo, PanelKeyAction action)
        {
            app._hotkeys!.ApplyPlan(
                new HotkeyDiff([combo], []),
                _ => app.OnKeyAction(action));
            return app._hotkeys.EffectiveKeys.Contains(combo);
        }

        public void UnregisterKey(HotkeyCombo combo) =>
            app._hotkeys!.ApplyPlan(new HotkeyDiff([], [combo]), _ => { });

        public IReadOnlyCollection<HotkeyCombo> CurrentKeys => app._hotkeys!.EffectiveKeys;

        public bool CanInteract() => app._panel is not null;

        public void FocusPanel()
        {
            if (app._panel is not { } panel)
            {
                return;
            }
            FocusAdapter.SetNoActivate(panel, on: false); // 输入态先清 NOACTIVATE（ADR-0002）
            FocusAdapter.ActivateForInput(panel);
        }

        public void BlurPanelIfFocused()
        {
            if (app._panel is { } panel)
            {
                FocusAdapter.SetNoActivate(panel, on: true); // 浏览态常驻不抢前台
            }
        }

        public void SendPanelKey(string action, string? noteEntryId)
        {
            if (action == NavAction.Escape.ActionName())
            {
                // 浏览态 Esc = 完整停靠流程（退输入态/注销导航键/归还快照/几何落位）
                app.DockFromPanel();
                return;
            }
            app._panel?.HandlePanelKey(action, noteEntryId);
        }

        public void SendPanelShown() => app._panel?.HandlePanelShown();

        public void SendCaptureEnd()
        {
            // 捕获覆盖层的呈现与收起归 T07；本票捕获态只参与状态机与键位让位
        }

        public FocusTarget? CaptureFocus() => FocusPasteRestore.Capture();

        public void RestoreFocus(FocusTarget target)
        {
            // 输入态退出归还前台（不注入粘贴）；失败按焦点错误回报
            if (FocusPasteRestore.RestoreAndPaste(target, paste: false) is { } failure)
            {
                app.ShowStatus(PasteChain.FocusErrorMessage(failure.Stage));
            }
        }

        public void ReportNoFocusTarget() =>
            app.ShowStatus(PasteChain.FocusErrorMessage(RestoreStage.Restore));

        public bool ValidateNoteTarget(string? targetId) =>
            targetId is null || app._history!.Find(targetId) is not null;

        public bool IsKeyDown(uint virtualKey) => KeyboardState.IsKeyDown(virtualKey);
    }
}
