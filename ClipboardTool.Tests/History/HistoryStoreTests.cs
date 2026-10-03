using ClipboardTool.Domain.History;
using Xunit;

namespace ClipboardTool.Tests.HistoryRules;

/// <summary>历史记录 T02 文字段测试（F01/F03/F04 的 record_text/promote 语义）。</summary>
public sealed class HistoryStoreTests
{
    private static HistoryStore CreateStore() =>
        new(newId: () => Guid.NewGuid().ToString("N"), nowMs: () => 1_700_000_000_000);

    [Fact]
    public void 空字符串不生成条目()
    {
        var store = CreateStore();
        var outcome = store.RecordText("");
        Assert.Null(outcome.Entry);
        Assert.False(outcome.Deduped);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void 正文原样保存_不自动trim()
    {
        var store = CreateStore();
        var outcome = store.RecordText("  两端有空格  ");
        Assert.Equal("  两端有空格  ", outcome.Entry!.Text);
    }

    [Fact]
    public void 重复记录沿用原条目并提升()
    {
        var store = CreateStore();
        var first = store.RecordText("abc").Entry!;
        store.RecordText("def");
        Assert.Equal(0, IndexOf(store, "def"));
        Assert.Equal(1, IndexOf(store, "abc"));

        var again = store.RecordText("abc");
        Assert.True(again.Deduped);
        Assert.Same(first.Id, again.Entry!.Id);
        Assert.Equal(first.CreatedAtMs, again.Entry.CreatedAtMs);
        Assert.True(IndexOf(store, "abc") == 0, "重复命中必须提升到块首");
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void 新记录插入块首_旧条目顺延()
    {
        var store = CreateStore();
        store.RecordText("一");
        store.RecordText("二");
        Assert.Equal("二", store.Entries[0].Text);
        Assert.Equal("一", store.Entries[1].Text);
    }

    [Fact]
    public void 使用条目提升到块首()
    {
        var store = CreateStore();
        store.RecordText("一");
        store.RecordText("二");
        store.RecordText("三");

        Assert.True(store.Promote(store.Entries.First(entry => entry.Text == "一").Id));
        Assert.Equal("一", store.Entries[0].Text);
    }

    private static int IndexOf(HistoryStore store, string text)
    {
        for (var i = 0; i < store.Entries.Count; i++)
        {
            if (store.Entries[i].Text == text)
            {
                return i;
            }
        }
        return -1;
    }
}
