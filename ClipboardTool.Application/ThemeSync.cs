using ClipboardTool.Domain.Settings;

namespace ClipboardTool.Application;

/// <summary>
/// 主题偏好的读取代次与切换互斥（F26–F28；legacy themeSync.ts ThemeSynchronizer 移植）。
/// 跟随系统模式的生效主题由广播触发的重新读取决定；读取是异步的，期间可能有更权威的
/// 变更（用户手选、下一次广播）先落地——代次让迟到的读取被丢弃，而不是覆盖较新的偏好。
/// 三态循环切换在落地前（pending）不接受下一次切换，失败释放后可重试。
/// </summary>
public sealed class ThemeSync
{
    private int _readGeneration;
    private bool _togglePending;

    /// <summary>当前偏好（null = 尚未从设置读出）与在途读取判死标记。</summary>
    public readonly record struct Snapshot(ThemeKind? Preference, int PendingReads);

    public ThemeKind? Preference { get; private set; }

    /// <summary>开始一次读取：取代次。读取落地时凭它判断期间是否有更权威的变更。</summary>
    public int BeginRead() => ++_readGeneration;

    /// <summary>读取落地：代次不符（期间发生过 ReceiveChange/InvalidateReads）则丢弃，不覆盖较新变更。</summary>
    public bool AcceptRead(int generation, ThemeKind preference)
    {
        if (generation != _readGeneration)
        {
            return false;
        }
        Preference = preference;
        return true;
    }

    /// <summary>收到权威变更（用户手选/落盘回读）：偏好直接更新，所有在途读取作废。</summary>
    public void ReceiveChange(ThemeKind preference)
    {
        _readGeneration++;
        Preference = preference;
    }

    /// <summary>手动作废在途读取（设置重载等场景）。</summary>
    public void InvalidateReads() => _readGeneration++;

    /// <summary>
    /// 三态循环切换（亮→暗→系统→亮）：pending 期间拒绝（返回 null），否则登记 pending 并返回下一态。
    /// 成败都须以 FinishToggle 收尾；失败后调用方保留原偏好即可重试。
    /// </summary>
    public ThemeKind? BeginToggle()
    {
        if (_togglePending || Preference is not { } current)
        {
            return null;
        }
        _togglePending = true;
        return current.Next();
    }

    /// <summary>切换落地（无论成败）：释放 pending 门。</summary>
    public void FinishToggle() => _togglePending = false;

    public Snapshot Current => new(Preference, _readGeneration);
}
