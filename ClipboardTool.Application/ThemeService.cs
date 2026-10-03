using ClipboardTool.Domain.Settings;

namespace ClipboardTool.Application;

/// <summary>
/// 系统明暗键的读取端口（读数归 Infrastructure 注册表实现，判定规则在 Domain.ThemeColorRules）。
/// 返回 null 表示「这台机器没写过这个键」，不是「值为 0」。
/// </summary>
public interface IThemeSystemPort
{
    /// <summary>应用模式键 AppsUseLightTheme（面板内容皮肤用）。</summary>
    uint? ReadAppsUseLightTheme();

    /// <summary>Windows 模式键 SystemUsesLightTheme（任务栏/开始菜单用，托盘图标看它）。</summary>
    uint? ReadSystemUsesLightTheme();

    /// <summary>系统主题广播（WM_SETTINGCHANGE / 注册表变更监听触发）。可能在任意线程触发。</summary>
    event Action? ThemeChanged;
}

/// <summary>
/// ThemeService：主题偏好的单一权威（F26–F28）。三态（跟随系统/亮/暗）的落盘、
/// 生效明暗推导与两条独立分派都从这里走——面板内容跟应用模式键、托盘图标跟任务栏键，
/// 「自定义」模式下两键相反时各判各的（ADR-0012）。落盘失败不推进偏好、不发事件
/// （会话内不假装切换成功），调用方与用户都可以重试。切换经 ThemeSync 的代次与
/// pending 门：三态循环切换进行中不接受下一次，跟随系统的广播读取迟到了不覆盖手选。
/// </summary>
public sealed class ThemeService
{
    private readonly ISystemThemePort _system;
    private readonly IPersistPort _persist;
    private readonly ThemeSync _sync = new();
    private readonly object _gate = new();
    private bool? _lastPanelDark; // 同值不重发：系统广播可能高频到达，重绘风暴由权威侧拦
    private bool? _lastTrayDark;

    /// <summary>面板内容生效明暗变化（渲染层换 ResourceDictionary）。</summary>
    public event Action<bool>? PanelThemeChanged;

    /// <summary>托盘图标明暗变化（图标档位由 TrayIconSync 按 scale 另行去重）。</summary>
    public event Action<bool>? TrayThemeChanged;

    /// <summary>偏好变化（托盘菜单重建：主题子菜单标题与勾选）。</summary>
    public event Action? MenuChanged;

    /// <summary>注册表键读取端口（仅测试代码可见的替身需要实现）。</summary>
    public interface ISystemThemePort : IThemeSystemPort
    {
    }

    /// <summary>偏好落盘端口：成功返回 true（失败=偏好不推进）。</summary>
    public interface IPersistPort
    {
        bool SaveTheme(ThemeKind theme);
    }

    public ThemeService(ISystemThemePort system, IPersistPort persist, ThemeKind initial)
    {
        _system = system;
        _persist = persist;
        _sync.ReceiveChange(initial);
        _system.ThemeChanged += RefreshFromSystem;
    }

    /// <summary>当前偏好（三态）。</summary>
    public ThemeKind Preference => _sync.Current.Preference ?? ThemeKind.System;

    /// <summary>面板内容当前有效明暗（未分派过时按当前偏好现读）。</summary>
    public bool IsPanelDark => _lastPanelDark ?? PanelIsDark(Preference);

    /// <summary>托盘图标当前有效明暗（未分派过时按当前偏好现读）。</summary>
    public bool IsTrayDark => _lastTrayDark ?? TrayIsDark(Preference);

    /// <summary>
    /// 手选主题的单一权威入口（托盘菜单三态项/启动按钮/初始化）：落盘 → 偏好推进 →
    /// 面板与托盘两路明暗分派 → 菜单重建通知。落盘失败原样保留旧偏好。
    /// </summary>
    public bool SetTheme(ThemeKind theme)
    {
        lock (_gate)
        {
            if (!_persist.SaveTheme(theme))
            {
                return false;
            }
            _sync.ReceiveChange(theme);
        }
        Dispatch(theme);
        return true;
    }

    /// <summary>三态循环切换（亮→暗→系统→亮）：落盘失败保留原偏好，pending 释放后可重试。</summary>
    public bool Toggle()
    {
        var next = _sync.BeginToggle();
        if (next is null)
        {
            return false;
        }
        var ok = SetTheme(next.Value);
        _sync.FinishToggle(); // 成败都释放：失败侧保留原偏好即可重试
        return ok;
    }

    /// <summary>
    /// 系统主题广播的响应：跟随系统时按应用模式键重推面板明暗（代次化读取，
    /// 与手选共享同一真源）；手动模式下广播不改变任何东西。键值没变不发事件。
    /// </summary>
    public void RefreshFromSystem()
    {
        ThemeKind preference;
        lock (_gate)
        {
            preference = Preference;
        }
        if (preference != ThemeKind.System)
        {
            return;
        }
        var generation = _sync.BeginRead();      // 读取前登记代次
        var apps = _system.ReadAppsUseLightTheme();
        var system = _system.ReadSystemUsesLightTheme();
        lock (_gate)
        {
            // 迟到的读取：期间发生过手选（ReceiveChange 推进了代次）则整体丢弃
            if (!_sync.AcceptRead(generation, ThemeKind.System) || Preference != ThemeKind.System)
            {
                return;
            }
        }
        var panelDark = ThemeColorRules.AppIsDark(apps);
        var trayDark = ThemeColorRules.TaskbarIsDark(system, apps);
        Dispatch(panelDark, trayDark, rebuildMenu: false);
    }

    /// <summary>手选路径的两路分派：面板跟应用键、托盘跟任务栏键（跟随系统时同样各自判）。</summary>
    private void Dispatch(ThemeKind theme)
    {
        Dispatch(PanelIsDark(theme), TrayIsDark(theme), rebuildMenu: true);
    }

    /// <summary>两路分派 + 同值去重：无变化的通道不发事件。手选路径额外通知菜单重建。</summary>
    private void Dispatch(bool panelDark, bool trayDark, bool rebuildMenu)
    {
        if (_lastPanelDark != panelDark)
        {
            _lastPanelDark = panelDark;
            PanelThemeChanged?.Invoke(panelDark);
        }
        if (_lastTrayDark != trayDark)
        {
            _lastTrayDark = trayDark;
            TrayThemeChanged?.Invoke(trayDark);
        }
        if (rebuildMenu)
        {
            MenuChanged?.Invoke();
        }
    }

    private bool PanelIsDark(ThemeKind theme) => theme switch
    {
        ThemeKind.Light => false,
        ThemeKind.Dark => true,
        _ => ThemeColorRules.AppIsDark(_system.ReadAppsUseLightTheme()),
    };

    private bool TrayIsDark(ThemeKind theme) => theme switch
    {
        ThemeKind.Light => false,
        ThemeKind.Dark => true,
        _ => ThemeColorRules.TaskbarIsDark(
            _system.ReadSystemUsesLightTheme(), _system.ReadAppsUseLightTheme()),
    };
}
