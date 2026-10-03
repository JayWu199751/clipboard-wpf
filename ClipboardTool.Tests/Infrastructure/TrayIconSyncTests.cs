using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 托盘图标同步状态机（F29；legacy tray.rs sync_icon 的去重与 HICON 生命周期）：
/// 同键不重设（display-metrics 高频触发也不动）；换图先销毁旧 HICON 再换新（句柄不泄漏）；
/// 工厂加载失败不记账（保持旧图）。
/// </summary>
public class TrayIconSyncTests
{
    private sealed class FakeFactory : TrayIconSync.ITrayIconFactory
    {
        public bool Fail { get; set; }
        public List<(bool Dark, int Size)> Loads { get; } = [];
        public List<IntPtr> Destroys { get; } = [];
        private int _next = 1;

        public IntPtr? Load(bool dark, int size)
        {
            if (Fail)
            {
                return null;
            }
            Loads.Add((dark, size));
            return new IntPtr(_next++);
        }

        public void Destroy(IntPtr hicon) => Destroys.Add(hicon);
    }

    [Fact]
    public void 首次同步加载图标()
    {
        var factory = new FakeFactory();
        var sync = new TrayIconSync(factory);

        var hicon = sync.Sync(dark: false, scale: 1.5);

        Assert.Equal([(false, 24)], factory.Loads);
        Assert.Equal(new IntPtr(1), hicon);
        Assert.Empty(factory.Destroys);
    }

    [Fact]
    public void 同键不重设()
    {
        var factory = new FakeFactory();
        var sync = new TrayIconSync(factory);
        sync.Sync(dark: false, scale: 1.0);
        sync.Sync(dark: false, scale: 1.1); // 1.0 与 1.1 同落 16 档

        Assert.Single(factory.Loads);
    }

    [Fact]
    public void 换档先销毁旧句柄再加载新图()
    {
        var factory = new FakeFactory();
        var sync = new TrayIconSync(factory);
        var first = sync.Sync(dark: false, scale: 1.0);

        var second = sync.Sync(dark: false, scale: 1.5);

        Assert.Equal([(false, 16), (false, 24)], factory.Loads);
        Assert.Equal([first], factory.Destroys); // 旧 HICON 必须销毁，否则换一次档漏一个句柄
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void 换主题换图_销毁旧句柄()
    {
        var factory = new FakeFactory();
        var sync = new TrayIconSync(factory);
        var first = sync.Sync(dark: false, scale: 1.75);

        sync.Sync(dark: true, scale: 1.75);

        Assert.Equal([(false, 28), (true, 28)], factory.Loads);
        Assert.Equal([first], factory.Destroys);
    }

    [Fact]
    public void 加载失败保持旧图_不销毁不记账()
    {
        var factory = new FakeFactory();
        var sync = new TrayIconSync(factory);
        var first = sync.Sync(dark: false, scale: 1.0);
        factory.Fail = true;

        var retry = sync.Sync(dark: true, scale: 1.0);

        Assert.True(first == retry, "加载失败时保持旧图");
        Assert.True(factory.Destroys.Count == 0, "旧句柄不能被销毁");
        Assert.Single(factory.Loads); // Loads 只记成功加载：失败那次没产出图标
        factory.Fail = false;
        var recovered = sync.Sync(dark: true, scale: 1.0); // 失败未记账：下次同键仍会重试
        Assert.Equal(2, factory.Loads.Count);
        Assert.Equal([first], factory.Destroys);
        Assert.NotEqual(first, recovered);
    }
}
