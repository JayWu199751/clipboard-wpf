using System.Security.Cryptography;

namespace ClipboardTool.Domain.History;

/// <summary>本轮需要落库的新内容（legacy 术语表：轮询基线的判定产物）。</summary>
public abstract record BaselineChange
{
    public sealed record Text(string Value) : BaselineChange;
    public sealed record Image(byte[] Png, string Hash) : BaselineChange;
}

/// <summary>
/// 轮询基线：判断「这次剪贴板内容算不算一次新复制」的唯一实现（legacy poll_baseline.rs 移植）。
///
/// 规则：
///   1. 序列号未变且没有待重试的写盘失败 → 本轮不必打开剪贴板（省 CPU，也少与他人争用）；
///   2. 图片优先：图片非空即按 PNG 内容哈希判定，文字基线跟上但不清空图片基线；
///   3. 无图片时按文字判定：空文字不算新复制；文字分支清空图片基线（图片→文字是真实切换）；
///   4. 写盘失败，或这一轮剪贴板被别的程序占着、读不到（NoteUntrusted）
///      → 基线一律不动、置重试标记，下一次即使序列号未变也再试一次；
///   5. 自己刚写入剪贴板（复制并粘贴）→ 立刻把当前内容认作基线，否则下一次轮询会被当成
///      「新复制」再提升+广播一次，表现为粘贴后列表闪一下。
///
/// 本类型不碰剪贴板、不碰窗口、不碰存储：调用方把读到的 (png, text) 递进来，
/// 拿回一个 Change，落库后再用 Confirm 回报成败。因此可以纯数据表驱动直测。
/// </summary>
public sealed class PollBaseline
{
    private string _text = string.Empty;
    private string _imageHash = string.Empty;
    private uint _seq;
    private bool _pendingRetry;
    private PendingUpdate? _pending;

    /// <summary>测试观察口（外部测试程序集经 InternalsVisibleTo 读取，等同 legacy 同模块测试直读私有态）。</summary>
    internal string TextForTest => _text;
    internal string ImageHashForTest => _imageHash;

    private sealed record PendingUpdate(string? Text, string? ImageHash);

    /// <summary>序列号未变且无待重试 → 本轮可跳过（seq 为 0 表示取不到序列号，保守地照常轮询）。</summary>
    public bool SkipUnchanged(uint seq) => seq != 0 && !_pendingRetry && seq == _seq;

    /// <summary>
    /// 记录本轮结束时的序列号。记的是**触发这次读取的那个**序列号：若读取期间内容又变了
    /// （写入方还没收手），序列号会前进，下一轮就不会被短路、会再读一次。
    /// </summary>
    public void NoteSeq(uint seq) => _seq = seq;

    /// <summary>
    /// 本轮读取不可信：剪贴板被别的程序占着，我们看到的既不是空内容、也不是新内容。
    /// 基线一律不动、序列号不推进，只留下「这一轮还欠着」的标记——置了它之后
    /// SkipUnchanged 不再短路，下一次尝试不会因为序列号没变而被跳过去。
    /// </summary>
    public void NoteUntrusted() => _pendingRetry = true;

    /// <summary>是否还有欠着的一轮（读不可信，或写盘失败）。</summary>
    public bool RetryPending => _pendingRetry;

    /// <summary>与基线比对本轮所见。返回非 null 时基线只是暂存，必须再调用 Confirm 才生效（图片写盘可能失败）。</summary>
    public BaselineChange? Observe(byte[]? png, string text)
    {
        if (png is { Length: > 0 })
        {
            var hash = Sha1Hex(png);
            if (hash != _imageHash)
            {
                _pending = new PendingUpdate(text, hash);
                return new BaselineChange.Image(png, hash);
            }
            _pending = new PendingUpdate(text, null);
            return null;
        }

        var changed = text.Length > 0 && text != _text;
        _pending = new PendingUpdate(text, string.Empty);
        return changed ? new BaselineChange.Text(text) : null;
    }

    /// <summary>回报本轮落库结果：成功接受基线，失败则基线不动并置重试标志。</summary>
    public void Confirm(bool ok)
    {
        var pending = _pending;
        _pending = null;
        if (pending is null)
        {
            return;
        }
        if (!ok)
        {
            _pendingRetry = true;
            return;
        }
        if (pending.Text is not null)
        {
            _text = pending.Text;
        }
        if (pending.ImageHash is not null)
        {
            _imageHash = pending.ImageHash;
        }
        _pendingRetry = false;
    }

    /// <summary>直接把当前内容认作基线（启动基线、自己写入后的同步），不产生判定。</summary>
    public void SyncNow(byte[]? png, string text)
    {
        _pending = null;
        _pendingRetry = false;
        if (png is { Length: > 0 })
        {
            _imageHash = Sha1Hex(png);
            _text = text;
        }
        else
        {
            _text = text;
            _imageHash = string.Empty;
        }
    }

    /// <summary>PNG 内容 SHA-1（F03 图片身份；与 legacy sha1_hex 同语义）。</summary>
    public static string Sha1Hex(byte[] bytes) =>
        Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
}
