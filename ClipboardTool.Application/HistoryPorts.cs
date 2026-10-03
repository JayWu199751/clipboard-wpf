namespace ClipboardTool.Application;

/// <summary>历史存档读取结果（02-spec/02 §1）：</summary>
public abstract record HistoryFileRead
{
    private HistoryFileRead()
    {
    }

    /// <summary>没有旧档（全新用户）：按空历史处理，无需备份。</summary>
    public sealed record Missing : HistoryFileRead;

    /// <summary>合法 JSON 数组：载入。</summary>
    public sealed record Loaded(string Json) : HistoryFileRead;

    /// <summary>文件在但不是合法 JSON 数组（整体坏档）：先备份原件，再按旧行为回空历史；
    /// 绝不拿空列表覆盖唯一原件。</summary>
    public sealed record Corrupt(string Json) : HistoryFileRead;

    /// <summary>文件在但读不出来（占用/权限）：与缺档、坏档是三回事——本会话禁止回写，
    /// 内存照常工作但磁盘原件分毫不动（否则下一条记录就会以空表覆盖从未读出的原件）。</summary>
    public sealed record Unreadable : HistoryFileRead;
}

/// <summary>
/// 历史存档持久化端口：Application 只依赖此抽象；路径布局、原子写入与坏档备份归
/// Infrastructure.JsonStore（02-spec/02 §1 可靠性要求在本票落地）。
/// </summary>
public interface IHistoryStorage
{
    HistoryFileRead ReadHistory();

    /// <summary>整表落盘（原子写）。由 HistoryService 在每次条目集合变化后调用。</summary>
    void SaveHistory(string historyJson);

    /// <summary>备份当前历史文件原件到独立路径（backup/），返回备份路径；无文件或失败返回 null。</summary>
    string? BackupHistory();
}
