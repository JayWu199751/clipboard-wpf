using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClipboardTool.Domain.History;
using Xunit;

namespace ClipboardTool.Tests.HistoryRules;

/// <summary>图片文件端口假件：写盘恒成功（路径 /images/{id}.png）、哈希恒空（图片去重因此默认不命中）、
/// 文件恒存在、删文件记进日志——与 legacy history.rs 测试的 Ports::fake 同形状。
/// HashPng 缺省按字节内容 SHA-1（模拟「解码 PNG → 规范化身份」的确定性映射，
/// 让按字节相同的去重用例照常工作；规范化等价本身由参照 PNG 测试与 ImageFileStore 测试钉住）。</summary>
public sealed class FakeImageFiles : IImageFileStore
{
    public List<string> Removed { get; } = [];
    public int SaveCalls { get; private set; }
    public Func<byte[], string, string?>? SaveOverride { get; set; }
    public Func<string, string>? HashOverride { get; set; }
    public Func<byte[], string>? HashPngOverride { get; set; }
    public Func<string, bool>? ExistsOverride { get; set; }

    public string? SavePng(byte[] png, string id)
    {
        SaveCalls++;
        // 注意：覆盖件返回 null（写盘失败）必须原样传出，不能被 ?? 兜底吞掉
        return SaveOverride is not null ? SaveOverride(png, id) : $"/images/{id}.png";
    }

    public string HashFile(string path) => HashOverride?.Invoke(path) ?? string.Empty;

    public string HashPng(byte[] png) =>
        HashPngOverride?.Invoke(png) ?? HistoryStoreFullRulesTests.ImageSha1Hex(png);

    public void RemoveFile(string path) => Removed.Add(path);

    public bool FileExists(string path) => ExistsOverride?.Invoke(path) ?? true;
}

/// <summary>
/// 历史存储完整规则测试（F03/F04/F05/F07；legacy history.rs 测试语义逐条移植）：
/// 条目身份、置顶块/普通块落位、promote、上限裁剪、备注归一化、载入过滤。
/// </summary>
public sealed class HistoryStoreFullRulesTests
{
    private static HistoryStore CreateStore(
        int? max = null,
        FakeImageFiles? images = null,
        Func<long>? nowMs = null)
    {
        images ??= new FakeImageFiles();
        return new HistoryStore(
            max ?? HistoryStore.DefaultMaxHistory,
            images,
            newId: () => Guid.NewGuid().ToString("N"),
            nowMs: nowMs ?? (() => 1_700_000_000_000));
    }

    /// <summary>时间步进时钟：每次 now 前进 10ms，让 pinnedAt/createdAt 严格可辨。</summary>
    private static Func<long> SteppingClock()
    {
        var t = 1000L;
        return () => t += 10;
    }

    /// <summary>把 JSON 数组文本解析为元素列表（走 HistoryArchive.TryParseArray 公共路径）。</summary>
    private static IReadOnlyList<JsonNode?> RawArray(string json) =>
        HistoryArchive.TryParseArray(json) ?? throw new InvalidOperationException("测试 JSON 应为合法数组");

    private static string Ids(HistoryStore store) => string.Join(",", store.Entries.Select(e => e.Id));

    private static string Texts(HistoryStore store) =>
        string.Join(",", store.Entries.Select(e => e.Type == EntryKind.Text ? e.Text : "<img>"));

    [Fact]
    public void 新文本条目插到最前()
    {
        var store = CreateStore();
        store.RecordText("a");
        store.RecordText("b");
        Assert.Equal("b,a", Texts(store));
    }

    [Fact]
    public void 同文本去重_提升已有条目且属性不变_不新建()
    {
        var store = CreateStore();
        var first = store.RecordText("hello", new SourceApp(AppName: "notes")).Entry!;
        Assert.True(store.SetNote(first.Id, "备注1"));
        store.RecordText("world");
        store.RecordText("other");

        var again = store.RecordText("hello");
        Assert.True(again.Deduped);
        Assert.Equal(first.Id, again.Entry!.Id);
        Assert.Equal(3, store.Count);
        Assert.Equal("hello,other,world", Texts(store));
        Assert.Equal("备注1", again.Entry.Note);
        Assert.Equal(first.CreatedAtMs, again.Entry.CreatedAtMs);
        Assert.Equal("notes", again.Entry.SourceApp!.AppName);
    }

    [Fact]
    public void 新内容插在置顶块之后_普通块最前()
    {
        var store = CreateStore();
        var a = store.RecordText("a").Entry!;
        store.RecordText("b");
        Assert.True(store.TogglePin(a.Id));
        store.RecordText("c");
        Assert.Equal("a,c,b", Texts(store));
        Assert.True(store.Entries[0].Pinned);
    }

