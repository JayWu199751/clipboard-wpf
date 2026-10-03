using ClipboardTool.Domain.History;

namespace ClipboardTool.Application;

/// <summary>
/// 剪贴板监听编排（legacy main.rs poll_round 的移植）：事件源优先，事件源起不来退 600ms 轮询兜底。
/// 每轮：序列号短路 → 独占读取（占用=欠重试，绝不当空接受）→ 基线判定 → 落库 → confirm。
/// 单轮异常只记录并跳过，不允许杀死监听。
/// </summary>
public sealed class ClipboardWatchService
{
    /// <summary>事件源起不来时的兜底周期（legacy POLL_INTERVAL）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);

    private readonly IClipboardReader _reader;
    private readonly IClipboardSequence _sequence;
    private readonly HistoryService _history;
    private readonly IClipboardWriter _writer;
    private readonly PollBaseline _baseline = new();
    private readonly object _roundGate = new();
    private bool _baselineSyncPending;

    public ClipboardWatchService(
        IClipboardReader reader,
        IClipboardSequence sequence,
        HistoryService history,
        IClipboardWriter writer)
    {
        _reader = reader;
        _sequence = sequence;
        _history = history;
        _writer = writer;
    }

    /// <summary>
    /// 启动监听：先同步启动基线（启动前已躺在剪贴板里的内容不再收录，F09），
    /// 再异步启动事件源；事件源就绪失败时退回 600ms 轮询兜底。
    /// </summary>
    public void Start(IClipboardChangeEventSource source)
    {
        SyncBaseline();
        source.RoundHandler = PollRound;
        source.Start(ready =>
        {
            if (!ready)
            {
                InstallPollingFallback();
            }
        });
    }

    private void InstallPollingFallback()
    {
        // 轮询最坏是慢，事件源最坏是全哑——留兜底，行为与改动前一致（序列号短路在轮内）
        var timer = new System.Threading.Timer(_ => PollRound(), null, PollInterval, PollInterval);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => timer.Dispose();
    }

    /// <summary>
    /// 读一次剪贴板当前内容，交给基线认作「已见过」（启动基线、自己写入后的同步）。
    /// 读不到（被占用）不放弃：挂欠账标记，之后每一轮先补这笔同步再谈新内容——
    /// 否则自己刚写入的内容会在下一次轮询里被当成新复制重复记录+重复提升（粘贴后闪烁）。
    /// 返回 false 表示这笔基线还欠着。
    /// </summary>
    public bool SyncBaseline()
    {
        lock (_roundGate)
        {
            return SyncBaselineLocked();
        }
    }

    /// <summary>
    /// 写剪贴板并同步基线（粘贴链第 2/3 步的原子化）：两者在同一轮内临界区完成，
    /// 事件轮不可能看到「已写入但基线未跟上」的中间态——否则自写内容会在下一次
    /// 通知/轮询里被当成新复制重复记录+重复提升（粘贴后列表闪烁的来源）。
    /// 返回 false 表示写入失败（基线不动）。WriteAndSyncImage 为图片侧（T06）：
    /// 按需读盘写位图内容，失败同样欠账。
    /// </summary>
    public bool WriteAndSyncText(string text)
    {
        lock (_roundGate)
        {
            if (!_writer.WriteText(text))
            {
                return false;
            }
            SyncBaselineLocked();
            return true;
        }
    }

    /// <summary>图片侧的写 + 同步（T06）：语义与 WriteAndSyncText 完全一致。</summary>
    public bool WriteAndSyncImage(string pngPath)
    {
        lock (_roundGate)
        {
            if (!_writer.WriteImage(pngPath))
            {
                return false;
            }
            SyncBaselineLocked();
            return true;
        }
    }

    /// <summary>真实现：假定已持 _roundGate（PollRoundCore 在锁内直接调用，避免 Monitor 重入的隐式依赖）。</summary>
    private bool SyncBaselineLocked()
    {
        var seq = _sequence.Current();
        if (_reader.Read() is not ClipboardReadOutcome.Known known)
        {
            // 启动这一刻剪贴板被占着：不预设基线即可；sync 的用途只是「别把已躺在剪贴板里的
            // 内容当成新复制」，读不到就先欠着（启动场景）或重试到补上（自写场景）
            _baselineSyncPending = true;
            return false;
        }
        _baseline.SyncNow(known.Snapshot.Png, known.Snapshot.Text);
        _baseline.NoteSeq(seq);
        _baselineSyncPending = false;
        return true;
    }

    /// <summary>
    /// 跑一轮：读、判定、落库、confirm。返回 true 表示尘埃落定（事件源据此撤重试定时器）；
    /// false 表示被占用、基线欠账未补或落库失败，需要稍后再试。
    /// </summary>
    public bool PollRound()
    {
        lock (_roundGate)
        {
            try
            {
                return PollRoundCore();
            }
            catch (Exception)
            {
                // 单轮异常不终止监听；也不排重试——免得同一次异常把重试打成死循环（legacy catch_unwind 语义）
                return true;
            }
        }
    }

    private bool PollRoundCore()
    {
        // 基线欠账（启动/自写同步时被占用）：先补这笔再谈新内容，补不上本轮按不可信处理
        if (_baselineSyncPending && !SyncBaselineLocked())
        {
            return false;
        }

        // 记的是**触发这次读取的那个**序列号：若读取期间内容又变了，序列号会前进，下一轮不会被短路
        var seq = _sequence.Current();
        if (_baseline.SkipUnchanged(seq))
        {
            return true;
        }

        if (_reader.Read() is not ClipboardReadOutcome.Known known)
        {
            // 占用≠空：基线不动、序列号不推进，只欠一轮重试；当成空接受会让这次复制永久消失
            _baseline.NoteUntrusted();
            return false;
        }

        var change = _baseline.Observe(known.Snapshot.Png, known.Snapshot.Text);
        if (change is null)
        {
            // 无新内容：仍要接受暂存的基线更新（图片未变时文字基线得跟上）
            _baseline.Confirm(true);
            _baseline.NoteSeq(seq);
            return true;
        }

        var recorded = change switch
        {
            BaselineChange.Text text => _history.RecordText(text.Value),
            // T06 图片侧：写盘失败返回 false → Confirm(false) 欠账重试（同轮不推进基线）
            BaselineChange.Image image => _history.RecordImage(image.Png),
            _ => false,
        };

        _baseline.Confirm(recorded);
        _baseline.NoteSeq(seq);
        return !_baseline.RetryPending;
    }
}
