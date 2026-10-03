using System.Text.Json.Nodes;
using ClipboardTool.Domain.History;
using Xunit;

namespace ClipboardTool.Tests.HistoryRules;

/// <summary>
/// 旧档 JSON 往返测试（F08；02-spec/02 §1 存档契约）：
/// JSON 数组、camelCase 键、text/imagePath 互斥投影、单条宽松解析。
/// </summary>
public sealed class HistoryArchiveTests
{
    private static HistoryEntry ReadFirstEntry(string json)
    {
        var array = HistoryArchive.TryParseArray(json);
        Assert.NotNull(array);
        return HistoryArchive.TryReadEntry(array[0])!;
    }

    [Fact]
    public void 解析样例条目_字段全保留_时间保持毫秒()
    {
        var entry = ReadFirstEntry("""
            [
              {
                "id": "e2c9ad03-84bf-47b1-911a-0542b0f3a301",
                "type": "text",
                "text": "完整正文\n保留换行与空格",
                "createdAt": 1790992800000,
                "sourceApp": {
                  "exePath": "C:\\Windows\\System32\\notepad.exe",
                  "appName": "notepad",
                  "windowTitle": "示例 - 记事本",
                  "iconDataUrl": null
                },
                "pinned": false,
                "pinnedAt": 0,
                "note": "示例备注"
              }
            ]
            """);
        Assert.Equal("e2c9ad03-84bf-47b1-911a-0542b0f3a301", entry.Id);
        Assert.Equal(EntryKind.Text, entry.Type);
        Assert.Equal("完整正文\n保留换行与空格", entry.Text);
        Assert.Equal(1790992800000, entry.CreatedAtMs);
        Assert.Equal("notepad", entry.SourceApp!.AppName);
        Assert.Equal("C:\\Windows\\System32\\notepad.exe", entry.SourceApp.ExePath);
        Assert.Equal("示例 - 记事本", entry.SourceApp.WindowTitle);
        Assert.Null(entry.SourceApp.IconDataUrl);
        Assert.False(entry.Pinned);
        Assert.Equal(0, entry.PinnedAtMs);
        Assert.Equal("示例备注", entry.Note);
    }

    [Fact]
    public void 图片条目_imagePath与置顶保留()
    {
        var entry = ReadFirstEntry("""
            [
              {
                "id": "1cc08a9e-8ec1-4f53-a6c2-a50a72eb5b20",
                "type": "image",
                "imagePath": "C:\\Users\\示例用户\\AppData\\Roaming\\ClipboardTool\\images\\1cc08a9e.png",
                "createdAt": 1790992810000,
                "sourceApp": null,
                "pinned": true,
                "pinnedAt": 1790992820000,
                "note": ""
              }
            ]
            """);
        Assert.Equal(EntryKind.Image, entry.Type);
        Assert.Equal("C:\\Users\\示例用户\\AppData\\Roaming\\ClipboardTool\\images\\1cc08a9e.png", entry.ImagePath);
        Assert.Null(entry.Text);
        Assert.Null(entry.SourceApp);
        Assert.True(entry.Pinned);
        Assert.Equal(1790992820000, entry.PinnedAtMs);
    }

    [Fact]
    public void 缺省字段归一化()
    {
        var entry = ReadFirstEntry("""[ { "id": "1", "type": "text", "text": "t" } ]""");
        Assert.Equal(0, entry.CreatedAtMs);
        Assert.False(entry.Pinned);
        Assert.Equal(0, entry.PinnedAtMs);
        Assert.Equal(string.Empty, entry.Note);
        Assert.Null(entry.SourceApp);
    }