    [Fact]
    public void 置顶条目去重命中_刷新pinnedAt并移到置顶块最前()
    {
        var store = CreateStore(nowMs: SteppingClock());
        var a = store.RecordText("a").Entry!;
        var b = store.RecordText("b").Entry!;
        Assert.True(store.TogglePin(a.Id));
        Assert.True(store.TogglePin(b.Id));
        Assert.Equal("b,a", Texts(store));

        var pinnedAtBefore = store.Find(a.Id)!.PinnedAtMs;
        var outcome = store.RecordText("a");
        Assert.True(outcome.Deduped);
        Assert.Equal("a", store.Entries[0].Text);
        Assert.True(store.Find(a.Id)!.PinnedAtMs > pinnedAtBefore, "置顶条目被再次使用时 pinnedAt 必须刷新");
    }

    [Fact]
    public void togglePin_置顶刷新pinnedAt移到块首_取消置顶回到普通块最前且保留pinnedAt()
    {
        var store = CreateStore(nowMs: SteppingClock());
        var a = store.RecordText("a").Entry!;
        store.RecordText("b");
        store.RecordText("c");

        Assert.True(store.TogglePin(a.Id));
        Assert.Equal("a,c,b", Texts(store));
        var pinned = store.Find(a.Id)!;
        Assert.True(pinned.Pinned);
        var pinnedAt = pinned.PinnedAtMs;
        Assert.True(pinnedAt > 0);

        Assert.True(store.TogglePin(a.Id));
        var unpinned = store.Find(a.Id)!;
        Assert.False(unpinned.Pinned);
        Assert.Equal(pinnedAt, unpinned.PinnedAtMs); // 取消置顶保留 pinnedAt（存档契约）
        Assert.Equal("a,c,b", Texts(store));
        Assert.False(store.TogglePin("missing"));
    }

    [Fact]
    public void 裁剪_置顶豁免_先裁普通块尾部()
    {
        var store = CreateStore(max: 3);
        store.RecordText("a");
        store.RecordText("b");
        store.RecordText("c");
        store.RecordText("d"); // 裁掉普通块最旧 a → [d,c,b]
        Assert.Equal("d,c,b", Texts(store));

        var frontId = store.Entries[0].Id;
        Assert.True(store.TogglePin(frontId)); // 置顶 d → 置顶块 [d]
        store.RecordText("e"); // [d,e,c,b] → 裁普通尾部 b → [d,e,c]
        Assert.Equal("d,e,c", Texts(store));
    }

    [Fact]
    public void 全部置顶时裁最旧置顶_经load触发()
    {
        var store = CreateStore(max: 3);
        store.Load(RawArray("""
            [
              { "id": "1", "type": "text", "text": "t1", "pinned": true, "pinnedAt": 100 },
              { "id": "2", "type": "text", "text": "t2", "pinned": true, "pinnedAt": 400 },
              { "id": "3", "type": "text", "text": "t3", "pinned": true, "pinnedAt": 300 },
              { "id": "4", "type": "text", "text": "t4", "pinned": true, "pinnedAt": 200 }
            ]
            """));
        // 排序 [2,3,4,1] → 裁最旧置顶 1
        Assert.Equal("2,3,4", Ids(store));
    }

