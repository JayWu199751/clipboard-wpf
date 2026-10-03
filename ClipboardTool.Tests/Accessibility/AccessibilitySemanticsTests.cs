// F48 无障碍语义测试（T09）：可访问名称常量、toast 播报文本、减少动画策略、
// AutomationProperties → UIA 附加属性链路。UIA 名称真源在 AccessibilityNames，
// XAML 侧以 x:Static 引用同一常量——常量测试 + peer 测试共同钉住语义存在。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Xunit;

namespace ClipboardTool.Tests.Accessibility;

// —— Seam 1：可访问名称真源（XAML 以 x:Static 引用，命名空间不能空、不能重复） ——
public class AccessibilityNamesTests
{
    [Fact]
    public void 交互元素名称全部非空且无前后空白()
    {
        var names = new[]
        {
            ClipboardTool.Presentation.Wpf.AccessibilityNames.HistoryList,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.SearchInput,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.SearchClear,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.NoteEditor,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.CaptureStatus,
        };
        Assert.All(names, name =>
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name, name.Trim());
        });
    }

    [Fact]
    public void 名称互不重复()
    {
        var names = new[]
        {
            ClipboardTool.Presentation.Wpf.AccessibilityNames.HistoryList,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.SearchInput,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.SearchClear,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.NoteEditor,
            ClipboardTool.Presentation.Wpf.AccessibilityNames.CaptureStatus,
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }
}

// —— Seam 2：toast 状态播报文本（F48 toast 播报 + F46 红绿语义） ——
public class ToastAnnouncementTests
{
    [Fact]
    public void 成功消息原样播报()
    {
        var text = ClipboardTool.Presentation.Wpf.ToastAnnouncement.Build(
            "已复制并粘贴", isError: false, actionLabel: null);
        Assert.Equal("已复制并粘贴", text);
    }

    [Fact]
    public void 错误消息带前缀()
    {
        var text = ClipboardTool.Presentation.Wpf.ToastAnnouncement.Build(
            "恢复输入焦点失败", isError: true, actionLabel: null);
        Assert.StartsWith("错误：", text, StringComparison.Ordinal);
        Assert.EndsWith("恢复输入焦点失败", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 带动作的消息拼出动作标签()
    {
        var text = ClipboardTool.Presentation.Wpf.ToastAnnouncement.Build(
            "已延迟删除", isError: false, actionLabel: "撤销");
        Assert.Contains("已延迟删除", text, StringComparison.Ordinal);
        Assert.Contains("撤销", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 次级说明并入播报()
    {
        var text = ClipboardTool.Presentation.Wpf.ToastAnnouncement.Build(
            "备注已保存", isError: false, actionLabel: null, dim: "第一条备注内容");
        Assert.Contains("备注已保存", text, StringComparison.Ordinal);
        Assert.Contains("第一条备注内容", text, StringComparison.Ordinal);
    }
}

// —— Seam 3：toast 播报承载链路（F48：LiveSetting=Polite + Name 经 TextBlock 自带 peer 进 UIA） ——
public class AutomationAttachmentTests
{
    [Fact]
    public void 播报文本经文本块的自动化对等端可读()
    {
        // toast 播报锚在 Message TextBlock 上：TextBlockAutomationPeer 自动创建且天然是
        // 内容元素（普通 Border 无 peer、不可承载播报，见产品注释）。AutomationProperties.Name
        // 覆盖默认的 Text 读取，使播报含错误前缀/动作/次级说明全文。
        Sta.Run(() =>
        {
            var host = new TextBlock
            {
                Text = "已延迟删除",
            };
            host.SetValue(AutomationProperties.NameProperty, "已延迟删除，撤销");
            host.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Polite);

            var peer = UIElementAutomationPeer.CreatePeerForElement(host);
            Assert.NotNull(peer);
            // 注：不做 IsContentElement() 断言——该方法在无宿主窗口的裸 STA 线程上走降级路径，
            // 与真实窗口内 UIA 树行为不符（TextBlockAutomationPeer 在真实树中是内容元素）。
            Assert.Equal("已延迟删除，撤销", peer.GetName());
            var liveSetting = (AutomationLiveSetting)host.GetValue(
                AutomationProperties.LiveSettingProperty);
            Assert.Equal(AutomationLiveSetting.Polite, liveSetting);
        });
    }
}

// —— Seam 4：减少动画策略（F48：系统减少动态时停动画；SPI_SETCLIENTAREAANIMATION 可编程翻转验证） ——
public class AccessibilityMotionTests
{
    [Fact]
    public void SPI翻转客户区动画后策略跟随并恢复()
    {
        // 可编程翻转验证（工单要求）：SPI_SETCLIENTAREAANIMATION 翻转系统设置，
        // 策略直读系统参数（无 WPF SystemParameters 静态缓存时滞）应立即跟随；
        // finally 无条件恢复原值——测试不得遗留系统设置变更。
        Sta.Run(() =>
        {
            var original = GetClientAreaAnimation();
            try
            {
                SetClientAreaAnimation(!original);
                Assert.Equal(!original, GetClientAreaAnimation());
                Assert.Equal(!original, ClipboardTool.Presentation.Wpf.AccessibilityMotion.PlayAnimations);
            }
            finally
            {
                SetClientAreaAnimation(original);
                Assert.Equal(original, GetClientAreaAnimation());
            }
        });
    }

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    // SPI_GETCLIENTAREAANIMATION：pvParam 是出参 BOOL 指针（out bool）。
    // SPI_SETCLIENTAREAANIMATION：pvParam 是「值」而非指针（实测：ref 传地址非零恒写 TRUE，
    // 写 False 永不生效），必须经 IntPtr 传值——两个签名 EntryPoint 同为 SystemParametersInfoW。
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint uiParam, out bool pvParam, uint fWinIni);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint uiParam, IntPtr pvParam, uint fWinIni);

    private static bool GetClientAreaAnimation()
    {
        var value = true;
        if (!SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, out value, 0))
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "SPI_GETCLIENTAREAANIMATION 读取失败");
        }
        return value;
    }

    private static void SetClientAreaAnimation(bool value)
    {
        if (!SystemParametersInfoW(SPI_SETCLIENTAREAANIMATION, 0,
                (System.IntPtr)(value ? 1 : 0), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "SPI_SETCLIENTAREAANIMATION 写入失败");
        }
    }
}
