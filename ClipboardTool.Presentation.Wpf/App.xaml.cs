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
    private MouseHook? _mouseHook;
    private JsonStore? _settingsStore;
    private AppSettings _settings = AppSettings.Default;
    private DispatcherTimer? _captureSuccessTimer;
    private StartupService? _startup;
    private bool _isElevated;

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

    // —— 启动诊断日志（startup.log）已按 F39/F40 收尾移除（T08 交接项）：
    //    日志仅为 T08 真机排查启动问题临时引入，移除前已冷启动验证启动路径无日志依赖。
    //    崩溃取证语义（F40 panic.log）未实现，见验收矩阵 F39/F40 行。

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // —— 单实例（F34，必须最先判）：mutex 归属判「谁是首个实例」；第二实例经 pipe
        //    投递呼出请求后退出（请求仅接受本用户：pipe 名含用户 SID；提权是同一 SID 天然互通）。
        //    服务端未就绪时客户端有限等待（最多 5 秒，legacy 口径）；此处在启动路径上同步等待是
        //    有意为之：投递完成前第二实例不能退。
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool");
        _settingsStore = new JsonStore(dataDir);
        _settings = _settingsStore.ReadSettings();

        // —— 静默启动通道（F36）：提权生产构建在启动尾部把计划任务事实收敛到持久化意图 ——
        _isElevated = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        _startup = new StartupService(
            new ScheduledTaskRegistrar(),
            ScheduledTaskBuilder.DefaultTaskName,
            Environment.ProcessPath ?? string.Empty);

        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        var summonPipe = $"ClipboardTool-{sid}-summon";
        var gate = new SingleInstanceGate($"ClipboardTool-{sid}-instance");        if (!gate.TryAcquire())
        {
            // 经线程池等待（不直接 GetResult）：本方法跑在 UI 线程的 Dispatcher 上下文上，
            // 直接同步阻塞会让 await 续体排队回一个已被阻塞的 Dispatcher → 经典死锁。
            var delivered = Task.Run(() => SummonClient.SummonAsync(summonPipe, timeoutMs: 5000))
                .GetAwaiter().GetResult();
            Shutdown(delivered ? 0 : 1);
            return;
        }
        var summonServer = new SummonServer(summonPipe);
        summonServer.SummonReceived += () => Dispatcher.BeginInvoke(SummonFromTray);

        _panel = new PanelWindow();
        _hotkeys = new HotkeyExecutor();
        _executor = new ModeExecutor();
        _coordinator = new PanelCoordinator(new PanelModesHost(this), new DispatcherDelayScheduler(Dispatcher));

        // —— 主题（F26–F28）：ThemeService 单一权威；注册表监听线程的广播归队 UI 线程。
        //    落盘失败不推进偏好（可重试）；面板跟应用模式键、托盘跟任务栏键，两路独立判定。
        var watcher = new RegistryThemeWatcher();
        _theme = new ThemeService(watcher, new ThemePersistPort(this), _settings.Theme);
        _theme.PanelThemeChanged += dark => Dispatcher.BeginInvoke(() => ApplyPanelTheme(dark));
        _theme.TrayThemeChanged += dark => Dispatcher.BeginInvoke(() => SyncTrayIcon(dark));
        _theme.MenuChanged += () => Dispatcher.BeginInvoke(RebuildTrayMenu);
        watcher.ThemeChanged += () => Dispatcher.BeginInvoke(_theme.RefreshFromSystem);
        watcher.Start();
        ApplyPanelTheme(PanelIsDark(_settings.Theme)); // 启动即落当前有效皮肤（不落盘不发事件链）

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
        };

        // —— 托盘（F29/F30/F33）：完整菜单六项 + 主题子菜单；五档精确图标按主屏缩放取档；
        //    主题/缩放变化经 TrayIconSync 同键去重后落地。 ——
        _tray = new TrayIconHost("ClipboardTool");
        _tray.SummonRequested += SummonFromTray;
        _tray.PointerEntered += () => Dispatcher.BeginInvoke(() => SyncTrayIcon(TrayIsDark(_settings.Theme)));
        _tray.MenuItemSelected += id => Dispatcher.BeginInvoke(() => OnTrayMenu(id));
        RebuildTrayMenu();
        SyncTrayIcon(TrayIsDark(_settings.Theme));

        // 呼出键没注册上=整会话热键哑；按 5s/20s 现读设置重试两遍（差量幂等，F32）
        var retry = new SummonKeyRetry(
            new DispatcherDelayScheduler(Dispatcher),
            readCurrent: () => ParsedToggle(),
            apply: combo => _coordinator!.SetToggleShortcut(combo));
        retry.Start();

        _panel.Show();
        summonServer.Start();

        // 启动收敛放尾部：一次 schtasks 查询（无动作时不注册），结果只记诊断不阻断启动
        var convergence = _startup.ConvergeOnStartup(IsDevelopmentBuild, _isElevated, _settings.AutoStart);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _panel = null;
        _deletion?.Dispose(); // 未到期条目保留存档（6 秒内强退不提交删除，F25）
        _hotkeys?.Dispose();
        _clipboardSource?.Dispose();
        _mouseHook?.Dispose();
        _executor?.Dispose();
        base.OnExit(e);
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

    // —— 主题编排（F26–F28） ——

    /// <summary>面板内容皮肤：三态 → 有效明暗（跟随系统看应用模式键）→ 切资源字典。</summary>
    private bool PanelIsDark(ThemeKind theme) => theme switch
    {
        ThemeKind.Light => false,
        ThemeKind.Dark => true,
        _ => _theme?.IsPanelDark ?? false,
    };

    /// <summary>托盘图标明暗：跟随系统看任务栏键（与面板判定互不干扰）。</summary>
    private bool TrayIsDark(ThemeKind theme) => theme switch
    {
        ThemeKind.Light => false,
        ThemeKind.Dark => true,
        _ => _theme?.IsTrayDark ?? false,
    };

    private void ApplyPanelTheme(bool dark)
    {
        var source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
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
                SummonFromTray();
                break;
            case "change-shortcut":
                EnterShortcutCapture();
                break;
            case "autostart":
                ToggleAutoStart();
                break;
            case "clear-history":
                // 托盘立即执行（F33）：先取消删除流程计时防误报，再清库存（含置顶/PNG 联动）；无确认窗
                _deletion!.ClearAll();
                _history!.Clear();
                break;
            case "quit":
                Shutdown();
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
        DockPanel(restoreFocus: true);
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

    /// <summary>主题落盘端口（ThemeService.IPersistPort）：落盘成功才推进内存快照；
    /// 落盘失败偏好不推进不发事件（会话内可重试）。菜单重建由 SetTheme 的 MenuChanged 负责。</summary>
    private sealed class ThemePersistPort(App app) : ThemeService.IPersistPort
    {
        public bool SaveTheme(ThemeKind theme)
        {
            try
            {
                app._settingsStore!.SaveSettings(app._settings with { Theme = theme });
            }
            catch (Exception)
            {
                return false; // 原子写失败=原件不动，主题保持旧值
            }
            app._settings = app._settings with { Theme = theme };
            return true;
        }
    }
}
