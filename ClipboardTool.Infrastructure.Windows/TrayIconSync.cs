namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 托盘图标同步状态机（F29；legacy tray.rs sync_icon 的去重键与 HICON 生命周期）：
/// 「明暗 + 主屏缩放」变化经 IconKey 去重，同键不动（display-metrics 高频触发也不重设）；
/// 键变才换图，换图先销毁旧 HICON 再加载新图（句柄不泄漏）；加载失败保持旧图不记账
/// （下次同键仍会重试）。真实 HICON 工厂（PNG 资产 → HICON）归 TrayIconHost 接线。
/// </summary>
public sealed class TrayIconSync
{
    /// <summary>HICON 工厂端口：加载失败返回 null。</summary>
    public interface ITrayIconFactory
    {
        IntPtr? Load(bool dark, int size);

        void Destroy(IntPtr hicon);
    }

    private readonly ITrayIconFactory _factory;
    private string? _lastKey;
    private IntPtr _lastIcon;

    public TrayIconSync(ITrayIconFactory factory) => _factory = factory;

    /// <summary>当前生效的 HICON（无图时 IntPtr.Zero）。</summary>
    public IntPtr CurrentIcon => _lastIcon;

    /// <summary>按当前明暗与主屏缩放同步图标：返回当前生效的 HICON。</summary>
    public IntPtr Sync(bool dark, double scale)
    {
        var key = TrayIconDecider.IconKey(dark, scale);
        if (key == _lastKey)
        {
            return _lastIcon; // 同键不重设：图标与句柄都不动
        }
        var loaded = _factory.Load(dark, TrayIconDecider.SizeForScale(scale));
        if (loaded is not { } hicon)
        {
            return _lastIcon; // 加载失败：保持旧图、不记账（不更新 _lastKey），下次同键仍会重试
        }
        if (_lastIcon != IntPtr.Zero)
        {
            _factory.Destroy(_lastIcon); // 先销毁旧句柄再换新：句柄不泄漏
        }
        _lastKey = key;
        _lastIcon = hicon;
        return hicon;
    }

    /// <summary>进程收尾：销毁当前持有的 HICON。</summary>
    public void Dispose()
    {
        if (_lastIcon != IntPtr.Zero)
        {
            _factory.Destroy(_lastIcon);
            _lastIcon = IntPtr.Zero;
        }
        _lastKey = null;
    }
}