    [Theory]
    [InlineData("""{ "id": "", "type": "text", "text": "t" }""")]                 // 空 id
    [InlineData("""{ "type": "text", "text": "t" }""")]                          // 缺 id
    [InlineData("""{ "id": 5, "type": "text", "text": "t" }""")]                 // id 类型坏
    [InlineData("""{ "id": "1", "type": "weird" }""")]                           // 未知类型
    [InlineData("""{ "id": "1", "type": "text" }""")]                            // 文字字段缺失
    [InlineData("""{ "id": "1", "type": "text", "text": null }""")]              // 文字为 null
    [InlineData("""{ "id": "1", "type": "image" }""")]                           // 图片缺路径
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "createdAt": "123" }""")]   // 时间类型坏
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "createdAt": -5 }""")]      // 负时间
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "createdAt": 1.5 }""")]     // 浮点时间
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "pinned": "yes" }""")]      // pinned 类型坏
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "note": 5 }""")]            // note 类型坏
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "note": null }""")]         // note 显式 null
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "sourceApp": 3 }""")]       // 来源类型坏
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "sourceApp": { "appName": 1 } }""")]
    [InlineData("""{ "id": "1", "type": "text", "text": "t", "sourceApp": { "iconDataUrl": 5 } }""")]
    [InlineData("""[1, 2]""")]                                                    // 非对象
    [InlineData("""null""")]
    public void 单条损坏返回null_由调用方只丢该条(string line)
    {
        Assert.Null(HistoryArchive.TryReadEntry(JsonNode.Parse(line)));
    }

    [Fact]
    public void sourceApp缺字段回空串_图标null保留()
    {
        var entry = ReadFirstEntry("""[ { "id": "1", "type": "text", "text": "t", "sourceApp": { "appName": "a" } } ]""");
        Assert.Equal("a", entry.SourceApp!.AppName);
        Assert.Equal(string.Empty, entry.SourceApp.ExePath);
        Assert.Equal(string.Empty, entry.SourceApp.WindowTitle);
        Assert.Null(entry.SourceApp.IconDataUrl);
    }

    [Fact]
    public void 非数组或坏JSON_解析不到数组()
    {
        Assert.Null(HistoryArchive.TryParseArray("not json at all"));
        Assert.Null(HistoryArchive.TryParseArray("""{"entries": []}"""));
        Assert.Null(HistoryArchive.TryParseArray("null"));
        Assert.Null(HistoryArchive.TryParseArray("[] trail"));
        Assert.Empty(HistoryArchive.TryParseArray("[]")!);
    }

    [Fact]
    public void 序列化文本条目_只投影已知字段()
    {
        var entry = new HistoryEntry(
            "id1", EntryKind.Text, "正文", ImagePath: null, CreatedAtMs: 1790992800000,
            SourceApp: new SourceApp("exe", "app", "标题", IconDataUrl: null),
            Pinned: true, PinnedAtMs: 5, Note: "备注");
        var array = HistoryArchive.TryParseArray(HistoryArchive.Serialize([entry]))!;
        var obj = Assert.IsType<JsonObject>(array[0]);

        var keys = obj.Select(p => p.Key).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "createdAt", "id", "note", "pinned", "pinnedAt", "sourceApp", "text", "type" }, keys);
        Assert.Equal("text", (string?)obj["type"]);
        Assert.Equal(1790992800000, (long?)obj["createdAt"]);
        Assert.True((bool?)obj["pinned"]);
        Assert.Equal(5, (long?)obj["pinnedAt"]);
        var sourceApp = Assert.IsType<JsonObject>(obj["sourceApp"]);
        Assert.Equal("app", (string?)sourceApp["appName"]);
        Assert.True(sourceApp.ContainsKey("iconDataUrl"), "旧档样例里 iconDataUrl 恒写（null）");
        Assert.Null(sourceApp["iconDataUrl"]);
    }

    [Fact]
    public void 序列化图片条目_互斥不写text_来源null写null键()
    {
        var entry = new HistoryEntry(
            "id2", EntryKind.Image, Text: null, ImagePath: @"C:\img\id2.png", CreatedAtMs: 1,
            SourceApp: null, Pinned: false, PinnedAtMs: 0, Note: string.Empty);
        var array = HistoryArchive.TryParseArray(HistoryArchive.Serialize([entry]))!;
        var obj = Assert.IsType<JsonObject>(array[0]);

        var keys = obj.Select(p => p.Key).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "createdAt", "id", "imagePath", "note", "pinned", "pinnedAt", "sourceApp", "type" }, keys);
        Assert.Equal("image", (string?)obj["type"]);
        Assert.Equal(@"C:\img\id2.png", (string?)obj["imagePath"]);
        Assert.True(obj.ContainsKey("sourceApp"), "sourceApp 恒写：无来源时写 null 键");
        Assert.Null(obj["sourceApp"]);
    }

    [Fact]
    public void 往返_序列化再解析_字段等值()
    {
        var entries = new[]
        {
            new HistoryEntry("a", EntryKind.Text, "正文\n多行", null, 1790992800000,
                new SourceApp("", "notepad", "窗口", null), true, 1790992899999, "  备注  "),
            new HistoryEntry("b", EntryKind.Image, null, @"C:\images\b.png", 1790992810000,
                null, false, 0, string.Empty),
        };
        var array = HistoryArchive.TryParseArray(HistoryArchive.Serialize(entries))!;
        Assert.Equal(2, array.Count);

        Assert.Equal(entries[0], HistoryArchive.TryReadEntry(array[0]));
        Assert.Equal(entries[1], HistoryArchive.TryReadEntry(array[1]));
    }

    [Fact]
    public void 序列化空历史_输出空数组()
    {
        Assert.Equal("[]", HistoryArchive.Serialize([]));
    }
}
