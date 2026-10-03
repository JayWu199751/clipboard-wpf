namespace ClipboardTool.Domain.History;

/// <summary>条目类型（存档字段精确取值 text / image，02-spec/02 §1）。</summary>
public enum EntryKind
{
    Text,
    Image,
}

/// <summary>来源应用（F06）。旧档四键缺省空串，图标可空；复制处理时取前台信息。</summary>
public sealed record SourceApp(
    string ExePath = "",
    string AppName = "",
    string WindowTitle = "",
    string? IconDataUrl = null);

/// <summary>
/// 历史条目（完整模型，T03 起）：Id/类型/正文/图片路径/创建时间/来源/置顶/置顶时间/备注。
/// 时间单位为 epoch 毫秒（02-spec/02 §2 存档契约）；条目身份只看内容（F03）；
/// 文字逐字符相等，图片按规范化像素 SHA-1（T06/ADR-0005，兼容旧版编码）；
/// 取消置顶保留 pinnedAt（存档契约）。
/// </summary>
public sealed record HistoryEntry(
    string Id,
    EntryKind Type,
    string? Text,
    string? ImagePath,
    long CreatedAtMs,
    SourceApp? SourceApp,
    bool Pinned,
    long PinnedAtMs,
    string Note);

/// <summary>记录结果：Entry 为 null 表示本次没有落库（空内容或图片写盘失败，调用方不更新基线）。
/// Hash 仅 recordImage 填写（供测试与调用方使用）。</summary>
public sealed record RecordOutcome(HistoryEntry? Entry, bool Deduped, string Hash = "");

/// <summary>
/// 图片文件端口：写图、取哈希、删图、判存在。四条全部必供（构造即需要实现）——
/// 曾写成 Option 配八连 setter，漏配一个不报错、只静默降级（legacy history.rs 的教训）。
/// </summary>
public interface IImageFileStore
{
    /// <summary>写 PNG 到 images/&lt;id&gt;.png，返回落盘路径；失败返回 null（调用方不落库）。</summary>
    string? SavePng(byte[] png, string id);

    /// <summary>取文件内容 SHA-1 十六进制；读不到返回空串（空串永不命中去重）。</summary>
    string HashFile(string path);

    /// <summary>PNG 字节 → 规范化像素身份 SHA-1（ADR-0005 第 4 条：PNG 解码 → 规范化 RGBA
    /// → SHA-1；旧版与新版编码在同一像素下同哈希）。解不出像素返回空串（永不命中）。</summary>
    string HashPng(byte[] png);

    /// <summary>删除图片文件（裁剪/删除联动，F05）。</summary>
    void RemoveFile(string path);

    /// <summary>判断图片文件是否存在（载入时丢图条目跳过，F08）。</summary>
    bool FileExists(string path);
}
