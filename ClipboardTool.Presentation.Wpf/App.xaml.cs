using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.Hotkeys;
using ClipboardTool.Domain.PanelModes;
using ClipboardTool.Domain.PasteChain;
using ClipboardTool.Domain.Settings;
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
    private ThemeService? _theme;
    private TrayIconHost? _tray;
    private TrayContextMenu? _trayMenu;
    private MouseHook? _mouseHook;
    private JsonStore? _settingsStore;
    private AppSettings _settings = AppSettings.Default;
    private DispatcherTimer? _captureSuccessTimer;
    private StartupService? _startup;
    private bool _isElevated;
    private readonly string _dataDir;
    private readonly DiagnosticLog _diagnostics;
    private readonly ProcessDiagnostics _processDiagnostics;
    private readonly string _instanceScope;
    private bool _exitRequested;

    public App() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool"),
        string.Empty) { }

    // 隔离 E2E 宿主复用真实 App 启动链；目录与实例通道均独立，绝不接管用户实例。
    internal App(string dataDirectory, string instanceScope)
    {
        _dataDir = dataDirectory;
        _instanceScope = instanceScope;
        _diagnostics = new DiagnosticLog(new FileDiagnosticSink(_dataDir),
            Environment.GetEnvironmentVariable("CLIPBOARD_TOOL_DIAG"), Environment.ProcessId);
        // 在生成的 Main 调用 InitializeComponent 前接好异常取证，资源初始化失败也有现场。
        _processDiagnostics = new ProcessDiagnostics(_diagnostics, Dispatcher);
        _diagnostics.Vital($"start ui=wpf development={IsDevelopmentBuild} exe={Environment.ProcessPath}");
    }

    /// <summary>开发构建不触碰计划任务事实（F36 三态之一：只记意图）。</summary>
    private static readonly bool IsDevelopmentBuild =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>焦点快照的真源在协调器（FocusTargetSnapshot）：呼出时补拍、退出输入态复用、
    /// 隐藏面板时消费。粘贴链路经端口读取同一份，不再有第二份存储。</summary>
    private FocusTarget? CapturedFocus => _coordinator?.FocusTargetSnapshot;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // —— 单实例（F34，必须最先判）：mutex 归属判「谁是首个实例」；第二实例经 pipe
        //    投递呼出请求后退出（请求仅接受本用户：pipe 名含用户 SID；提权是同一 SID 天然互通）。
        //    服务端未就绪时客户端有限等待（最多 5 秒，legacy 口径）；此处在启动路径上同步等待是
        //    有意为之：投递完成前第二实例不能退。
        var dataDir = _dataDir;
        _settingsStore = new JsonStore(dataDir);
        _settings = _settingsStore.ReadSettings();

        // —— 静默启动通道（F36）：提权生产构建在启动尾部把计划任务事实收敛到持久化意图 ——
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        _isElevated = new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        _startup = new StartupService(
            new ScheduledTaskRegistrar(),
            ScheduledTaskBuilder.DefaultTaskName,
            Environment.ProcessPath ?? string.Empty, _diagnostics);
        _diagnostics.Vital($"startup-channel elevated={_isElevated} autostart={_settings.AutoStart}");

        var sid = identity.User?.Value ?? string.Empty;
        var summonPipe = $"ClipboardTool-{sid}{_instanceScope}-summon";
        var gate = new SingleInstanceGate($"ClipboardTool-{sid}{_instanceScope}-instance");
        if (!gate.TryAcquire())
        {
            _diagnostics.Vital("instance-secondary delivery=requested");
            // 第二实例的职责就是把呼出请求投进 pipe 后退出：投递异步进行（不阻塞 UI 线程），
            // 退出时机挂在投递完成回调上——投递完成前退出会让 pipe 丢信（既有语义）。
            // legacy 5s 口径用超时取消表达（SummonAsync 内部有限等待）；异常与超时同样按
            // 「本次未呼出」收场（退出码 1）。
            _ = Task.Run(() => SummonClient.SummonAsync(summonPipe, timeoutMs: 5000))
                .ContinueWith(t =>
                {
                    var delivered = t.Status == TaskStatus.RanToCompletion && t.Result;
                    if (t.IsFaulted)
                    {
                        _ = t.Exception; // 观察异常避免未观察 Task 异常；按未送达收场
                    }
                    _diagnostics.Vital($"instance-secondary delivery={delivered}");
                    RequestExit("secondary-instance", delivered ? 0 : 1);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        var summonServer = new SummonServer(summonPipe);
        summonServer.SummonReceived += () => RequestSummon("instance");

        _panel = new PanelWindow(_diagnostics);
        _hotkeys = new HotkeyExecutor();
        _executor = new ModeExecutor(_diagnostics);
        _coordinator = new PanelCoordinator(new PanelModesHost(this), new DispatcherDelayScheduler(Dispatcher));

        // —— 主题（F26–F28）：ThemeService 单一权威；注册表监听线程的广播归队 UI 线程。
        //    落盘失败不推进偏好（可重试）；面板跟应用模式键、托盘跟任务栏键，两路独立判定。
        var watcher = new RegistryThemeWatcher();
        _theme = new ThemeService(watcher, new ThemePersistPort(this), _settings.Theme);
        _theme.PanelThemeChanged += dark => Dispatcher.BeginInvoke(() => ApplyPanelTheme(dark));
        _theme.TrayThemeChanged += dark => Dispatcher.BeginInvoke(() => SyncTrayIcon(dark));
        _theme.MenuChanged += () => Dispatcher.BeginInvoke(RebuildTrayMenu);
        // 面板主题按钮（F26）：偏好推进后回推面板（图标/提示与托盘子菜单同源同步）
        _theme.MenuChanged += () => Dispatcher.BeginInvoke(() => _panel?.SetThemePreference(_theme!.Preference));
        watcher.ThemeChanged += () => Dispatcher.BeginInvoke(_theme.RefreshFromSystem);
        watcher.Start();
        ApplyPanelTheme(_theme!.IsPanelDark); // 启动即落当前有效皮肤（ThemeService 单一权威，不落盘不发事件链）
        _panel.SetThemePreference(_theme.Preference); // 面板主题按钮初值（展示当前偏好，F26）

        // 共享缩略图缓存（T06）：解码 Task.Run 后台线程（结果 Freeze）、回调归队 UI；
        // 双上限（32 张 / 24 MiB）、按 Id 记忆化、失效与代次规则在缓存内部
        _panel.SetThumbnailCache(new ThumbnailCache(
            WpfThumbnailDecoder.Decode,
            work => Task.Run(work),
            action => Dispatcher.BeginInvoke(action)));

        // 存档目录 %APPDATA%\ClipboardTool（02-spec/02 §1 契约）：历史 JSON、图片、设置同目录。
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory,
            new ImageFileStore(dataDir),
            // 新 id UUID 带连字符形状（与 legacy uuid::Uuid::new_v4、旧档样例一致，02-spec/02 §1）
            () => Guid.NewGuid().ToString(),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _history = new HistoryService(store, _settingsStore);
        _history.LoadFromStorage();
        _watch = new ClipboardWatchService(
            new ClipboardReader(), new ClipboardSequenceReader(), _history, new ClipboardWriter(),
            // 来源应用采集（F06）：复制处理时取前台窗口信息，失败降级未知来源
            new ForegroundSource(), _diagnostics);
        _paste = new PasteService(
            _history,
            _watch,
            capturedFocus: () => CapturedFocus,
            restoreAndPaste: target => FocusPasteRestore.RestoreAndPaste(target, paste: true),
            hidePanel: HidePanelAfterPaste,
            reportFocusError: (stage, _) => ShowStatus(PasteChain.FocusErrorMessage(stage)));

        // 延迟删除（F25）：摘除/撤销窗口/到期真删。计时经 Dispatcher 调度器在 UI 线程触发，
        // 与面板编排同线程封闭；跨停靠/呼出继续计时（秒表语义，不随面板隐藏重置）。
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
        _panel.ThemeToggleRequested += () => _theme!.Toggle(); // F26 面板入口：三态循环走权威 Toggle（失败保留原偏好可重试）
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
        _panel.CaptureAttempted += OnCaptureAttempted;
        _panel.CaptureCancelled += OnCaptureCancelled;

        _clipboardSource = new ClipboardMessageSource();
        _watch.Start(_clipboardSource);

        // —— 单击外部停靠（F17）：全局低级鼠标钩子上报真实按下（回调只记录，ADR-0004），
        //    判定（可见/时间窗/面板外）在 Domain.ExternalClickRules，命中走停靠编排。
        //    事件在钩子转发线程触发，归队 UI 线程后与协调器状态同线程封闭判定。
        _mouseHook = new MouseHook();
        _mouseHook.Pressed += (x, y, atMs) =>
            Dispatcher.BeginInvoke(() => DockIfClickedOutside(x, y, atMs));
        _mouseHook.Start();

        // 键位计划由协调器按四态推导差量（F18–F21）：停靠态={呼出键}；呼出浏览态=呼出键+八导航键；
        // 搜索/备注/捕获按让位矩阵增删。注册动作在宿主 RegisterKey 里按动作分发（呼出/导航）。
        // 注意顺序：面板自身在 SourceInitialized 里先 Dock，此前协调器尚未给键；初始计划由
        // Attach 之后的 SetToggleShortcut 落地（停靠态目标集合）。呼出键来自设置（码表解码）。
        _panel.SourceInitialized += (_, _) =>
        {
            _hotkeys.Attach(_panel);
            _coordinator.SetToggleShortcut(ParsedToggle());
            _diagnostics.Vital($"window-ready ui=wpf hwnd=0x{_panel.Hwnd.ToInt64():X}");
        };
        _panel.ContentRendered += (_, _) => _diagnostics.Vital("wpf-ui-ready");

        // —— 托盘（F29/F30/F33）：完整菜单六项 + 主题子菜单；五档精确图标按主屏缩放取档；
        //    主题/缩放变化经 TrayIconSync 同键去重后落地。 ——
        _trayMenu = new TrayContextMenu(Resources);
        _tray = new TrayIconHost("ClipboardTool", _trayMenu.Show, diagnostics: _diagnostics);
        _tray.SummonRequested += () => RequestSummon("tray-click");
        _tray.PointerEntered += () => Dispatcher.BeginInvoke(() => SyncTrayIcon(_theme!.IsTrayDark));
        _tray.MenuItemSelected += id => Dispatcher.BeginInvoke(() => OnTrayMenu(id));
        RebuildTrayMenu();
        SyncTrayIcon(_theme!.IsTrayDark);

        // 呼出键没注册上=整会话热键哑；按 5s/20s 现读设置重试两遍（差量幂等，F32）
        var retry = new SummonKeyRetry(
            new DispatcherDelayScheduler(Dispatcher),
            readCurrent: () => ParsedToggle(),
            apply: combo => _coordinator!.SetToggleShortcut(combo));
        retry.Start();

        _panel.Show();
        var readable = WindowPlacer.TryGetPhysicalRect(_panel.Hwnd, out var warmupRect);
        _diagnostics.Vital($"warmup ui=wpf docked={_panel.IsDocked} readable={readable} " +
            $"actual={warmupRect.Left},{warmupRect.Top},{warmupRect.Right},{warmupRect.Bottom}");
        summonServer.Start();

        // 启动收敛放尾部：一次 schtasks 查询（无动作时不注册），结果只记诊断不阻断启动
        _ = _startup.ConvergeOnStartup(IsDevelopmentBuild, _isElevated, _settings.AutoStart);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogExitRequest("wpf", e.ApplicationExitCode);
        try
        {
            _panel = null;
            _deletion?.Dispose(); // 未到期条目保留存档（6 秒内强退不提交删除，F25）
            _hotkeys?.Dispose();
            _clipboardSource?.Dispose();
            _mouseHook?.Dispose();
            _executor?.Dispose();
            _trayMenu?.Dispose();
            _tray?.Dispose();
            base.OnExit(e);
        }
        catch (Exception exception)
        {
            _diagnostics.Panic("exit-cleanup", exception);
            throw; // 保留全局异常钩子，让 CLR 记录实际异常退出。
        }
        _processDiagnostics.CompleteExit(e.ApplicationExitCode);
        _processDiagnostics.Dispose();
    }

    private void RequestExit(string source, int code = 0)
    {
        LogExitRequest(source, code);
        Shutdown(code);
    }

    private void LogExitRequest(string source, int code)
    {
        if (!_exitRequested)
        {
            _exitRequested = true;
            _diagnostics.Vital($"exit-requested source={source} code={code}");
        }
    }

    // —— 设置（F37 落盘口径）：内存快照单点写，落盘与托盘菜单刷新随行 ——

    /// <summary>设置变更唯一入口：改快照 → 落盘 → 托盘菜单重建（文案随设置变）。</summary>
    private void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        _settings = change(_settings);
        try
        {
            _settingsStore!.SaveSettings(_settings);
        }
        catch (Exception)
        {
            // 落盘失败不阻断会话内生效（原子写保证原件不动）；诊断日志归 T08
        }
        RebuildTrayMenu();
    }

    /// <summary>
    /// 「落盘成功才推进」的设置变更单点（UpdateSettings 的守卫变体）：失败不推进内存快照。
    /// 主题偏好走此口（ThemeService 语义：落盘失败偏好不推进不发事件）；菜单重建由
    /// SetTheme 的 MenuChanged 事件负责，此处不重复。除此之外不再有直改 _settings 的旁路。
    /// </summary>
    private bool UpdateSettingsPersistFirst(Func<AppSettings, AppSettings> change)
    {
        var next = change(_settings);
        try
        {
            _settingsStore!.SaveSettings(next);
        }
        catch (Exception)
        {
            return false; // 原子写失败=原件不动，偏好保持旧值
        }
        _settings = next;
        return true;
    }

    /// <summary>当前呼出键（设置串按码表解码；解析不了回落默认键——坏档不哑热键）。</summary>
    private HotkeyCombo ParsedToggle() =>
        AccelCodec.Parse(_settings.Shortcut) ?? HotkeyPlan.SummonDefault;

    /// <summary>
    /// 「开机启动」开关三态（F36）：开发构建只记意图、未提权延后到下次提权启动收敛、
    /// 已提权立即重建任务事实；重建失败回退（意图不翻转，状态栏提示，任务保持原样可重试）。
    /// 意图落盘仍走 UpdateSettings 单点（快照+落盘+菜单重建）。
    /// </summary>
    private void ToggleAutoStart()
    {
        var newIntent = !_settings.AutoStart;
        var outcome = _startup!.Toggle(IsDevelopmentBuild, _isElevated, newIntent);
        if (outcome.IntentApplied)
        {
            UpdateSettings(s => s with { AutoStart = newIntent });
        }
        else
        {
            ShowStatus(outcome.Message ?? "计划任务更新失败，开机启动未开启");
        }
    }

    // —— 主题编排（F26–F28）：明暗判定单一权威在 ThemeService（IsPanelDark/IsTrayDark） ——

    private void ApplyPanelTheme(bool dark)
    {
        var source = new Uri(dark ? "/ClipboardTool;component/Themes/Dark.xaml" : "/ClipboardTool;component/Themes/Light.xaml", UriKind.Relative);
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = source };
    }

    private void SyncTrayIcon(bool dark)
    {
        var scale = new ScreenMetricsProvider().GetPrimary()?.DpiScale ?? 1.0;
        _tray?.SyncIcon(dark, scale);
    }

    /// <summary>托盘菜单（F30）：六项 + 主题子菜单；三条动态文案由 TrayIconDecider 收口。</summary>
    private void RebuildTrayMenu()
    {
        if (_tray is null)
        {
            return;
        }
        var labels = TrayIconDecider.MenuLabels(_settings.Shortcut, _settings.AutoStart);
        _tray.SetMenuItems(
        [
            new TrayMenuItem("show", "显示剪贴板面板"),
            new TrayMenuItem("change-shortcut", labels.Shortcut),
            new TrayMenuItem("sep1", Separator: true),
            new TrayMenuItem("autostart", labels.Autostart, Checked: _settings.AutoStart),
            new TrayMenuItem("theme", TrayIconDecider.ThemeMenuTitle(_settings.Theme),
                SubItems: TrayIconDecider.ThemeMenuItems(_settings.Theme)
                    .Select(item => new TrayMenuItem(item.Id, item.Label, item.Checked))
                    .ToList()),
            new TrayMenuItem("clear-history", "清空历史"),
            new TrayMenuItem("sep2", Separator: true),
            new TrayMenuItem("quit", "退出"),
        ]);
    }

    /// <summary>托盘菜单分发：id → 动作。主题项经纯判定映射；认不出的 id 什么都不做。</summary>
    private void OnTrayMenu(string id)
    {
        switch (id)
        {
            case "show":
                RequestSummon("tray-menu");
                break;
            case "change-shortcut":
                EnterShortcutCapture();
                break;
            case "autostart":
                ToggleAutoStart();
                break;
            case "clear-history":
                // 清空历史编排（F33）：次序收口在 PendingDeletionService.ClearAll（F25 排空 + 清库存）
                _deletion!.ClearAll();
                break;
            case "quit":
                RequestExit("tray-menu");
                break;
            default:
                if (TrayIconDecider.ThemeOfMenuId(id) is { } theme)
                {
                    _theme!.SetTheme(theme);
                }
                break;
        }
    }

    // —— 换键捕获编排（F31） ——

    /// <summary>
    /// 进入捕获：状态机退输入态+注销全部全局键 → 呼出面板+覆盖层 → 聚焦面板收键
    /// （捕获态 NeedsFocus=false 是状态机的键位语义；覆盖层的键盘采集由编排侧显式聚焦）。
    /// </summary>
    private void EnterShortcutCapture()
    {
        if (_panel is null || !_coordinator!.EnterInput(PanelMode.ShortcutCapture))
        {
            return;
        }
        _diagnostics.Vital("summon-req src=shortcut-capture");
        _diagnostics.Vital("summon-run src=shortcut-capture latency_ms=0");
        _panel.Summon();
        _panel.ShowCaptureOverlay();
        FocusAdapter.SetNoActivate(_panel, on: false);
        FocusAdapter.ActivateForInput(_panel);
    }

    /// <summary>覆盖层录入：校验（缺修饰）→ 试注册（占用/无效保旧键）→ 成功持久化 1200ms 收层。</summary>
    private void OnCaptureAttempted(HotkeyCombo combo)
    {
        var verdict = CaptureKeyRules.Validate(combo);
        if (verdict != CaptureVerdict.Ok)
        {
            _panel?.SetCaptureStatus(CaptureKeyRules.MessageOf(verdict), ok: false);
            return;
        }
        if (!_coordinator!.TrySetToggleShortcut(combo))
        {
            _panel?.SetCaptureStatus($"{combo.DisplayName} 已被占用或无效，请换一个", ok: false);
            return;
        }
        UpdateSettings(s => s with { Shortcut = AccelCodec.Encode(combo) });
        _panel?.SetCaptureStatus($"已设置为 {combo.DisplayName}", ok: true);
        ScheduleCaptureOverlayClose();
    }

    /// <summary>Esc 取消：状态机退捕获态恢复旧键（差量自动恢复），capture-end 事件收层。</summary>
    private void OnCaptureCancelled()
    {
        _coordinator!.ExitInput(PanelMode.ShortcutCapture, restoreFocus: false);
        // ExitInputInternal 会发 CaptureEnd → SendCaptureEnd 收层；这里无需重复
    }

    /// <summary>成功路径的 1200ms 收层（legacy 渲染层 setTimeout；失败不收层可继续录）。</summary>
    private void ScheduleCaptureOverlayClose()
    {
        _captureSuccessTimer?.Stop();
        _captureSuccessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _captureSuccessTimer.Tick += (_, _) =>
        {
            _captureSuccessTimer!.Stop();
            _captureSuccessTimer = null;
            _panel?.HideCaptureOverlay();
        };
        _captureSuccessTimer.Start();
    }

    // —— 呼出/停靠编排 ——

    /// <summary>
    /// 呼出时序（legacy show_on capture=true）：先拍焦点快照（失败静默）→ 协调器 Show
    /// （重置搜索/备注态 + 键位差量 + panel:shown 呼出重置）→ 面板几何落地。
    /// </summary>
    private void SummonPanel(string source, long requestedAt)
    {
        _diagnostics.Vital($"summon-run src={source} latency_ms={Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:F1}");
        if (_panel is not { IsDocked: true })
        {
            _diagnostics.Vital($"summon-skipped reason={(_panel is null ? "not-ready" : "already-visible")}");
            return;
        }
        _coordinator!.EnsureFocusSnapshot();
        _coordinator.Show();
        _panel.Summon();
    }

    private void RequestSummon(string source)
    {
        var requestedAt = Stopwatch.GetTimestamp();
        _diagnostics.Vital($"summon-req src={source}");
        if (Dispatcher.CheckAccess())
        {
            SummonPanel(source, requestedAt);
            return;
        }
        try
        {
            var operation = Dispatcher.BeginInvoke(() => SummonPanel(source, requestedAt));
            operation.Aborted += (_, _) => _diagnostics.Vital($"dispatch-lost action=summon src={source}");
            if (operation.Status == DispatcherOperationStatus.Aborted)
            {
                _diagnostics.Vital($"dispatch-lost action=summon src={source}");
            }
        }
        catch (Exception exception)
        {
            _diagnostics.Vital($"dispatch-failed action=summon src={source} type={exception.GetType().FullName}");
        }
    }

    /// <summary>
    /// 停靠编排（Esc 停靠 / 呼出键收起 / 粘贴成功收起共用）：先退输入态并注销导航键，
    /// 再几何落位；restoreFocus=true 时快照由协调器归还并消费。
    /// </summary>
    private void DockPanel(bool restoreFocus, string reason)
    {
        if (_panel is not { IsDocked: false })
        {
            return;
        }
        _coordinator!.Hide(restoreFocus);
        _panel.Dock();
        var readable = WindowPlacer.TryGetPhysicalRect(_panel.Hwnd, out var actual);
        _diagnostics.Vital($"hide reason={reason} restore_focus={restoreFocus} readable={readable} " +
            $"actual={actual.Left},{actual.Top},{actual.Right},{actual.Bottom}");
    }

    /// <summary>浏览态 Esc 停靠（宿主事件入口）。</summary>
    private void DockFromPanel() => DockPanel(restoreFocus: true, reason: "escape");

    /// <summary>
    /// 单击面板外部 → 停靠（F17）：全局钩子上报的一次真实按下。判定全部在 Domain 纯规则
    /// （ExternalClickRules.ShouldDockOnOutsideClick）：面板可见 + 点击晚于本次呼出
    /// （时间窗防护：呼出瞬间的惯用手势不误收）+ 明确落在面板物理矩形外（矩形读不到不动作）。
    /// 输入态不豁免（legacy hide 统一路径逐层退出）；多击连点每次按下判一次（legacy 口径）。
    /// </summary>
    private void DockIfClickedOutside(int x, int y, long clickedAtMs)
    {
        if (_panel is not { IsDocked: false } panel
            || _coordinator is not { Visible: true } coordinator)
        {
            return; // 面板不可见：不动作
        }
        bool? inside = WindowPlacer.TryGetPhysicalRect(panel.Hwnd, out var rect)
            ? ExternalClickRules.ContainsPoint(rect.Left, rect.Top, rect.Right, rect.Bottom, x, y)
            : null;
        if (!ExternalClickRules.ShouldDockOnOutsideClick(
                panelVisible: true, coordinator.ShownAtMs, clickedAtMs, inside))
        {
            return;
        }
        DockPanel(restoreFocus: true, reason: "outside-click");
    }

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
                RequestSummon("hotkey");
            }
            else
            {
                DockPanel(restoreFocus: true, reason: "hotkey-toggle");
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
        Dispatcher.BeginInvoke(() => DockPanel(restoreFocus: false, reason: "paste-success"));
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
            var registered = app._hotkeys.EffectiveKeys.Contains(combo);
            var message = $"hotkey_register accel={AccelCodec.Encode(combo)} ok={registered}";
            if (action.Kind == PanelKeyKind.Toggle)
            {
                app._diagnostics.Vital(message);
            }
            else
            {
                app._diagnostics.Verbose(message);
            }
            return registered;
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
            // capture-end：捕获取消/强退路径收覆盖层（成功路径由 1200ms 计时自行收）
            app._panel?.HideCaptureOverlay();
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

    /// <summary>主题落盘端口（ThemeService.IPersistPort）：走设置单点 PersistFirst 变体——
    /// 落盘成功才推进内存快照（失败=偏好不推进不发事件，会话内可重试）。</summary>
    private sealed class ThemePersistPort(App app) : ThemeService.IPersistPort
    {
        public bool SaveTheme(ThemeKind theme) =>
            app.UpdateSettingsPersistFirst(s => s with { Theme = theme });
    }
}
