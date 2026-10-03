using System.IO;
using System.Windows.Media.Imaging;
using ClipboardTool.Infrastructure.Windows;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 有界缩略图缓存（T06，工单红绿：双上限、失效、并发取消、错图防护）。
/// 解码工厂与执行队列注入：测试内同步执行，完全确定性；
/// LRU 按「最近使用」单调序，双上限 = 至多 32 张 且 解码像素估算 ≤ 24 MiB。
/// </summary>
public sealed class ThumbnailCacheTests
{
    /// <summary>测试用解码工厂：记录调用；未注册像素估算的 Id 默认 16 字节、默认 2×2 位图。</summary>
    private (ThumbnailCache Cache, List<string> Decoded, Dictionary<string, int> PixelBytes, Dictionary<string, BitmapSource> Bitmaps)
        MakeCache(Dictionary<string, int>? pixelBytes = null)
    {
        var decoded = new List<string>();
        pixelBytes ??= [];
        var cache = new ThumbnailCache(
            (path, _) =>
            {
                decoded.Add(path);
                var id = Path.GetFileNameWithoutExtension(path);
                pixelBytes.TryGetValue(id, out var bytes);
                return new ThumbnailDecodeResult(Bmp(), bytes);
            },
            work => work()); // 同步队列：测试确定性
        return (cache, decoded, pixelBytes, new Dictionary<string, BitmapSource>());
    }

    private static BitmapSource Bmp(int w = 2, int h = 2)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[w * h * 4], w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static string PathOf(string id) => $@"C:\images\{id}.png";

    [Fact]
    public void 同Id重复请求_解码一次_命中走缓存()
    {
        var (cache, decoded, _, _) = MakeCache();
        BitmapSource? got = null;
        cache.GetOrLoad("a", PathOf("a"), 100, (_, b) => got = b);

        Assert.NotNull(got);
        Assert.True(cache.TryGet("a", out var hit));
        Assert.Same(got, hit);

        // 容器复用换绑再绑回（P4 结论：按 Id 记忆化）→ 不再解码
        cache.GetOrLoad("a", PathOf("a"), 100, (_, _) => { });
        Assert.Single(decoded);
    }

    [Fact]
    public void 数量上限_至多32张_LRU淘汰最旧()
    {
        var (cache, _, _, _) = MakeCache();
        for (var i = 0; i < 40; i++)
        {
            cache.GetOrLoad($"id{i}", PathOf($"id{i}"), 100, (_, _) => { });
        }

        Assert.Equal(32, cache.Count);
        Assert.False(cache.TryGet("id0", out _)); // 最旧被淘汰
        Assert.False(cache.TryGet("id7", out _));
        Assert.True(cache.TryGet("id8", out _));
        Assert.True(cache.TryGet("id39", out _));
    }

    [Fact]
    public void 像素上限_总量不超24MiB_超限淘汰最旧()
    {
        // 每张 5 MiB：第 5 张使总量 25 MiB 超 24 → 淘汰到 4 张（数量上限同样满足）
        var pixelBytes = Enumerable.Range(0, 5).ToDictionary(i => $"big{i}", _ => 5 * 1024 * 1024);
        var (cache, _, _, _) = MakeCache(pixelBytes);
        for (var i = 0; i < 5; i++)
        {
            cache.GetOrLoad($"big{i}", PathOf($"big{i}"), 100, (_, _) => { });
        }

        Assert.Equal(4, cache.Count);
        Assert.False(cache.TryGet("big0", out _));
        Assert.True(cache.TryGet("big4", out _));
    }

    [Fact]
    public void LRU次序_被使用的旧项不被淘汰()
    {
        var (cache, _, _, _) = MakeCache();
        for (var i = 0; i < 32; i++)
        {
            cache.GetOrLoad($"id{i}", PathOf($"id{i}"), 100, (_, _) => { });
        }
        Assert.True(cache.TryGet("id0", out _)); // 触碰最旧 → 升为最近使用

        cache.GetOrLoad("id-new", PathOf("id-new"), 100, (_, _) => { }); // 挤掉次旧

        Assert.True(cache.TryGet("id0", out _));
        Assert.False(cache.TryGet("id1", out _));
    }

