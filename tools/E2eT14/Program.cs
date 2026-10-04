// 票 14（F06 来源应用接线）真机端到端验证（E2eT09 手法：ForceForeground + 存档备份恢复 + 零残留）。
// 复制来源 = 驱动自带的 WPF 前台窗口（E2eT14.exe 本进程）：本机 notepad.exe 是商店版无窗存根，
// 起不了经典窗口；自带窗口让「前台窗口 → 来源进程」归因完全确定性（AppName 期望 = E2eT14）。
// 链路：起空档应用 → 驱动窗口置前台 → SelectAll+Copy（真实剪贴板写入）→ 轮询存档断言：
//   ① 条目 SourceApp.AppName == E2eT14（ExePath/WindowTitle 同验）；
//   ② IconDataUrl 为合法 PNG data URL（ExtractAssociatedIcon 提取链路真机走通）；
//   ③ 同进程第二次复制（图标缓存命中路径）仍采集到来源。
// 来源名参与搜索匹配（F22）由 SearchRulesTests 单测覆盖，此处不重复。
// 结束还原存档、清理进程，零残留。

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

var failures = new List<string>();
var notes = new List<string>();
var passCount = 0;

void Check(bool ok, string name, string detail = "")
{
    if (ok)
    {
        passCount++;
        Console.WriteLine($"PASS {name}{(detail.Length == 0 ? "" : " ｜ " + detail)}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"FAIL {name}{(detail.Length == 0 ? "" : " ｜ " + detail)}");
    }
}

var repoRoot = FindRepoRoot();
var logPath = Path.Combine(repoRoot, "tools", "E2eT14", "run-output.txt");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
Console.SetOut(new StreamWriter(logPath, append: false) { AutoFlush = true });
Console.WriteLine($"E2eT14 运行于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool");
var historyPath = Path.Combine(dataDir, "clipboard-history.json");
var historyBackup = File.Exists(historyPath) ? File.ReadAllText(historyPath) : null;

if (Process.GetProcessesByName("ClipboardTool").Length > 0)
{
    Console.WriteLine("已存在 ClipboardTool 运行实例（可能为安装版），为不干扰真实使用中止 E2E。");
    return 2;
}

_ = Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

var app = (Process?)null;
Dispatcher? sourceDispatcher = null;
TextBox? sourceBox = null;
var windowClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

try
{
    // 空档启动：备份真实存档后移除，本会话产生的条目即唯一内容
    Directory.CreateDirectory(dataDir);
    File.Delete(historyPath);

    app = Process.Start(new ProcessStartInfo(FindAppExe()) { UseShellExecute = false })!;
    Thread.Sleep(2500);
    Check(app.HasExited == false, "被测应用启动且存活");

    // 标记含毫秒，跨运行不与旧档撞车；两个标记对应两次复制
    var stamp = DateTime.Now.ToString("HHmmssfff");
    var marker1 = $"E2ET14-F06-source-1-{stamp}";
    var marker2 = $"E2ET14-F06-source-2-{stamp}";

    // —— 驱动自带 STA 来源窗口（复制来源进程 = 本驱动） ——
    var hwndTcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sta = new Thread(() =>
    {
        var window = new Window
        {
            Width = 460,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Title = "E2eT14 来源窗口",
        };
        sourceBox = new TextBox { Text = marker1, FontSize = 14, Margin = new Thickness(12) };
        window.Content = sourceBox;
        window.Loaded += (_, _) =>
        {
            sourceDispatcher = Dispatcher.CurrentDispatcher;
            hwndTcs.TrySetResult(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        };
        window.Closed += (_, _) => windowClosed.TrySetResult(true);
        window.Show();
        Dispatcher.Run();
    })
    { IsBackground = true, Name = "E2eT14SourceWindow" };
    sta.SetApartmentState(ApartmentState.STA);
    sta.Start();
    var sourceHwnd = hwndTcs.Task.GetAwaiter().GetResult();
    Thread.Sleep(300);

    Native.ForceForeground(sourceHwnd);
    Thread.Sleep(200);
    Check(Native.GetForegroundWindow() == sourceHwnd, "驱动窗口为前台窗口（来源判定前提）");

    // 第一次复制：全选 + 复制（真实剪贴板写入，前台即本驱动进程）
    sourceDispatcher!.Invoke(() =>
    {
        sourceBox!.SelectAll();
        sourceBox.Copy();
    });

    var entry1 = WaitEntry(historyPath, marker1, TimeSpan.FromSeconds(15));
    Check(entry1 is not null, "复制后条目落档");
    if (entry1 is not null)
    {
        var source = entry1.Value.GetProperty("sourceApp");
        Check(source.ValueKind == JsonValueKind.Object,
            "文字条目携带 SourceApp",
            source.ValueKind == JsonValueKind.Object ? "" : $"实际 {source.ValueKind}");
        if (source.ValueKind == JsonValueKind.Object)
        {
            var appName = source.GetProperty("appName").GetString() ?? "";
            var exePath = source.GetProperty("exePath").GetString() ?? "";
            var title = source.GetProperty("windowTitle").GetString() ?? "";
            Check(appName.Equals("E2eT14", StringComparison.OrdinalIgnoreCase),
                "AppName == 驱动进程名（前台归因正确）", $"实际「{appName}」");
            Check(exePath.EndsWith("E2eT14.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exePath),
                "ExePath 真实存在", $"实际「{exePath}」");
            Check(title.Contains("E2eT14"), "WindowTitle 非空且含窗口标题", $"实际「{title}」");

            var icon = source.TryGetProperty("iconDataUrl", out var iconProp)
                ? iconProp.GetString()
                : null;
            Check(icon is not null && icon.StartsWith("data:image/png;base64,", StringComparison.Ordinal),
                "IconDataUrl 为 PNG data URL（图标提取链路走通）");
            if (icon is not null && icon.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
            {
                try
                {
                    var png = Convert.FromBase64String(icon["data:image/png;base64,".Length..]);
                    Check(png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
                        "图标字节为合法 PNG 签名", $"{png.Length} bytes");
                }
                catch (Exception ex)
                {
                    Check(false, "图标字节为合法 PNG 签名", ex.Message);
                }
            }
        }
    }

    // 第二次复制：同进程换文本再复制（同 exe 第二次采集走图标缓存命中路径）
    sourceDispatcher!.Invoke(() =>
    {
        sourceBox!.Text = marker2;
        sourceBox.SelectAll();
        sourceBox.Copy();
    });

    var entry2 = WaitEntry(historyPath, marker2, TimeSpan.FromSeconds(15));
    Check(entry2 is not null, "第二次复制条目落档");
    if (entry2 is not null)
    {
        var appName2 = entry2.Value.TryGetProperty("sourceApp", out var s2) && s2.ValueKind == JsonValueKind.Object
            ? s2.GetProperty("appName").GetString()
            : null;
        Check(appName2 is not null && appName2.Equals("E2eT14", StringComparison.OrdinalIgnoreCase),
            "第二次复制仍采集到来源（缓存路径不破坏采集）");
    }

    notes.Add("F22 来源名搜索匹配由 SearchRulesTests 单测覆盖（AppName/WindowTitle/ExePath 三字段在列）");
}
finally
{
    try
    {
        sourceDispatcher?.InvokeShutdown();
        windowClosed.Task.Wait(2000);
    }
    catch (Exception ex)
    {
        notes.Add("来源窗口关闭失败：" + ex.Message);
    }
    foreach (var p in Process.GetProcessesByName("ClipboardTool"))
    {
        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            notes.Add("ClipboardTool 进程已清理");
        }
        catch (Exception ex)
        {
            notes.Add("ClipboardTool 清理失败：" + ex.Message);
        }
    }

    // 还原真实存档
    try
    {
        if (historyBackup is not null)
        {
            File.WriteAllText(historyPath, historyBackup);
            notes.Add("真实存档已原样恢复");
        }
        else if (File.Exists(historyPath))
        {
            File.Delete(historyPath);
            notes.Add("测试前无存档，测试档案已移除");
        }
    }
    catch (Exception ex)
    {
        notes.Add("存档还原失败：" + ex.Message);
    }
}

foreach (var note in notes)
{
    Console.WriteLine("NOTE " + note);
}
Console.WriteLine($"通过 {passCount} 项，失败 {failures.Count} 项");
if (failures.Count > 0)
{
    Console.WriteLine("FAILED: " + string.Join(" / ", failures));
    return 1;
}
Console.WriteLine("E2eT14 全部通过");
return 0;

// ---------- 助手 ----------

static string FindRepoRoot()
{
    for (var dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
    {
        if (File.Exists(Path.Combine(dir, "ClipboardTool.sln")))
        {
            return dir;
        }
    }
    throw new InvalidOperationException("未找到仓库根（ClipboardTool.sln）");
}

static string FindAppExe()
{
    var candidates = new[]
    {
        "ClipboardTool.Presentation.Wpf/bin/Debug/net10.0-windows/ClipboardTool.exe",
        "ClipboardTool.Presentation.Wpf/bin/Release/net10.0-windows/ClipboardTool.exe",
    };
    for (var dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
    {
        foreach (var candidate in candidates)
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full))
            {
                return Path.GetFullPath(full);
            }
        }
    }
    throw new FileNotFoundException("未找到被测应用 exe（先 dotnet build）");
}

/// <summary>轮询存档直到出现指定正文文字的条目（落档有异步延迟：通知 → 读 → 落库 → 落盘）。</summary>
static JsonElement? WaitEntry(string historyPath, string text, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (File.Exists(historyPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(historyPath));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.TryGetProperty("text", out var t) && t.GetString() == text)
                    {
                        return e.Clone();
                    }
                }
            }
            catch (IOException) { /* 落盘中，稍后再读 */ }
            catch (JsonException) { }
        }
        Thread.Sleep(300);
    }
    return null;
}

// ---------- Win32（E2eT09 同款前台获取） ----------

internal static class Native
{
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkMenu = 0x12;

    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] internal static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>前台锁规避（E2eT09 同款）：先发一次 Alt 抬落解锁，再 SetForegroundWindow。</summary>
    internal static void ForceForeground(IntPtr hwnd)
    {
        _ = SendAltKey();
        _ = SetForegroundWindow(hwnd);
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            if (GetForegroundWindow() == hwnd)
            {
                return;
            }
            _ = SendAltKey();
            _ = SetForegroundWindow(hwnd);
            Thread.Sleep(120);
        }
    }

    private static bool SendAltKey()
    {
        var keyDown = KeyInput(VkMenu, 0);
        var keyUp = KeyInput(VkMenu, KeyEventKeyUp);
        var size = Marshal.SizeOf<INPUT>();
        _ = SendInput(1, [keyDown], size);
        _ = SendInput(1, [keyUp], size);
        return true;
    }

    internal static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero },
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public HARDWAREINPUT hi;
    }
}
