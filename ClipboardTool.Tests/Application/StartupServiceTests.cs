using ClipboardTool.Application;
using Xunit;

namespace ClipboardTool.Tests.Application;

/// <summary>
/// StartupService 编排（F36）：三态开关如何落到端口上、失败如何回退意图。
/// 计划任务端口用假件：ReadState 返回预设事实，Register 记录调用并可预设成败。
/// </summary>
public class StartupServiceTests
{
    private const string TaskName = "ClipboardToolElevated";
    private const string ExePath = @"C:\Program Files\ClipboardTool\ClipboardTool.exe";

    private sealed class FakePort : IScheduledTaskPort
    {
        public TaskFacts Facts { get; set; } = new(false, false, "");
        public bool NextRegisterResult { get; set; } = true;
        public List<(string TaskName, string ExePath, bool Trigger)> Registered { get; } = [];

        public TaskFacts ReadState(string taskName) => Facts;

        public bool Register(string taskName, string exePath, bool withLogonTrigger)
        {
            Registered.Add((taskName, exePath, withLogonTrigger));
            return NextRegisterResult;
        }
    }

    [Fact]
    public void 开关_开发构建_只翻意图_不触碰任务端口()
    {
        var port = new FakePort();
        var service = new StartupService(port, TaskName, ExePath);

        var outcome = service.Toggle(isDevelopmentBuild: true, isElevated: false, newIntent: true);

        Assert.True(outcome.IntentApplied);
        Assert.Empty(port.Registered);
    }

    [Fact]
    public void 开关_未提权_只翻意图_延后到下次提权启动()
    {
        var port = new FakePort();
        var service = new StartupService(port, TaskName, ExePath);

        var outcome = service.Toggle(isDevelopmentBuild: false, isElevated: false, newIntent: true);

        Assert.True(outcome.IntentApplied);
        Assert.Empty(port.Registered);
    }

    [Fact]
    public void 开关_已提权_注册成功_意图生效()
    {
        var port = new FakePort { Facts = new TaskFacts(false, false, "") };
        var service = new StartupService(port, TaskName, ExePath);

        var outcome = service.Toggle(isDevelopmentBuild: false, isElevated: true, newIntent: true);

        Assert.True(outcome.IntentApplied);
        var call = Assert.Single(port.Registered);
        Assert.Equal(TaskName, call.TaskName);
        Assert.Equal(ExePath, call.ExePath);
        Assert.True(call.Trigger);
    }

    [Fact]
    public void 开关_已提权_注册失败_回退意图不生效()
    {
        var port = new FakePort { NextRegisterResult = false };
        var service = new StartupService(port, TaskName, ExePath);

        var outcome = service.Toggle(isDevelopmentBuild: false, isElevated: true, newIntent: true);

        Assert.False(outcome.IntentApplied); // 回退：调用方不翻转设置快照
        Assert.NotEmpty(port.Registered);
    }

    [Fact]
    public void 开关_已提权_事实已对齐意图_不重复注册()
    {
        var port = new FakePort { Facts = new TaskFacts(true, true, ExePath) };
        var service = new StartupService(port, TaskName, ExePath);

        var outcome = service.Toggle(isDevelopmentBuild: false, isElevated: true, newIntent: true);

        Assert.True(outcome.IntentApplied);
        Assert.Empty(port.Registered);
    }

    [Fact]
    public void 启动收敛_开发构建_不触碰任务端口()
    {
        var port = new FakePort { Facts = new TaskFacts(false, false, "") };
        var service = new StartupService(port, TaskName, ExePath);

        service.ConvergeOnStartup(isDevelopmentBuild: true, isElevated: true, intent: true);

        Assert.Empty(port.Registered);
    }

    [Fact]
    public void 启动收敛_未提权_不触碰任务端口_等下次提权启动()
    {
        var port = new FakePort { Facts = new TaskFacts(false, false, "") };
        var service = new StartupService(port, TaskName, ExePath);

        service.ConvergeOnStartup(isDevelopmentBuild: false, isElevated: false, intent: true);

        Assert.Empty(port.Registered);
    }

    [Fact]
    public void 启动收敛_已提权_按判定表执行()
    {
        var port = new FakePort { Facts = new TaskFacts(true, false, ExePath) };
        var service = new StartupService(port, TaskName, ExePath);

        var outcome = service.ConvergeOnStartup(isDevelopmentBuild: false, isElevated: true, intent: true);

        Assert.True(outcome.Ok);
        var call = Assert.Single(port.Registered);
        Assert.True(call.Trigger);
    }

    [Fact]
    public void 启动收敛_注册失败_不阻断启动_报告失败()
    {
        var port = new FakePort { Facts = new TaskFacts(false, false, ""), NextRegisterResult = false };
        var service = new StartupService(port, TaskName, ExePath);

        var outcome = service.ConvergeOnStartup(isDevelopmentBuild: false, isElevated: true, intent: true);

        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Message);
    }
}
