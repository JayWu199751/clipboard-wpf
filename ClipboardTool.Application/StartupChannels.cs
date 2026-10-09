namespace ClipboardTool.Application;

/// <summary>计划任务事实（F36 三通道之一）：任务是否存在、是否带登录触发器、任务里记录的 exe 路径。</summary>
public readonly record struct TaskFacts(bool Exists, bool HasLogonTrigger, string ExePath);

/// <summary>收敛动作：把任务事实对齐到持久化意图所需的最小操作（注册=幂等替换）。</summary>
public enum StartupTaskAction
{
    /// <summary>事实已对齐意图（含路径一致），什么都不做。</summary>
    None,
    /// <summary>意图开 & 任务缺席：创建带登录触发器的任务。</summary>
    CreateWithTrigger,
    /// <summary>意图关 & 任务缺席：创建不带触发器的任务（静默按需通道常驻）。</summary>
    CreateWithoutTrigger,
    /// <summary>补触发器或路径过期：重注册且带登录触发器（升级更新 exe 路径走这里）。</summary>
    ReRegisterWithTrigger,
    /// <summary>移触发器（保留静默按需通道）或路径过期：重注册且不带登录触发器。</summary>
    ReRegisterWithoutTrigger,
}

/// <summary>开关三态（红绿一）：意图如何落地。</summary>
public enum StartupToggleMode
{
    /// <summary>开发构建：只记意图，不触碰任务事实（调试进程不是提权正式入口）。</summary>
    IntentOnly,
    /// <summary>未提权：意图照记，事实重建延后到下次提权启动收敛。</summary>
    Defer,
    /// <summary>已提权：立即重建事实，失败回退意图。</summary>
    ExecuteNow,
}

/// <summary>
/// 静默启动通道的纯判定（F36；工单红绿一、二）：
/// autoStart 是持久化意图、计划任务与触发器是事实，不一致靠开关或下次提权启动收敛。
/// intent=用户意图（settings.json），factual=计划任务查询结果，路径一致性用于升级时更新任务。
/// </summary>
public static class StartupChannelRules
{
    /// <summary>开关三态：开发构建只记意图；未提权延后；已提权立即执行。</summary>
    public static StartupToggleMode DecideToggle(bool isDevelopmentBuild, bool isElevated) =>
        (isDevelopmentBuild, isElevated) switch
        {
            (true, _) => StartupToggleMode.IntentOnly,
            (_, false) => StartupToggleMode.Defer,
            _ => StartupToggleMode.ExecuteNow,
        };

    /// <summary>
    /// 判定表（提权生产构建前提）：意图 × 任务事实 × 当前 exe 路径 → 收敛动作。
    /// 任务本身是常驻静默拉起入口：意图关也只移触发器、不删任务；路径过期随动作一并更新。
    /// </summary>
    public static StartupTaskAction DecideConvergence(bool intent, TaskFacts factual, string currentExePath)
    {
        if (!factual.Exists)
        {
            return intent ? StartupTaskAction.CreateWithTrigger : StartupTaskAction.CreateWithoutTrigger;
        }
        var pathStale = !string.Equals(factual.ExePath, currentExePath, StringComparison.OrdinalIgnoreCase);
        return (intent, factual.HasLogonTrigger) switch
        {
            (true, false) => StartupTaskAction.ReRegisterWithTrigger,
            (true, true) => pathStale ? StartupTaskAction.ReRegisterWithTrigger : StartupTaskAction.None,
            (false, true) => StartupTaskAction.ReRegisterWithoutTrigger,
            (false, false) => pathStale ? StartupTaskAction.ReRegisterWithoutTrigger : StartupTaskAction.None,
        };
    }

    /// <summary>动作 → 注册参数：带触发器动作映射（None/移触发器侧均不带）。</summary>
    public static bool WithLogonTrigger(StartupTaskAction action) =>
        action is StartupTaskAction.CreateWithTrigger or StartupTaskAction.ReRegisterWithTrigger;
}

/// <summary>通道操作的对外结果：意图是否生效、做了什么、失败原因（回退时给 UI 提示）。</summary>
public sealed record StartupOutcome(bool IntentApplied, StartupTaskAction Action, bool Ok, string? Message);

/// <summary>计划任务端口（Application 侧）：查询事实与幂等重注册；由 Infrastructure 以 schtasks 实现。</summary>
public interface IScheduledTaskPort
{
    /// <summary>查询任务事实；任务不存在返回 Exists=false。</summary>
    TaskFacts ReadState(string taskName);

    /// <summary>创建或整体替换任务定义（幂等）；成功返回 true。</summary>
    bool Register(string taskName, string exePath, bool withLogonTrigger);
}

/// <summary>
/// 静默启动通道编排（F36）：开关按三态落地，启动收敛按判定表执行。
/// 失败回退 = 意图不生效（调用方保持原快照），事实保持原样可重试。
/// </summary>
public sealed class StartupService(IScheduledTaskPort port, string taskName, string currentExePath,
    DiagnosticLog? diagnostics = null)
{
    private readonly DiagnosticLog _diagnostics = diagnostics ?? DiagnosticLog.None;
    /// <summary>托盘「开机启动」开关：按三态处理；ExecuteNow 失败时 IntentApplied=false（回退）。</summary>
    public StartupOutcome Toggle(bool isDevelopmentBuild, bool isElevated, bool newIntent)
    {
        var mode = StartupChannelRules.DecideToggle(isDevelopmentBuild, isElevated);
        _diagnostics.Verbose($"startup-toggle mode={mode} intent={newIntent}");
        if (mode != StartupToggleMode.ExecuteNow)
        {
            return new StartupOutcome(true, StartupTaskAction.None, Ok: true, null); // 只记意图/延后
        }
        return Execute(newIntent, deferOnFailure: false);
    }

    /// <summary>启动收敛：提权生产构建下把事实对齐到持久化意图；其余通道不动事实。</summary>
    public StartupOutcome ConvergeOnStartup(bool isDevelopmentBuild, bool isElevated, bool intent)
    {
        _diagnostics.Verbose($"startup-converge development={isDevelopmentBuild} elevated={isElevated} intent={intent}");
        if (isDevelopmentBuild || !isElevated)
        {
            return new StartupOutcome(true, StartupTaskAction.None, Ok: true, null);
        }
        return Execute(intent, deferOnFailure: true);
    }

    private StartupOutcome Execute(bool intent, bool deferOnFailure)
    {
        var action = StartupChannelRules.DecideConvergence(intent, port.ReadState(taskName), currentExePath);
        _diagnostics.Verbose($"startup-action action={action}");
        if (action == StartupTaskAction.None)
        {
            return new StartupOutcome(true, action, Ok: true, null);
        }
        if (port.Register(taskName, currentExePath, StartupChannelRules.WithLogonTrigger(action)))
        {
            _diagnostics.Verbose($"startup-register action={action} ok=True");
            return new StartupOutcome(true, action, Ok: true, null);
        }
        _diagnostics.Verbose($"startup-register action={action} ok=False");
        return new StartupOutcome(
            IntentApplied: deferOnFailure, // 启动收敛不回退持久化意图（下次启动再试）；开关路径回退
            Action: action,
            Ok: false,
            Message: "计划任务更新失败，开机启动可能不生效");
    }
}
