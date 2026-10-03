using System.Xml.Linq;

namespace ClipboardTool.Tests;

/// <summary>
/// 应用清单双形态（F35；工单目标一）：release requireAdministrator（直接运行即 UAC）、
/// debug asInvoker（开发/测试免提权）；两份都保留 PerMonitorV2 与 Common-Controls 6。
/// 以仓库源文件为断言对象：发布用哪份由 csproj 按配置选择，构建产物断言归真机验证。
/// </summary>
public class ManifestTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void 发布清单_requireAdministrator()
    {
        var level = ExecutionLevel(Path.Combine(RepoRoot, "ClipboardTool.Presentation.Wpf", "app.release.manifest"));
        Assert.Equal("requireAdministrator", level);
    }

    [Fact]
    public void 开发清单_asInvoker()
    {
        var level = ExecutionLevel(Path.Combine(RepoRoot, "ClipboardTool.Presentation.Wpf", "app.manifest"));
        Assert.Equal("asInvoker", level);
    }

    [Theory]
    [InlineData("app.manifest")]
    [InlineData("app.release.manifest")]
    public void 两份清单都保留PerMonitorV2与CommonControls6(string fileName)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot, "ClipboardTool.Presentation.Wpf", fileName));

        var dpi = doc.Descendants().Single(e => e.Name.LocalName == "dpiAwareness").Value;
        Assert.Equal("PerMonitorV2", dpi);

        var commonControls = doc.Descendants()
            .Where(e => e.Name.LocalName == "assemblyIdentity")
            .Select(e => (string?)e.Attribute("name"))
            .Any(name => name is not null && name.Contains("Common-Controls", StringComparison.OrdinalIgnoreCase));
        Assert.True(commonControls, $"{fileName} 缺 Common-Controls 6 依赖声明");
    }

    private static string ExecutionLevel(string path)
    {
        var doc = XDocument.Load(path);
        return (string)doc.Descendants()
            .Single(e => e.Name.LocalName == "requestedExecutionLevel")
            .Attribute("level")!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClipboardTool.sln")))
        {
            dir = dir.Parent!;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
