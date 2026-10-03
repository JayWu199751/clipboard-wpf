using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Domain.Settings;
using ClipboardTool.Infrastructure.Windows;
using ClipboardTool.Tests.HistoryRules;
using Xunit;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 存档文件读写测试（F08/F37；02-spec/02 §1 可靠性要求）：
/// 原子写入（.tmp→替换）、替换失败原件不丢可恢复、坏档先备份再回默认、备份放独立路径。
/// </summary>
public sealed class JsonStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clipstore-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore _store;

    public JsonStoreTests()
    {
        _store = new JsonStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理尽力而为 */ }
    }

    private string HistoryPath => Path.Combine(_dir, "clipboard-history.json");

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private string BackupDir => Path.Combine(_dir, "backup");

    [Fact]
    public void 原子写入_成功后无tmp残留_内容正确()
    {
        _store.SaveHistory("""[{"id":"1","type":"text","text":"a"}]""");
        Assert.Equal("""[{"id":"1","type":"text","text":"a"}]""", File.ReadAllText(HistoryPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void 替换失败_原件不丢且无tmp残留_解锁后可恢复写入()
    {
        const string v1 = """[{"id":"1","type":"text","text":"v1"}]""";
        const string v2 = """[{"id":"2","type":"text","text":"v2"}]""";
        _store.SaveHistory(v1);

        // 占住原件（不给 FileShare.Delete）：替换必须失败，原件与内容保持完整。
        // 断言在解锁后做——自己占着 FileShare.None 时自己也不能读。
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // 失败异常类型因 Win32 错误码而异（IOException/UnauthorizedAccess），
            // 关键断言是：必须失败、原件不丢、无 tmp 残留
            Assert.NotNull(Record.Exception(() => _store.SaveHistory(v2)));
        }
        Assert.Equal(v1, File.ReadAllText(HistoryPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // 解锁后重写成功 = 重启恢复路径
        _store.SaveHistory(v2);
        Assert.Equal(v2, File.ReadAllText(HistoryPath));
    }

    [Fact]
    public void 读历史_无文件返回Missing_合法数组返回Loaded()
    {
        Assert.IsType<HistoryFileRead.Missing>(_store.ReadHistory());

        File.WriteAllText(HistoryPath, """[{"id":"1","type":"text","text":"a"}]""");
        var loaded = Assert.IsType<HistoryFileRead.Loaded>(_store.ReadHistory());
        Assert.Contains("\"a\"", loaded.Json);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"entries": []}""")]
    [InlineData("")]
    public void 读历史_整体坏档返回Corrupt(string bad)
    {
        File.WriteAllText(HistoryPath, bad);
        var corrupt = Assert.IsType<HistoryFileRead.Corrupt>(_store.ReadHistory());
        Assert.Equal(bad, corrupt.Json);
    }

    [Fact]
    public void 坏档备份_先备份原件_再覆盖不丢证据()
    {
        const string bad = "not json at all";
        File.WriteAllText(HistoryPath, bad);

        Assert.IsType<HistoryFileRead.Corrupt>(_store.ReadHistory());
        var backupPath = _store.BackupHistory();
        Assert.NotNull(backupPath);
        Assert.StartsWith(BackupDir, backupPath);
        Assert.Equal(bad, File.ReadAllText(backupPath));
        Assert.True(Directory.EnumerateFiles(BackupDir).Count() >= 1);

        // 备份在手之后，正式写档覆盖原件
        _store.SaveHistory("[]");
        Assert.Equal("[]", File.ReadAllText(HistoryPath));
        Assert.Equal(bad, File.ReadAllText(backupPath));
    }

    [Fact]
    public void 备份_无文件时返回null()
    {
        Assert.Null(_store.BackupHistory());
        Assert.Null(_store.BackupSettings());
    }

    [Fact]
    public void 设置_往返写出camelCase无auto_start()
    {
        var settings = new AppSettings(true, "Ctrl+Alt+V", ThemeKind.Dark);
        _store.SaveSettings(settings);
        var raw = File.ReadAllText(SettingsPath);
        Assert.Contains("\"autoStart\":true", raw);
        Assert.DoesNotContain("auto_start", raw);

        var loaded = _store.ReadSettings();
        Assert.Equal(settings, loaded);
    }

    [Fact]
    public void 设置_无文件回默认_坏档回默认并备份()
    {
        Assert.Equal(AppSettings.Default, _store.ReadSettings());

        File.WriteAllText(SettingsPath, "not json at all");
        Assert.Equal(AppSettings.Default, _store.ReadSettings());
        var backupPath = _store.BackupSettings();
        Assert.NotNull(backupPath);
        Assert.Equal("not json at all", File.ReadAllText(backupPath));
    }

    [Fact]
    public void 旧存档样例_经JsonStore读入存储_文字图片混合落位正确()
    {
        // 样例存档：文字+图片混合、置顶+备注+来源、缺字段、未知类型（丢 PNG 变体在 Store 测试覆盖）
        var samplePng = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestAssets", "images", "sample.png"));
        Assert.True(File.Exists(samplePng), "真实测试 PNG 必须随测试工程输出");
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestAssets", "archives", "mixed-archive.json"));
        var json = File.ReadAllText(fixture).Replace("__SAMPLE_PNG__", samplePng.Replace(@"\", @"\\"));

        File.WriteAllText(HistoryPath, json);
        var loaded = Assert.IsType<HistoryFileRead.Loaded>(_store.ReadHistory());
        var raw = HistoryArchive.TryParseArray(loaded.Json)!;

        var images = new FakeImageFiles();
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory, images,
            () => Guid.NewGuid().ToString("N"), () => 1_700_000_000_000);
        store.Load(raw);

        // 未知类型/空 id/缺文字被过滤；置顶块在前按 pinnedAt，普通块随后
        Assert.Equal(2, store.Count);
        Assert.Equal("1cc08a9e-8ec1-4f53-a6c2-a50a72eb5b20", store.Entries[0].Id);
        Assert.True(store.Entries[0].Pinned);
        Assert.Equal(1790992820000, store.Entries[0].PinnedAtMs);
        Assert.EndsWith("sample.png", store.Entries[0].ImagePath);
        Assert.Equal("e2c9ad03-84bf-47b1-911a-0542b0f3a301", store.Entries[1].Id);
        Assert.Equal("示例备注", store.Entries[1].Note);
        Assert.Equal("notepad", store.Entries[1].SourceApp!.AppName);
    }
}
