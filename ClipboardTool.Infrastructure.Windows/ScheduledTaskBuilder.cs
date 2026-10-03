namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 计划任务注册命令构造（F35 雏形；legacy tasks.rs ps_register_task 移植）。
/// 领域契约（ADR-0001）：计划任务 ClipboardToolElevated 是唯一的提权/静默拉起通道，
/// RunLevel=Highest、LogonType=Interactive、按意图决定是否带登录触发器。
/// 为什么不用 schtasks /create：它强制 /sc 触发器，表达不出「仅静默拉起（无触发器）」。
/// T07 只落命令行构造（纯函数 + 单引号转义）；注册执行、提权自检与重建舞步归 T08。
/// </summary>
public static class ScheduledTaskBuilder
{
    public const string DefaultTaskName = "ClipboardToolElevated";

    /// <summary>构造 Register-ScheduledTask 的 PowerShell 命令行；withLogonTrigger=开机启动。</summary>
    public static string BuildRegisterCommand(string exePath, string taskName, bool withLogonTrigger)
    {
        var trigger = withLogonTrigger ? " -Trigger (New-ScheduledTaskTrigger -AtLogOn)" : string.Empty;
        return "try { "
            + $"$action = New-ScheduledTaskAction -Execute '{Escape(exePath)}'; "
            + "$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME "
            + "-LogonType Interactive -RunLevel Highest; "
            + $"Register-ScheduledTask -TaskName '{Escape(taskName)}' -Action $action "
            + $"-Principal $principal{trigger} -Force -ErrorAction Stop | Out-Null; exit 0 "
            + "} catch { Write-Error $_.Exception.Message; exit 1 }";
    }

    /// <summary>单引号串内的 ' 按 PowerShell 语义翻倍：路径/任务名里的引号不能变成命令边界。</summary>
    private static string Escape(string value) => value.Replace("'", "''");
}
