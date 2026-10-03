using ClipboardTool.Application;
using ClipboardTool.Domain.History;
using ClipboardTool.Tests.HistoryRules;
using Xunit;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// HistoryService 持久化收敛测试（F08）：启动读档（坏档先备份）、变更自动落盘、
/// 无存储时照常工作。存储端口由假件提供。
/// </summary>
public sealed class HistoryServicePersistenceTests
{
    private sealed class FakeStorage : IHistoryStorage
    {
        public HistoryFileRead NextRead { get; set; } = new HistoryFileRead.Missing();
        public List<string> SavedHistory { get; } = [];
        public int BackupCalls { get; private set; }

        public HistoryFileRead ReadHistory() => NextRead;

        public void SaveHistory(string historyJson) => SavedHistory.Add(historyJson);

        public string? BackupHistory()
        {
            BackupCalls++;
            return "backup/clipboard-history.json";
        }
    }

    private static (HistoryService Service, HistoryStore Store, FakeStorage Storage) Create(
        FakeStorage? storage = null)
    {
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory, new FakeImageFiles(),
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000);
        storage ??= new FakeStorage();
        return (new HistoryService(store, storage), store, storage);
    }

    [Fact]
    public void 载入旧档_条目进入存储并广播一次()
    {
        var storage = new FakeStorage
        {
            NextRead = new HistoryFileRead.Loaded(
                """[{"id":"1","type":"text","text":"a"},{"id":"2","type":"text","text":"b"}]"""),
        };
        var (service, store, _) = Create(storage);

        var fired = 0;
        service.EntriesChanged += () => fired++;
        service.LoadFromStorage();

        Assert.Equal(2, store.Count);
        Assert.Equal("a", store.Entries[0].Text);
        Assert.Equal(1, fired);
        Assert.Empty(storage.SavedHistory); // 读档不回写，避免无谓覆盖原件
    }

    [Fact]
    public void 整体坏档_先备份_回空历史_不丢原件()
    {
        var storage = new FakeStorage { NextRead = new HistoryFileRead.Corrupt("not json") };
        var (service, store, _) = Create(storage);

        service.LoadFromStorage();

        Assert.Equal(1, storage.BackupCalls);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 缺档_回空历史并广播()
    {
        var (service, store, storage) = Create();
        var fired = 0;
        service.EntriesChanged += () => fired++;

        service.LoadFromStorage();

        Assert.Equal(0, storage.BackupCalls);
        Assert.Equal(0, store.Count);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void 读失败_本会话禁止回写_内存照常工作()
    {
        // 文件在但读不出（占用/权限）：绝不回写——否则下一条记录会以空表覆盖从未读出的原件
        var storage = new FakeStorage { NextRead = new HistoryFileRead.Unreadable() };
        var (service, store, _) = Create(storage);

        service.LoadFromStorage();
        Assert.True(service.RecordText("a"));

        Assert.Equal(0, storage.BackupCalls);
        Assert.Empty(storage.SavedHistory); // 禁写生效：整表没有回盘
        Assert.Equal(1, store.Count);       // 内存照常工作
    }

    [Fact]
    public void 变更自动落盘_记录与提升各写一次()
    {
        var (service, _, storage) = Create();

        service.RecordText("一");
        service.RecordText("二");
        service.Promote(FindId(service, "一"));

        Assert.Equal(3, storage.SavedHistory.Count);
        Assert.Contains("\"一\"", storage.SavedHistory[0]);

        // 落盘内容可被重新解析（序列化即存档契约投影）
        var parsed = HistoryArchive.TryParseArray(storage.SavedHistory[2])!;
        Assert.Equal(2, parsed.Count);
        Assert.Equal("一", HistoryArchive.TryReadEntry(parsed[0])!.Text);
    }

    [Fact]
    public void 重复命中也落盘_提升后的顺序必须持久化()
    {
        var (service, _, storage) = Create();
        service.RecordText("a");
        service.RecordText("b");
        service.RecordText("a"); // 去重命中 → 提升到普通块首

        Assert.Equal(3, storage.SavedHistory.Count);
        var parsed = HistoryArchive.TryParseArray(storage.SavedHistory[2])!;
        Assert.Equal("a", HistoryArchive.TryReadEntry(parsed[0])!.Text);
    }

    [Fact]
    public void 空文本_不落盘不广播()
    {
        var (service, _, storage) = Create();
        var fired = 0;
        service.EntriesChanged += () => fired++;

        Assert.False(service.RecordText(""));

        Assert.Empty(storage.SavedHistory);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void 置顶与备注变更经服务收敛落盘()
    {
        var (service, _, storage) = Create();
        service.RecordText("a");

        var id = service.Entries[0].Id;
        Assert.True(service.SetNote(id, "  备注  "));
        Assert.True(service.TogglePin(id));

        Assert.Equal(3, storage.SavedHistory.Count);
        var pinned = HistoryArchive.TryReadEntry(
            HistoryArchive.TryParseArray(storage.SavedHistory[2])![0])!;
        Assert.True(pinned.Pinned);
        Assert.Equal("备注", pinned.Note);
    }

    [Fact]
    public void 记录图片_落盘且重复命中不重写文件()
    {
        var images = new FakeImageFiles();
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory, images,
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000);
        var storage = new FakeStorage();
        var service = new HistoryService(store, storage);

        Assert.True(service.RecordImage("png"u8.ToArray()));
        Assert.True(service.RecordImage("png"u8.ToArray())); // 去重命中 → 只提升

        Assert.Equal(1, images.SaveCalls);                   // 文件只写一次
        Assert.Equal(2, storage.SavedHistory.Count);         // 两次集合变化各落盘一次
        var parsed = HistoryArchive.TryParseArray(storage.SavedHistory[1])!;
        Assert.Single(parsed);
    }

    [Fact]
    public void 删除与清空经服务收敛落盘()
    {
        var images = new FakeImageFiles();
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory, images,
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000);
        var storage = new FakeStorage();
        var service = new HistoryService(store, storage);

        service.RecordImage("png"u8.ToArray());
        var id = service.Entries[0].Id;
        var imagePath = service.Entries[0].ImagePath!;
        Assert.True(service.Remove(id));
        service.Clear();

        Assert.Equal([imagePath], images.Removed); // 删除联动删 PNG
        Assert.Equal(3, storage.SavedHistory.Count);
        Assert.Equal("[]", storage.SavedHistory[2]);
    }

    [Fact]
    public void 无存储_照常工作不落盘()
    {
        var store = new HistoryStore(
            HistoryStore.DefaultMaxHistory, new FakeImageFiles(),
            () => Guid.NewGuid().ToString(), () => 1_700_000_000_000);
        var service = new HistoryService(store);

        service.LoadFromStorage(); // 不得抛出
        Assert.True(service.RecordText("a"));
        service.Promote("missing");
        Assert.Equal(1, store.Count);
    }

    private static string FindId(ClipboardTool.Application.HistoryService service, string text) =>
        service.Entries.First(e => e.Text == text).Id;
}
