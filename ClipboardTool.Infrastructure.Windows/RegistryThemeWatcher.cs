using System.Runtime.InteropServices;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 系统明暗键的注册表读取与变更监听（F26–F28 效果侧；判定规则在 Domain.ThemeColorRules）。
/// 读 HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize 下的两个 DWORD；
/// 键不存在或类型不对返回 null——那是「这台机器没写过这个键」，不是「值为 0」。
/// 变更监听用 RegNotifyChangeKeyValue 异步循环（真机验证项：广播到事件的延迟与去抖）；
/// 事件在监听线程触发，订阅方自行归队（ThemeService 的判定线程安全由其内部锁保证）。
/// </summary>
public sealed class RegistryThemeWatcher : ThemeService.ISystemThemePort
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>应用模式键 AppsUseLightTheme（面板内容皮肤跟它）。</summary>
    public uint? ReadAppsUseLightTheme() => ReadLightFlag("AppsUseLightTheme");

    /// <summary>Windows 模式键 SystemUsesLightTheme（任务栏跟它，托盘图标看它）。</summary>
    public uint? ReadSystemUsesLightTheme() => ReadLightFlag("SystemUsesLightTheme");

    /// <summary>系统主题广播（注册表变更触发）。监听线程上触发。</summary>
    public event Action? ThemeChanged;

    /// <summary>读 Personalize 下的一个 DWORD：缺键/类型不对返回 null。</summary>
    private static uint? ReadLightFlag(string valueName)
    {
        var status = NativeMethods.RegGetValueW(
            NativeMethods.HKEY_CURRENT_USER,
            PersonalizeKey,
            valueName,
            NativeMethods.RRF_RT_REG_DWORD,
            IntPtr.Zero,
            out var data,
            out _);
        return status == 0 ? data : null;
    }

    /// <summary>启动监听（后台线程循环）。真机验证项：亮暗切换的广播时机与频率。</summary>
    public void Start()
    {
        var thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "RegistryThemeWatcher",
        };
        thread.Start();
    }

    private void WatchLoop()
    {
        while (true)
        {
            if (NativeMethods.RegOpenKeyW(NativeMethods.HKEY_CURRENT_USER, PersonalizeKey, out var key) != 0)
            {
                Thread.Sleep(1000); // 键暂时打不开（精简系统）：稍后重试
                continue;
            }
            try
            {
                // 同步通知：调用阻塞到任一值变更（REG_NOTIFY_CHANGE_LAST_SET）即返回。
                // 标准用法；亮暗切换（含「自定义」模式下两键独立变化）都会触发。
                var status = NativeMethods.RegNotifyChangeKeyValue(
                    key, watchSubtree: false, NativeMethods.REG_NOTIFY_CHANGE_LAST_SET,
                    manualResetEvent: IntPtr.Zero, asynchronous: false);
                if (status != 0)
                {
                    Thread.Sleep(1000);
                    continue;
                }
                ThemeChanged?.Invoke();
            }
            finally
            {
                _ = NativeMethods.RegCloseKey(key);
            }
        }
    }
}