    [Fact]
    public void 移除失效_立即再解码()
    {
        var (cache, decoded, _, _) = MakeCache();
        cache.GetOrLoad("a", PathOf("a"), 100, (_, _) => { });
        cache.Remove("a");

        Assert.False(cache.TryGet("a", out _));

        cache.GetOrLoad("a", PathOf("a"), 100, (_, _) => { });
        Assert.Equal(2, decoded.Count); // 失效后重新解码
    }

    [Fact]
    public void 并发取消_在途请求被失效后_结果不落缓存不回调()
    {
        // decodeQueue 暂存工作不执行：模拟解码在后台进行中
        Action? pending = null;
        var cache = new ThumbnailCache(
            (_, _) => new ThumbnailDecodeResult(Bmp(), 16),
            work => pending = work);
        var calls = 0;
        cache.GetOrLoad("a", PathOf("a"), 100, (_, _) => calls++);

        cache.Remove("a");  // 条目删除/裁剪/清空先于解码完成到达
        pending!.Invoke();  // 迟到的解码结果

        Assert.Equal(0, calls);           // 不回调（防错图）
        Assert.False(cache.TryGet("a", out _)); // 不落缓存
    }

    [Fact]
    public void 错图防护_同Id先后两次请求_只认最新代次()
    {
        // 代次场景：A 失效 → 同 Id 重新入史 → 新请求；旧代次的迟到结果必须作废
        Action? pendingOld = null;
        Action? pendingNew = null;
        var queue = new List<Action>();
        var cache = new ThumbnailCache(
            (_, _) => new ThumbnailDecodeResult(Bmp(), 16),
            work => queue.Add(work));

        var oldCalls = 0;
        cache.GetOrLoad("a", PathOf("a"), 100, (_, _) => oldCalls++);
        pendingOld = queue[^1];
        cache.Remove("a");                                  // 失效（旧代次作废）
        cache.GetOrLoad("a", PathOf("a"), 100, (_, _) => { }); // 新代次请求
        pendingNew = queue[^1];

        pendingOld!.Invoke(); // 旧代次迟到
        Assert.Equal(0, oldCalls);

        pendingNew!.Invoke(); // 新代次完成
        Assert.True(cache.TryGet("a", out var fresh));
        Assert.NotNull(fresh);
    }

    [Fact]
    public void 清空_全部失效含在途()
    {
        Action? pending = null;
        var cache = new ThumbnailCache(
            (_, _) => new ThumbnailDecodeResult(Bmp(), 16),
            work => pending = work);
        var calls = 0;
        cache.GetOrLoad("a", PathOf("a"), 100, (_, _) => calls++);
        cache.GetOrLoad("b", PathOf("b"), 100, (_, _) => { });

        cache.Clear();
        pending!.Invoke();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, calls);
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void 解码失败_不缓存不误用占位()
    {
        var cache = new ThumbnailCache((_, _) => null, work => work());
        BitmapSource? got = null;
        cache.GetOrLoad("bad", PathOf("bad"), 100, (_, b) => got = b);

        Assert.Null(got); // 回调带 null → 卡片显示占位
        Assert.False(cache.TryGet("bad", out _));
    }

    [Fact]
    public async Task 真解码器_按目标物理像素解码并Freeze()
    {
        // 生产实现（跨线程防线）：后台解码 → Freeze → 任意线程可用；
        // 解码到缩略图物理像素（DecodePixelWidth 保比例），不碰全尺寸位图。
        var dir = Path.Combine(Path.GetTempPath(), "cbt-thumbtests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "sample.png");
            var rgba = new byte[64 * 32 * 4];
            for (var i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = 0x10;
                rgba[i + 1] = 0x20;
                rgba[i + 2] = 0x30;
                rgba[i + 3] = 0x80;
            }
            await File.WriteAllBytesAsync(path, PngEncoder.Encode(64, 32, rgba));

            var result = await Task.Run(() => WpfThumbnailDecoder.Decode(path, pixelWidth: 16));

            Assert.NotNull(result);
            Assert.Equal(16, result!.Bitmap.PixelWidth);
            Assert.Equal(8, result.Bitmap.PixelHeight); // 保比例
            Assert.True(result.Bitmap.IsFrozen);
            Assert.Equal(16 * 8 * 4, result.PixelBytes);
            Assert.Null(WpfThumbnailDecoder.Decode(Path.Combine(dir, "missing.png"), 16));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
