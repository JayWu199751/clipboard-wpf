using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Xml.Linq;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 计划任务定义 XML 构造（F36；T08 把 T07 的 PowerShell 命令行构造升级为任务 XML）。
/// 领域契约（ADR-0001）：计划任务 ClipboardToolElevated 是唯一的提权/静默拉起通道，
/// RunLevel=HighestAvailable、LogonType=InteractiveToken、按意图决定是否带登录触发器。
/// 为什么用 XML：schtasks /Create /XML 原样吃定义文件，路径里的引号/&amp; 等字符由 XML 转义
/// 收口（PowerShell 命令行拼接对特殊字符脆弱）；「仅静默拉起（无触发器）」也天然可表达。
/// 常驻应用附加纪律：ExecutionTimeLimit=PT0S（缺省 72 小时上限会杀常驻进程）、电池条件全关。
/// </summary>
public static class ScheduledTaskBuilder
{
    public const string DefaultTaskName = "ClipboardToolElevated";

    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>任务命名空间（Registrar 解析查询结果用同一常量）。</summary>
    internal static string Namespace => TaskNamespace;

    /// <summary>构造任务定义 XML（schtasks /Create /XML 与 Register-ScheduledTask -Xml 均可用）。</summary>
    public static string BuildTaskXml(string exePath, bool withLogonTrigger)
    {
        var trigger = withLogonTrigger ? "<LogonTrigger />" : string.Empty;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="{TaskNamespace}">
              <RegistrationInfo>
                <Description>ClipboardTool 提权静默启动通道（开机启动=登录触发器；无触发器=按需静默拉起）</Description>
              </RegistrationInfo>
              <Triggers>{trigger}</Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}

/// <summary>
/// 计划任务注册执行（F36）：schtasks 三件套——/Query /XML 读事实、/Create /XML /F 幂等替换、
/// （卸载另用 /Delete，归安装包与人工步骤）。查询失败按缺席处理：缺席在判定表里只会引出
/// 重建动作，重建失败再如实报错，查询误判不会被放大成错误事实。
/// </summary>
public sealed class ScheduledTaskRegistrar : IScheduledTaskPort
{
    public TaskFacts ReadState(string taskName)
    {
        var psi = new ProcessStartInfo("schtasks");
        psi.ArgumentList.Add("/Query");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(taskName);
        psi.ArgumentList.Add("/XML");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.Unicode; // schtasks /XML 输出 UTF-16
        using var process = Process.Start(psi);
        if (process is null)
        {
            return new TaskFacts(false, false, string.Empty);
        }
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !stdout.Contains("<Task "))
        {
            return new TaskFacts(false, false, string.Empty); // 任务不存在（或定义不可读）→ 按缺席收敛
        }
        var doc = XDocument.Parse(stdout);
        var ns = doc.Root?.Name.NamespaceName ?? ScheduledTaskBuilder.Namespace;
        return new TaskFacts(
            Exists: true,
            HasLogonTrigger: doc.Descendants(XName.Get("LogonTrigger", ns)).Any(),
            ExePath: doc.Descendants(XName.Get("Command", ns)).FirstOrDefault()?.Value ?? string.Empty);
    }

    public bool Register(string taskName, string exePath, bool withLogonTrigger)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"ClipboardTool-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, ScheduledTaskBuilder.BuildTaskXml(exePath, withLogonTrigger), Encoding.Unicode);
            var psi = new ProcessStartInfo("schtasks");
            psi.ArgumentList.Add("/Create");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);
            psi.ArgumentList.Add("/XML");
            psi.ArgumentList.Add(xmlPath);
            psi.ArgumentList.Add("/F");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException)
            {
                // 临时 XML 清理失败无害（%TEMP% 内）
            }
        }
    }
}
