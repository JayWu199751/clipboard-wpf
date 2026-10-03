using System.Collections.Concurrent;

namespace ClipboardTool.Application;

/// <summary>
/// 模式意图的单一串行写入者：专用执行线程（本仓库 ADR-0004）。
/// 恢复/注入含 AttachThreadInput、SetForegroundWindow、SendInput 与百毫秒级回读等待，
/// 不得占用 UI 泵（失败路径会冻结面板渲染）；UI 线程只发具名意图、渲染事实。
/// legacy ADR-0006（modes-on-dedicated-thread）的同构落地；呼出/停靠的几何迁移归 T04/T05。
/// </summary>
public sealed class ModeExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public ModeExecutor()
    {
        _thread = new Thread(RunLoop)
        {
            Name = "ClipboardTool.ModeExecutor",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>投递一个具名意图；按到达顺序在执行线程串行执行。</summary>
    public void Post(Action intent) => _queue.Add(intent);

    private void RunLoop()
    {
        foreach (var intent in _queue.GetConsumingEnumerable())
        {
            try
            {
                intent();
            }
            catch
            {
                // 意图失败不得终止执行线程；失败结果由意图自身经结果契约回报渲染层
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
    }
}
