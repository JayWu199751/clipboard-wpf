using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;

namespace P4VirtualizationProbe;

/// <summary>
/// 探针卡片：文字卡 1–3 行（变高）、图片卡 = 150 DIP 缩略图 + 三行文字 + meta。
/// 缩略图懒加载 + 按 Id 记忆化（memoization）：解码计数在工厂层统计，
/// 「无重复解码风暴」的断言对象就是这里的计数器。
/// </summary>
public sealed class ProbeCard
{
    private static int _decodeRequests;
    private static int _decodeCacheHits;
    private static long _decodeTotalMs;

    /// <summary>真实解码请求次数（未命中缓存时的 WIC 解码调用）。</summary>
    public static int DecodeRequests => Volatile.Read(ref _decodeRequests);

    /// <summary>缓存命中次数（复用已解码缩略图，未触发解码）。</summary>
    public static int DecodeCacheHits => Volatile.Read(ref _decodeCacheHits);

    /// <summary>全部解码调用的累计毫秒（解码成本佐证）。</summary>
    public static long DecodeTotalMs => Volatile.Read(ref _decodeTotalMs);

    public string Id { get; }
    public string Body { get; }
    public string Meta { get; }
    public bool IsImage { get; }
    public BitmapImage? Thumb => IsImage ? DecodeThumb(Id) : null;

    private ProbeCard(int index, bool isImage)
    {
        Id = $"card-{index:000}";
        IsImage = isImage;
        Body = isImage ? Bodies.ImageBody(index) : Bodies.TextBody(index);
        Meta = $"{(index % 12) + 1:D2}-{(index % 28) + 1:D2} {(index % 24):D2}:{(index * 7 % 60):D2}" + (isImage ? " · 截图" : string.Empty);
    }

    /// <summary>构造 200 条混合卡：约 30% 图片卡（4K 图占位），其余文字卡 1–3 行。</summary>
    public static IReadOnlyList<ProbeCard> CreateMixed(int total = 200)
    {
        var cards = new ProbeCard[total];
        for (var i = 0; i < total; i++)
        {
            cards[i] = new ProbeCard(i, isImage: i % 3 == 1); // 67 张图片卡
        }
        return cards;
    }

    private static readonly Dictionary<string, BitmapImage> ThumbCache = [];

    /// <summary>
    /// 缩略图解码（记忆化）：源为程序生成的 4K BMP（3840×2160 24bpp，约 24.9 MB），
    /// WIC 全量解码后按 DecodePixelWidth=320 缩样——每次未命中缓存的真实解码都有可观成本，
    /// 解码风暴（逐帧重复解码）会立刻反映在计数与耗时上。
    /// </summary>
    private static BitmapImage DecodeThumb(string id)
    {
        lock (ThumbCache)
        {
            if (ThumbCache.TryGetValue(id, out var cached))
            {
                Interlocked.Increment(ref _decodeCacheHits);
                return cached;
            }
        }

        Interlocked.Increment(ref _decodeRequests);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var image = new BitmapImage();
        using var stream = new MemoryStream(FourKBmp.Source);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 320;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        Interlocked.Add(ref _decodeTotalMs, sw.ElapsedMilliseconds);

        lock (ThumbCache)
        {
            ThumbCache[id] = image;
        }
        return image;
    }
}

/// <summary>卡片正文生成：文字卡 1–3 行（行宽 ~60 汉字），图片卡固定三行。</summary>
public static class Bodies
{
    private static string Line(int seed, int len) =>
        string.Concat(Enumerable.Range(0, len).Select(j => Content[(seed + j * 7) % Content.Length]));

    private const string Content = "剪贴板历史条目正文样本文字混合滚动虚拟化试验变高卡片缩略图解码计数回收复用容器测量位置漂移选中可见性导航留白页脚分隔线";

    public static string TextBody(int index)
    {
        var lines = index % 3 + 1; // 1–3 行：变高来源之一
        return string.Join('\n', Enumerable.Range(0, lines).Select(l => Line(index * 13 + l, 30 + l * 15)));
    }

    public static string ImageBody(int index) =>
        string.Join('\n', Enumerable.Range(0, 3).Select(l => Line(index * 29 + l, 40)));
}

/// <summary>程序生成 4K BMP（3840×2160 24bpp）：无压缩、无内嵌缩略图，WIC 需全量解码，模拟真实大图成本。</summary>
public static class FourKBmp
{
    public static byte[] Source { get; } = Build();

    private static byte[] Build()
    {
        const int w = 3840, h = 2160, stride = w * 3;
        var pixelSize = stride * h;
        var data = new byte[54 + pixelSize];

        // BITMAPFILEHEADER（14 B）
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), 54);
        // BITMAPINFOHEADER（40 B）：bottom-up 24bpp
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), h);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34), (uint)pixelSize);

        // 渐变填充（x,y 混合灰度 + 伪噪声）：8.3M 像素单线程 ~百 ms，一次性成本
        for (var y = 0; y < h; y++)
        {
            var row = 54 + (long)(h - 1 - y) * stride;
            for (var x = 0; x < w; x++)
            {
                var v = (byte)((x + y * 2 + ((x * y) >> 9)) & 0xFF);
                data[row + x * 3] = v;
                data[row + x * 3 + 1] = (byte)(v * 3 / 4);
                data[row + x * 3 + 2] = (byte)(255 - v);
            }
        }
        return data;
    }
}