    [Fact]
    public void remove图片条目_删内存加删文件_clear清全部()
    {
        var images = new FakeImageFiles();
        var store = CreateStore(images: images);
        var img = store.RecordImage("png1"u8.ToArray()).Entry!;
        store.RecordText("t");
        var imgPath = img.ImagePath!;
        Assert.Equal(2, store.Count);

        Assert.True(store.Remove(img.Id));
        Assert.Equal([imgPath], images.Removed);
        Assert.False(store.Remove(img.Id));
        store.Clear();
        Assert.Equal([imgPath], images.Removed);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void recordImage身份按PNGsha1_命中提升_不写新文件()
    {
        var images = new FakeImageFiles();
        var store = CreateStore(images: images);
        var first = store.RecordImage("same-png"u8.ToArray()).Entry!;
        var again = store.RecordImage("same-png"u8.ToArray());
        Assert.True(again.Deduped);
        Assert.Equal(first.Id, again.Entry!.Id);
        Assert.Equal(1, images.SaveCalls); // 重复命中不重写文件
        Assert.Equal(1, store.Count);
        Assert.Equal(ImageSha1Hex("same-png"u8.ToArray()), again.Hash);
    }

    [Fact]
    public void recordImage写盘失败_不插入条目()
    {
        var images = new FakeImageFiles { SaveOverride = (_, _) => null };
        var store = CreateStore(images: images);
        var outcome = store.RecordImage("x"u8.ToArray());
        Assert.Null(outcome.Entry);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void recordImage同图跨格式命中_沿用原id不重写文件()
    {
        // ADR-0005 第 4 条：身份 = PNG 解码 → 规范化 RGBA → SHA-1。同一像素的
        // 旧版（image crate）编码与新编码字节不同，身份必须相同、命中沿用原 id 不重写文件。
        // 假件以「同像素 → 同哈希」模拟解码规范化；规范化等价本身由参照 PNG 测试与
        // ImageFileStore 测试钉住。
        var images = new FakeImageFiles
        {
            HashPngOverride = _ => "pixhash-A",   // 两种编码解出同一像素
            HashOverride = _ => "pixhash-A",      // 磁盘文件同样解出该像素
        };
        var store = CreateStore(images: images);
        var first = store.RecordImage("legacy-image-crate-png"u8.ToArray()).Entry!;

        var again = store.RecordImage("wic-png"u8.ToArray()); // 同图、编码字节不同

        Assert.True(again.Deduped);
        Assert.Equal(first.Id, again.Entry!.Id);
        Assert.Equal(1, images.SaveCalls); // 命中不重写文件
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 历史图片按文件哈希去重_命中不重写文件()
    {
        var hex = ImageSha1Hex("same-png"u8.ToArray());
        var images = new FakeImageFiles { HashOverride = _ => hex };
        var store = CreateStore(images: images);
        store.Load(RawArray("""[{"id":"1","type":"image","imagePath":"/images/1.png"}]"""));

        var outcome = store.RecordImage("same-png"u8.ToArray());
        Assert.True(outcome.Deduped);
        Assert.Equal("1", outcome.Entry!.Id);
        Assert.Equal(0, images.SaveCalls);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 图片哈希匹配_空哈希不命中_用注入的哈希识别历史图片()
    {
        var images = new FakeImageFiles { HashOverride = _ => "abc123" };
        var store = CreateStore(images: images);
        store.Load(RawArray("""[{"id":"1","type":"image","imagePath":"/images/1.png"}]"""));

        Assert.Equal("1", store.MatchImageHash("abc123")!.Id);
        Assert.Null(store.MatchImageHash("other"));
        Assert.Null(store.MatchImageHash(""));
    }

    [Fact]
    public void 载入归一化_缺省字段_未知类型与丢文件图片被过滤()
    {
        var images = new FakeImageFiles { ExistsOverride = p => p != "/images/lost.png" };
        var store = CreateStore(images: images);
        store.Load(RawArray("""
            [
              { "id": "1", "type": "text", "text": "plain" },
              { "id": "2", "type": "image", "imagePath": "/images/ok.png", "pinned": true, "pinnedAt": 100 },
              { "id": "3", "type": "image", "imagePath": "/images/lost.png" },
              { "id": "4", "type": "text", "text": "pinned", "pinned": true, "pinnedAt": 200 },
              { "id": "5", "type": "weird" },
              null
            ]
            """));
        Assert.Equal("4,2,1", Ids(store));
        var plain = store.Find("1")!;
        Assert.False(plain.Pinned);
        Assert.Equal(0, plain.PinnedAtMs);
        Assert.Equal(string.Empty, plain.Note);
    }

    [Fact]
    public void 载入时单条字段类型坏只丢该条_其余保留()
    {
        var store = CreateStore();
        store.Load(RawArray("""
            [
              { "id": "1", "type": "text", "text": "好的" },
              { "id": "2", "type": "text", "text": "pinned写坏", "pinned": "yes" },
              { "id": "3", "type": "text", "text": "时间写坏", "createdAt": "123" },
              { "id": "4", "type": "text", "text": "note写坏", "note": 5 },
              { "id": "5", "type": "text", "text": "来源写坏", "sourceApp": { "appName": 123 } },
              { "id": "", "type": "text", "text": "空id" },
              { "id": "7", "type": "text", "text": null }
            ]
            """));
        Assert.Equal(new[] { "1" }, store.Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void 载入时置顶块按pinnedAt新旧_与载入顺序无关()
    {
        var store = CreateStore();
        store.Load(RawArray("""
            [
              { "id": "1", "type": "text", "text": "older-pin", "pinned": true, "pinnedAt": 100 },
              { "id": "2", "type": "text", "text": "newer-pin", "pinned": true, "pinnedAt": 300 },
              { "id": "3", "type": "text", "text": "mid-pin", "pinned": true, "pinnedAt": 200 }
            ]
            """));
        Assert.Equal("2,3,1", Ids(store));
    }

    [Fact]
    public void 载入普通块保持存档顺序_即最近使用序()
    {
        var store = CreateStore();
        store.Load(RawArray("""
            [
              { "id": "1", "type": "text", "text": " newest " },
              { "id": "2", "type": "text", "text": "middle" },
              { "id": "3", "type": "text", "text": "oldest", "createdAt": 1 }
            ]
            """));
        // 正文不 trim；createdAt 旧也不重排——存档顺序即最近使用序
        Assert.Equal("1,2,3", Ids(store));
        Assert.Equal(" newest ", store.Find("1")!.Text);
        Assert.Equal(1, store.Find("3")!.CreatedAtMs);
    }

    [Fact]
    public void setNote_去首尾空白_截断200_空串等同删除()
    {
        var store = CreateStore();
        var entry = store.RecordText("t").Entry!;
        Assert.True(store.SetNote(entry.Id, "  你好  "));
        Assert.Equal("你好", store.Find(entry.Id)!.Note);

        var long250 = new string('x', 250) + " ";
        Assert.True(store.SetNote(entry.Id, long250));
        Assert.Equal(new string('x', 200), store.Find(entry.Id)!.Note);

        Assert.True(store.SetNote(entry.Id, "   "));
        Assert.Equal(string.Empty, store.Find(entry.Id)!.Note);
        Assert.False(store.SetNote("missing", "n"));
    }

    [Fact]
    public void 备注截断按Unicode标量_代理对边界不产生孤立代理项()
    {
        var store = CreateStore();
        var entry = store.RecordText("t").Entry!;

        // 199 个标量 + 一个四字节 emoji = 200 标量：完整保留，UTF-16 长度 201 且无孤立代理项
        var fitsExactly = new string('a', 199) + "🦊";
        Assert.Equal(fitsExactly, store.NormalizeNote(fitsExactly));
        Assert.Equal(201, store.NormalizeNote(fitsExactly).Length);
        AssertNoLoneSurrogates(store.NormalizeNote(fitsExactly));

        // 210 个标量截到 200：恰好落在 emoji 中间按整标量舍弃，绝不出半个代理对
        var overflow = new string('a', 150) + Repeat("🦊", 60);
        var normalized = store.NormalizeNote(overflow);
        Assert.Equal(200, normalized.EnumerateRunes().Count());
        AssertNoLoneSurrogates(normalized);
        Assert.Equal(new string('a', 150) + Repeat("🦊", 50), normalized);

        // 载入侧同样归一化（F07 存储侧）
        store.Load(RawArray($$"""[{"id":"9","type":"text","text":"n","note":"  {{overflow}}  "}]"""));
        Assert.Equal(normalized, store.Find("9")!.Note);
    }

    private static string Repeat(string text, int count)
    {
        var sb = new System.Text.StringBuilder(text.Length * count);
        for (var i = 0; i < count; i++)
        {
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static void AssertNoLoneSurrogates(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]), "不得出现孤立的高代理项");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(value[i]), "不得出现孤立的低代理项");
            }
        }
    }

    [Fact]
    public void 裁剪联动删PNG()
    {
        var images = new FakeImageFiles();
        var store = CreateStore(max: 2, images: images);
        var first = store.RecordImage("png-1"u8.ToArray()).Entry!;
        store.RecordImage("png-2"u8.ToArray());
        store.RecordImage("png-3"u8.ToArray()); // 上限 2，最旧的 first 被裁且文件被删
        Assert.Equal(2, store.Count);
        Assert.Equal([first.ImagePath!], images.Removed);
    }

    [Fact]
    public void remove后同内容图片不再命中()
    {
        var images = new FakeImageFiles();
        var store = CreateStore(images: images);
        var first = store.RecordImage("same"u8.ToArray()).Entry!;
        Assert.True(store.Remove(first.Id));
        var again = store.RecordImage("same"u8.ToArray());
        Assert.False(again.Deduped);
        Assert.NotEqual(first.Id, again.Entry!.Id);
        Assert.Equal(2, images.SaveCalls);
    }

    [Fact]
    public void promote与remove找不到条目_返回false且不广播()
    {
        var store = CreateStore();
        Assert.False(store.Promote("missing"));
        Assert.False(store.Remove("missing"));
        Assert.Null(store.Find("missing"));
    }

    internal static string ImageSha1Hex(byte[] png) =>
        Convert.ToHexString(SHA1.HashData(png)).ToLowerInvariant();
}
