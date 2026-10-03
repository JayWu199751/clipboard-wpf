using ClipboardTool.Application;
using ClipboardTool.Domain.Settings;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// 主题读取代次与切换互斥（F26–F28；legacy themeSync.ts ThemeSynchronizer 移植）：
/// 广播触发的重新读取是异步的，期间用户可能已手选另一态——迟到的读取不覆盖较新变更（代次）；
/// 三态循环切换进行中（pending）不接受新切换，失败后释放可重试。
/// </summary>
public class ThemeSyncTests
{
    [Fact]
    public void 初始无偏好_循环切换拒绝()
    {
        var sync = new ThemeSync();
        Assert.Null(sync.BeginToggle());
    }

    [Fact]
    public void 读取成功写入偏好()
    {
        var sync = new ThemeSync();
        var gen = sync.BeginRead();
        Assert.True(sync.AcceptRead(gen, ThemeKind.Dark));
        Assert.Equal(ThemeKind.Dark, sync.Current.Preference);
    }

    [Fact]
    public void 迟到的读取不覆盖较新变更()
    {
        var sync = new ThemeSync();
        var gen = sync.BeginRead();               // 广播触发的读取在途
        sync.ReceiveChange(ThemeKind.Light);      // 期间用户手选（或另一次广播先到）
        Assert.False(sync.AcceptRead(gen, ThemeKind.Dark), "迟到的读取必须被代次拦下");
        Assert.Equal(ThemeKind.Light, sync.Current.Preference);
    }

    [Fact]
    public void 收到变更使在途读取失效并直接更新()
    {
        var sync = new ThemeSync();
        var gen = sync.BeginRead();
        sync.ReceiveChange(ThemeKind.Dark);
        Assert.False(sync.AcceptRead(gen, ThemeKind.Light), "变更落地后在途读取应作废");
        Assert.Equal(ThemeKind.Dark, sync.Current.Preference);
    }

    [Fact]
    public void 手动失效使在途读取作废()
    {
        var sync = new ThemeSync();
        var gen = sync.BeginRead();
        sync.InvalidateReads();
        Assert.False(sync.AcceptRead(gen, ThemeKind.Light));
    }

    [Fact]
    public void 三态循环_亮到暗到系统到亮()
    {
        var sync = new ThemeSync();
        sync.ReceiveChange(ThemeKind.Light);
        Assert.Equal(ThemeKind.Dark, sync.BeginToggle());
        sync.FinishToggle();
        sync.ReceiveChange(ThemeKind.Dark);
        Assert.Equal(ThemeKind.System, sync.BeginToggle());
        sync.FinishToggle();
        sync.ReceiveChange(ThemeKind.System);
        Assert.Equal(ThemeKind.Light, sync.BeginToggle());
        sync.FinishToggle();
    }

    [Fact]
    public void 切换进行中再切换拒绝_pending_释放后恢复()
    {
        var sync = new ThemeSync();
        sync.ReceiveChange(ThemeKind.Light);
        Assert.NotNull(sync.BeginToggle());
        Assert.Null(sync.BeginToggle()); // pending 门：上一次切换还没落地
        sync.FinishToggle();
        Assert.Equal(ThemeKind.Dark, sync.BeginToggle()); // 释放后可重试
    }
}
