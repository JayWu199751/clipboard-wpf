using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 计划任务注册命令构造（F35 雏形；legacy tasks.rs ps_register_task 移植）：
/// schtasks /create 无法表达「仅作静默拉起通道（无触发器）」，所以走 PowerShell 的
/// Register-ScheduledTask。T07 只落命令行构造的纯函数与转义；注册/提权/重建舞步归 T08。
/// </summary>
public class ScheduledTaskBuilderTests
{
    [Fact]
    public void 无开机触发器_仅静默拉起通道()
    {
        var command = ScheduledTaskBuilder.BuildRegisterCommand(@"C:\Program Files\ClipboardTool\ClipboardTool.exe", "ClipboardToolElevated", withLogonTrigger: false);

        Assert.Contains("New-ScheduledTaskAction -Execute 'C:\\Program Files\\ClipboardTool\\ClipboardTool.exe'", command);
        Assert.Contains("New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest", command);
        Assert.Contains("Register-ScheduledTask -TaskName 'ClipboardToolElevated'", command);
        Assert.Contains("-Force -ErrorAction Stop", command);
        Assert.DoesNotContain("New-ScheduledTaskTrigger", command);
        Assert.EndsWith("exit 0 } catch { Write-Error $_.Exception.Message; exit 1 }", command);
    }

    [Fact]
    public void 带开机触发器_登录触发器登录触发()
    {
        var command = ScheduledTaskBuilder.BuildRegisterCommand(@"C:\App\Clip.exe", "ClipboardToolElevated", withLogonTrigger: true);

        Assert.Contains("-Trigger (New-ScheduledTaskTrigger -AtLogOn)", command);
    }

    [Fact]
    public void 路径含单引号时转义_命令不被注入()
    {
        // 单引号串里的 ' 按 PowerShell 语义要写成 ''，否则路径即注入口
        var command = ScheduledTaskBuilder.BuildRegisterCommand(@"C:\Users\o'brien\Clip.exe", "ClipboardToolElevated", withLogonTrigger: false);

        Assert.Contains(@"C:\Users\o''brien\Clip.exe", command);
        Assert.DoesNotContain(@"o'brien\Clip.exe'", command.Replace("''", "\0"));
    }

    [Fact]
    public void 任务名含单引号同样转义()
    {
        var command = ScheduledTaskBuilder.BuildRegisterCommand(@"C:\App\Clip.exe", "Clip'Tool", withLogonTrigger: false);

        Assert.Contains("TaskName 'Clip''Tool'", command);
    }
}
