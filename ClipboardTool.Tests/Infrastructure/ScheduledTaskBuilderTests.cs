using System.Xml.Linq;
using ClipboardTool.Infrastructure.Windows;

namespace ClipboardTool.Tests.Infrastructure;

/// <summary>
/// 计划任务定义 XML 构造（F36；T08 把 T07 的 PowerShell 命令行构造升级为任务 XML）：
/// schtasks /Create /XML 直接可用。钉工单红绿三条：路径引号（XML 转义后可无损解析回读）、
/// 最高权限（RunLevel=HighestAvailable）、登录触发器开合。
/// 常驻应用附加纪律：ExecutionTimeLimit=PT0S（默认 72 小时上限会杀常驻进程）、电池条件全关。
/// </summary>
public class ScheduledTaskBuilderTests
{
    [Fact]
    public void 无登录触发器_仅静默拉起通道()
    {
        var xml = ScheduledTaskBuilder.BuildTaskXml(@"C:\Program Files\ClipboardTool\ClipboardTool.exe", withLogonTrigger: false);

        var doc = XDocument.Parse(xml);
        Assert.Null(doc.Descendants(doc.Root.nodeName("LogonTrigger")).FirstOrDefault());
        Assert.Equal("HighestAvailable", doc.Descendants(doc.Root.nodeName("RunLevel")).Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(doc.Root.nodeName("LogonType")).Single().Value);
        Assert.Equal(@"C:\Program Files\ClipboardTool\ClipboardTool.exe", CommandOf(doc));
    }

    [Fact]
    public void 带登录触发器_开机启动()
    {
        var xml = ScheduledTaskBuilder.BuildTaskXml(@"C:\App\Clip.exe", withLogonTrigger: true);

        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Descendants(doc.Root.nodeName("LogonTrigger")).FirstOrDefault());
        Assert.Equal(@"C:\App\Clip.exe", CommandOf(doc));
    }

    [Fact]
    public void 路径含引号与XML特殊字符_转义后无损回读()
    {
        // 文件名里的 ' 与 & 合法，" 虽非法但转义契约同样成立：都不得破坏 XML 结构
        const string path = @"C:\Users\o'brien & sons\odd""name\Clip.exe";

        var doc = XDocument.Parse(ScheduledTaskBuilder.BuildTaskXml(path, withLogonTrigger: false));

        Assert.Equal(path, CommandOf(doc));
    }

    [Fact]
    public void 空格路径不加引号_无参数场景Command即纯路径()
    {
        var xml = ScheduledTaskBuilder.BuildTaskXml(@"C:\Program Files\ClipboardTool\ClipboardTool.exe", withLogonTrigger: false);

        // Command 文本就是路径本身；带空格也不包引号（Exec 无 Arguments 时不需要）
        Assert.DoesNotContain("&quot;", xml);
        Assert.Contains("<Command>C:\\Program Files\\ClipboardTool\\ClipboardTool.exe</Command>", xml);
    }

    [Fact]
    public void 常驻应用不设时限_电池条件全关()
    {
        var xml = ScheduledTaskBuilder.BuildTaskXml(@"C:\App\Clip.exe", withLogonTrigger: true);

        var doc = XDocument.Parse(xml);
        Assert.Equal("PT0S", doc.Descendants(doc.Root.nodeName("ExecutionTimeLimit")).Single().Value);
        Assert.Equal("false", doc.Descendants(doc.Root.nodeName("DisallowStartIfOnBatteries")).Single().Value);
        Assert.Equal("false", doc.Descendants(doc.Root.nodeName("StopIfGoingOnBatteries")).Single().Value);
    }

    private static string CommandOf(XDocument doc) =>
        doc.Descendants(doc.Root.nodeName("Command")).Single().Value;
}

file static class TaskXmlExtensions
{
    /// <summary>任务 XML 命名空间下的元素名（mit/task）。</summary>
    public static XName nodeName(this XElement? root, string local) =>
        XName.Get(local, root?.Name.NamespaceName ?? "http://schemas.microsoft.com/windows/2004/02/mit/task");
}
