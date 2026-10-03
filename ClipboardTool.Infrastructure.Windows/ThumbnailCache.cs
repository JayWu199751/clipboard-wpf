using System.Windows.Media.Imaging;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>缩略图解码结果：Freeze 过的位图 + 解码像素估算（宽×高×4 字节）。</summary>
public sealed record ThumbnailDecodeResult(BitmapSource Bitmap, int PixelBytes);

/// <summary>解码工厂：path 的 PNG 解到目标物理像素宽（保比例）；失败返回 null。</summary>
public delegate ThumbnailDecodeResult? ThumbnailDecoder(string path, int pixelWidth);

/// <summary>
/// 有界缩略图缓存（T06）：按条目 Id 记忆化（P4 结论：容器 Recycling 换绑后绑定重取走缓存），
/// 双上限 LRU——至多 32 张且解码像素估算 ≤ 24 MiB，超限淘汰最久未使用的条目。
/// 失效规则：Remove/Clear 立即作废缓存项**与在途解码结果**（迟到结果不落缓存、不回调），
/// 并按每 Id 代次隔离失效后的新请求——旧代次结果绝不可能渲染到新代次的卡片上（错图防护）。
/// 解码经注入队列在后台执行（生产 = Task.Run），结果必须 Freeze 后才可跨线程交给 UI；
/// 回调经 marshal 归队（生产 = Dispatcher.BeginInvoke），回调携带 id 供容器校验（Recycling 防错图）。
/// 线程模型：LRU 表与在途表全部锁内维护；解码与回调在锁外执行。
/// </summary>
public sealed class ThumbnailCache
{
    public const int MaxCount = 32;
    public const long MaxPixelBytes = 24 * 1024 * 1024;

    private readonly ThumbnailDecoder _decode;
    private readonly Action<Action> _decodeQueue;
    private readonly Action<Action> _marshal;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CachedItem>> _items = [];
    private readonly LinkedList<CachedItem> _lru = new(); // 头 = 最近使用
    private readonly Dictionary<string, long> _generation = new();
    private long _totalPixelBytes;
    private long _useCounter;

    private sealed record CachedItem(string Id, BitmapSource Bitmap, int PixelBytes);

    public ThumbnailCache(
        ThumbnailDecoder decode,
        Action<Action>? decodeQueue = null,
        Action<Action>? marshal = null)
    {
        _decode = decode;
        _decodeQueue = decodeQueue ?? (work => Task.Run(work));
        _marshal = marshal ?? (action => action());
    }

    /// <summary>当前缓存条数（测试与诊断观察口）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>命中取图（LRU 触碰）；未命中返回 false。</summary>
    public bool TryGet(string id, out BitmapSource? bitmap)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
            bitmap = null;
            return false;
        }
    }

    /// <summary>
    /// 取缩略图：命中同步可得（TryGet 语义内联在缓存内部判断）；未命中且无在途则排队解码，
    /// 完成后落缓存并回调 onLoaded(id, bitmap)（null = 解码失败，不缓存）。
    /// 回调归队执行；失效（Remove/Clear）后完成的结果被丢弃。
    /// </summary>
    public void GetOrLoad(string id, string path, int pixelWidth, Action<string, BitmapSource?> onLoaded)
    {
        long generation;
        lock (_gate)
        {
            // TryGetValue 一次查找：命中即触碰 LRU 并取图（避免 ContainsKey/TryGet/索引器三次查找）
            if (_items.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                var hit = node.Value.Bitmap;
                _marshal(() => onLoaded(id, hit));
                return;
            }
            generation = ++_useCounter;
            _generation[id] = generation;
        }

        _decodeQueue(() =>
        {
            var result = _decode(path, pixelWidth);
            bool valid;
            lock (_gate)
            {
                valid = _generation.TryGetValue(id, out var g) && g == generation;
                if (valid && result is not null)
                {
                    StoreLocked(id, result.Bitmap, result.PixelBytes);
                }
            }
            if (valid)
            {
                _marshal(() => onLoaded(id, result?.Bitmap));
            }
            // invalid = 期间被失效/被新代次取代：取消旧请求——不落缓存、不回调
        });
    }

    /// <summary>失效单条（移除/裁剪联动）：缓存项作废，在途结果一并取消。</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(id, out var node))
            {
                _totalPixelBytes -= node.Value.PixelBytes;
                _lru.Remove(node);
                _items.Remove(id);
            }
            _generation.Remove(id); // 代次作废 → 在途结果完成时被丢弃
        }
    }

    /// <summary>全部失效（清空历史/停靠回收不可见项）：含在途。宿主的重置/预热由
    /// Presentation 在停靠/呼出时对已实现容器显式执行（停靠清缓存才能真正释放内存）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _lru.Clear();
            _generation.Clear();
            _totalPixelBytes = 0;
        }
    }

    /// <summary>落缓存并执行双上限淘汰（调用方持锁）。新条目自身不被淘汰。</summary>
    private void StoreLocked(string id, BitmapSource bitmap, int pixelBytes)
    {
        var item = new CachedItem(id, bitmap, pixelBytes);
        var node = new LinkedListNode<CachedItem>(item);
        _lru.AddFirst(node);
        _items[id] = node;
        _totalPixelBytes += pixelBytes;

        while (_items.Count > 1 && (_items.Count > MaxCount || _totalPixelBytes > MaxPixelBytes))
        {
            var oldest = _lru.Last!;
            _lru.RemoveLast();
            _items.Remove(oldest.Value.Id);
            _totalPixelBytes -= oldest.Value.PixelBytes;
        }
    }
}

/// <summary>
/// 缩略图生产解码器：BitmapImage + DecodePixelWidth 在解码时直接缩到目标物理像素
/// （全尺寸位图只在 WIC 内部瞬时存在，不常驻托管内存——工单禁止常驻全尺寸 Bitmap/base64）；
/// OnLoad 立即读完流不锁文件；Freeze 后跨线程可安全交给 UI（线程防线）。
/// </summary>
public static class WpfThumbnailDecoder
{
    public static ThumbnailDecodeResult? Decode(string path, int pixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Max(1, pixelWidth);
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return new ThumbnailDecodeResult(
                bitmap,
                bitmap.PixelWidth * bitmap.PixelHeight * 4);
        }
        catch (Exception)
        {
            return null; // 文件丢失/坏图 → 占位
        }
    }
}
