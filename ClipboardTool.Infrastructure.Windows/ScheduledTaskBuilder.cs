using System.Runtime.InteropServices;
using System.Security;
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
/// 计划任务注册执行（F36）：Task Scheduler COM API——RegisterTask(TASK_CREATE_OR_UPDATE)
/// 幂等替换定义；GetFolder().GetTask 读事实（缺席抛 0x80070002，按缺席收敛）。
/// 为什么不用 schtasks：其 /Query /XML 的 stdout 编码随机器代码页漂移（本机实测恒解码失败，
/// 把在册任务误判成缺席）；COM 直连无编码坑、免临时文件、免进程拉起。卸载删除归 NSIS 卸载器。
/// </summary>
public sealed class ScheduledTaskRegistrar : IScheduledTaskPort
{
    // COM 常量（taskschd.h）：TASK_CREATE_OR_UPDATE、TASK_LOGON_INTERACTIVE_TOKEN
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;

    public TaskFacts ReadState(string taskName)
    {
        try
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            svc.Connect();
            var task = svc.GetFolder("\\").GetTask(taskName); // 缺席：COMException 0x80070002
            var doc = XDocument.Parse((string)task.Xml);
            var ns = doc.Root?.Name.NamespaceName ?? ScheduledTaskBuilder.Namespace;
            return new TaskFacts(
                Exists: true,
                HasLogonTrigger: doc.Descendants(XName.Get("LogonTrigger", ns)).Any(),
                ExePath: doc.Descendants(XName.Get("Command", ns)).FirstOrDefault()?.Value ?? string.Empty);
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            return new TaskFacts(false, false, string.Empty); // 任务不存在 → 按缺席收敛
        }
        catch (Exception)
        {
            // 事实不可读（COM 不可用等）也按缺席收敛：判定表最多引出一次幂等重建，重建失败如实报错
            return new TaskFacts(false, false, string.Empty);
        }
    }

    public bool Register(string taskName, string exePath, bool withLogonTrigger)
    {
        try
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            svc.Connect();
            svc.GetFolder("\\").RegisterTask(
                taskName,
                ScheduledTaskBuilder.BuildTaskXml(exePath, withLogonTrigger),
                TaskCreateOrUpdate,
                null, null,
                TaskLogonInteractiveToken,
                null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
