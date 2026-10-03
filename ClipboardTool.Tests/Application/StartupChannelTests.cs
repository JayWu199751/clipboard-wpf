using ClipboardTool.Application;
using Xunit;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// 静默启动通道的三态与判定表（F36；工单红绿一、二）：
/// autoStart 是持久化意图，计划任务与触发器是事实。开关三态：
/// 开发构建只记意图、未提权延后到下次提权启动、已提权立即重建事实（失败回退意图）。
/// 判定表：提权生产构建下，意图 × 任务事实 × exe 路径 → 收敛动作（升级=路径不一致重注册）。
/// </summary>
public class StartupChannelTests
{
    // —— 红绿一：开关三态 ——

    [Theory]
    [InlineData(true, false, StartupToggleMode.IntentOnly)]   // 开发构建（无论提权与否）
    [InlineData(true, true, StartupToggleMode.IntentOnly)]
    [InlineData(false, false, StartupToggleMode.Defer)]       // 未提权：延后
    [InlineData(false, true, StartupToggleMode.ExecuteNow)]   // 已提权：立即执行
    public void 开关三态_开发只记意图_未提权延后_已提权执行(bool isDev, bool isElevated, StartupToggleMode expected)
    {
        Assert.Equal(expected, StartupChannelRules.DecideToggle(isDev, isElevated));
    }

    // —— 红绿二：判定表（intent × factual × 路径）——

    private const string CurrentExe = @"C:\Program Files\ClipboardTool\ClipboardTool.exe";
    private const string OldExe = @"C:\Program Files\ClipboardTool\old\ClipboardTool.exe";

    [Fact]
    public void 判定表_意图开_无任务_创建带触发器()
    {
        Assert.Equal(StartupTaskAction.CreateWithTrigger,
            StartupChannelRules.DecideConvergence(intent: true, new TaskFacts(false, false, ""), CurrentExe));
    }

    [Fact]
    public void 判定表_意图开_任务无触发器_补触发器()
    {
        Assert.Equal(StartupTaskAction.ReRegisterWithTrigger,
            StartupChannelRules.DecideConvergence(true, new TaskFacts(true, false, CurrentExe), CurrentExe));
    }

    [Fact]
    public void 判定表_意图开_任务带触发器_路径一致_不动()
    {
        Assert.Equal(StartupTaskAction.None,
            StartupChannelRules.DecideConvergence(true, new TaskFacts(true, true, CurrentExe), CurrentExe));
    }

    [Fact]
    public void 判定表_意图开_任务带触发器_路径过期_升级重注册()
    {
        Assert.Equal(StartupTaskAction.ReRegisterWithTrigger,
            StartupChannelRules.DecideConvergence(true, new TaskFacts(true, true, OldExe), CurrentExe));
    }

    [Fact]
    public void 判定表_意图关_无任务_创建不带触发器_保留静默按需通道()
    {
        Assert.Equal(StartupTaskAction.CreateWithoutTrigger,
            StartupChannelRules.DecideConvergence(false, new TaskFacts(false, false, ""), CurrentExe));
    }

    [Fact]
    public void 判定表_意图关_任务无触发器_路径一致_不动()
    {
        Assert.Equal(StartupTaskAction.None,
            StartupChannelRules.DecideConvergence(false, new TaskFacts(true, false, CurrentExe), CurrentExe));
    }

    [Fact]
    public void 判定表_意图关_任务无触发器_路径过期_重注册更新路径()
    {
        Assert.Equal(StartupTaskAction.ReRegisterWithoutTrigger,
            StartupChannelRules.DecideConvergence(false, new TaskFacts(true, false, OldExe), CurrentExe));
    }

    [Fact]
    public void 判定表_意图关_任务带触发器_移触发器保留任务()
    {
        Assert.Equal(StartupTaskAction.ReRegisterWithoutTrigger,
            StartupChannelRules.DecideConvergence(false, new TaskFacts(true, true, CurrentExe), CurrentExe));
    }

    [Fact]
    public void 判定表_意图关_任务带触发器且路径过期_移触发器同时更新路径()
    {
        Assert.Equal(StartupTaskAction.ReRegisterWithoutTrigger,
            StartupChannelRules.DecideConvergence(false, new TaskFacts(true, true, OldExe), CurrentExe));
    }

    [Fact]
    public void 判定表_带触发器动作映射_注册时带登录触发器_否则不带()
    {
        Assert.True(StartupChannelRules.WithLogonTrigger(StartupTaskAction.CreateWithTrigger));
        Assert.True(StartupChannelRules.WithLogonTrigger(StartupTaskAction.ReRegisterWithTrigger));
        Assert.False(StartupChannelRules.WithLogonTrigger(StartupTaskAction.CreateWithoutTrigger));
        Assert.False(StartupChannelRules.WithLogonTrigger(StartupTaskAction.ReRegisterWithoutTrigger));
        Assert.False(StartupChannelRules.WithLogonTrigger(StartupTaskAction.None));
    }
}
