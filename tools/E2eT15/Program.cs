using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipboardTool.Application;
using ClipboardTool.Domain.Settings;
using ClipboardTool.Infrastructure.Windows;
using ClipboardTool.Presentation.Wpf;

internal static class Program
{
    private const string PrivateMarker = "private-clipboard-must-not-appear";
    private static readonly Dictionary<int, Task<string>> ChildErrors = [];

    [STAThread]
    public static int Main(string[] args)
    {
#if !DEBUG
        throw new InvalidOperationException("E2E 仅允许 Debug，避免修改正式计划任务。");
#else
        SetErrorMode(0x0001 | 0x0002); // 故意崩溃的隔离子进程不弹系统错误对话框。
        _ = WerSetFlags(0x20); // WER_FAULT_REPORTING_NO_UI，仅影响本验证进程及其子进程各自设置。
        if (args is ["--child", var directory, var scope, var scenario])
        {
            return RunChild(directory, scope, scenario);
        }
        if (args is ["--writer", var writerDirectory, var writer])
        {
            var log = new DiagnosticLog(new FileDiagnosticSink(writerDirectory), null, Environment.ProcessId);
            for (var i = 0; i < 40; i++) log.Vital($"writer={writer} item={i}");
            return 0;
        }
        return RunSuite(args is ["--evidence", var evidence] ? Path.GetFullPath(evidence) : null);
#endif
    }

    private static int RunSuite(string? evidenceDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "clipboard-e2e-t15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var foreground = GetForegroundWindow();
        try
        {
            foreach (var verbose in new[] { false, true })
            {
                var dir = Path.Combine(root, verbose ? "verbose" : "default");
                var scope = "-t15-" + Guid.NewGuid().ToString("N");
                Seed(dir);
                using var process = StartChild(dir, scope, "normal", verbose);
                Check(Wait(process) == 0, $"真实 App 启动与交互退出（verbose={verbose}）");
                var log = File.ReadAllText(Path.Combine(dir, "diag.log"));
                foreach (var marker in new[]
                {
                    "start ui=wpf", "window-ready ui=wpf", "wpf-ui-ready", "warmup ui=wpf docked=True", "tray-ready added=True",
                    "hotkey_register accel=", "summon-req src=hotkey", "summon-run src=hotkey latency_ms=",
                    "summon-req src=tray-click", "summon-req src=tray-menu", "summon-req src=instance",
                    "summon-landed first=True final=True", "actual=", "hide reason=hotkey-toggle",
                    "exit-requested source=tray-menu code=0", "exit code=0", "instance-secondary delivery=True",
                })
                {
                    Check(log.Contains(marker), $"关键路径读数：{marker}（verbose={verbose}）");
                }
                Check(log.Contains(" verbose pid=") == verbose, "详细事件门禁与启动环境一致");
                if (verbose)
                {
                    Check(log.Contains("clipboard-round settled="), "真实剪贴板单轮详细读数");
                    Check(log.Contains("startup-converge development=True"), "任务收敛详细读数且开发构建不改任务");
                }
                Check(log.Split('\n').Where(line => line.Length > 0).All(line => line.Contains(" pid=")), "每条读数均带 PID");
            }

            // 首实例门被占，但不存在 pipe 服务：必须保留投递失败与非零退出码。
            var missingDir = Path.Combine(root, "missing-server");
            Seed(missingDir);
            var missingScope = "-t15-" + Guid.NewGuid().ToString("N");
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            using (var gate = new SingleInstanceGate($"ClipboardTool-{sid}{missingScope}-instance"))
            {
                Check(gate.TryAcquire(), "隔离单实例门占用成功");
                using var process = StartChild(missingDir, missingScope, "secondary", false);
                Check(Wait(process) == 1, "第二实例投递超时保留退出码 1");
                var log = File.ReadAllText(Path.Combine(missingDir, "diag.log"));
                Check(log.Contains("delivery=False") && log.Contains("exit code=1"), "投递失败有完整读数");
            }

            foreach (var source in new[] { "dispatcher", "background" })
            {
                var dir = Path.Combine(root, source);
                Seed(dir);
                using var process = StartChild(dir, "-t15-" + Guid.NewGuid().ToString("N"), source, false);
                var crashCode = Wait(process, expectedFailure: true);
                Check(crashCode != 0, $"{source} 未处理异常保留崩溃退出行为 code={crashCode}");
                var panic = File.ReadAllText(Path.Combine(dir, "panic.log"));
                Check(panic.Contains("System.InvalidOperationException") && panic.Contains(nameof(Crash)), $"{source} 异常类型与真实堆栈可追溯");
                Check(panic.Contains(source == "dispatcher" ? "source=wpf-dispatcher" : "source=appdomain"), $"{source} 全局异常钩子已接线");
                Check(!panic.Contains(PrivateMarker), "异常消息中的正文不写入日志");
                var log = File.ReadAllText(Path.Combine(dir, "diag.log"));
                Check(log.Contains("unhandled-exception") && log.Contains("exit-requested"), "崩溃在 vital 中留痕");
            }

            var taskDir = Path.Combine(root, "unobserved");
            Seed(taskDir);
            using (var process = StartChild(taskDir, "-t15-" + Guid.NewGuid().ToString("N"), "unobserved", false))
            {
                Check(Wait(process) == 0, "未观察任务异常留痕且保留运行时非终止策略");
                var panic = File.ReadAllText(Path.Combine(taskDir, "panic.log"));
                Check(panic.Contains("source=unobserved-task") && panic.Contains(nameof(Crash)), "真实任务异常钩子保留嵌套堆栈");
                Check(!panic.Contains(PrivateMarker), "任务异常不写入正文");
            }

            var blockedDir = Path.Combine(root, "blocked-log");
            Seed(blockedDir);
            Directory.CreateDirectory(Path.Combine(blockedDir, "diag.log"));
            using (var process = StartChild(blockedDir, "-t15-" + Guid.NewGuid().ToString("N"), "normal", false))
            {
                Check(Wait(process) == 0, "日志路径不可写时真实 App 仍能启动、呼出和退出");
            }

            var sharedDir = Path.Combine(root, "shared");
            Directory.CreateDirectory(sharedDir);
            File.WriteAllBytes(Path.Combine(sharedDir, "diag.log"), new byte[512 * 1024 + 1]);
            using var writer1 = Start(["--writer", sharedDir, "one"], false);
            using var writer2 = Start(["--writer", sharedDir, "two"], false);
            Check(Wait(writer1) == 0 && Wait(writer2) == 0, "两个独立进程同时写入并轮转成功");
            var lines = File.ReadAllLines(Path.Combine(sharedDir, "diag.log"));
            Check(lines.Length == 80 && lines.Distinct().Count() == 80, "跨进程轮转与追加无缺行或截断");
            Check(Directory.GetFiles(sharedDir).Length == 2, "轮转仅保留 diag.log 与 diag.log.1");
            if (evidenceDirectory is not null)
            {
                foreach (var source in Directory.GetFiles(root, "*.log", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(evidenceDirectory, Path.GetRelativePath(root, source));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: true);
                }
            }
            Console.WriteLine("全部 F40 隔离进程验证通过；未修改用户存档、设置和计划任务。");
            return 0;
        }
        finally
        {
            _ = SetForegroundWindow(foreground);
            // root 直接创建于 Temp 且包含本次 GUID，删除范围仅限本工具的隔离数据。
            Directory.Delete(root, recursive: true);
        }
    }

    private static int RunChild(string directory, string scope, string scenario)
    {
        var app = new App(directory, scope);
        app.InitializeComponent();
        if (scenario != "secondary")
        {
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                if (scenario == "dispatcher")
                {
                    _ = app.Dispatcher.BeginInvoke(new Action(Crash));
                    return;
                }
                if (scenario == "background")
                {
                    new Thread(Crash) { IsBackground = true }.Start();
                    return;
                }
                try
                {
                    if (scenario == "unobserved")
                    {
                        _ = CreateUnobservedTask();
                        for (var attempt = 0; attempt < 20; attempt++)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            if (File.Exists(Path.Combine(directory, "panic.log"))) break;
                            await Task.Delay(20);
                        }
                        app.Shutdown(0);
                        return;
                    }
                    var panel = Field<PanelWindow>(app, "_panel");
                    var tray = Field<TrayIconHost>(app, "_tray");
                    var menu = Field<TrayContextMenu>(app, "_trayMenu");
                    var watch = Field<ClipboardWatchService>(app, "_watch");
                    var trayHwnd = Field<IntPtr>(tray, "_hwnd");
                    // 发送真实 WM_HOTKEY / 托盘回调消息；这不是物理键盘或鼠标验收。
                    Toggle(panel);
                    await Until(() => !panel.IsDocked);
                    Toggle(panel);
                    await Until(() => panel.IsDocked);
                    _ = SendMessageW(trayHwnd, 0x8001, IntPtr.Zero, new IntPtr(0x0202));
                    await Until(() => !panel.IsDocked);
                    Toggle(panel);
                    await Until(() => panel.IsDocked);
                    MenuCommand(trayHwnd, menu, "显示剪贴板面板");
                    await Until(() => !panel.IsDocked);
                    Toggle(panel);
                    await Until(() => panel.IsDocked);
                    using var secondary = StartChild(directory, scope, "secondary", false);
                    await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    if (secondary.ExitCode != 0) throw new InvalidOperationException("第二实例投递失败");
                    await Until(() => !panel.IsDocked);
                    watch.PollRound();
                    MenuCommand(trayHwnd, menu, "退出");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception);
                    app.Shutdown(2);
                }
            }));
        }
        return app.Run();
    }

    private static void Seed(string directory) => new JsonStore(directory).SaveSettings(
        AppSettings.Default with { Shortcut = "Control+Alt+Shift+F12", AutoStart = false });

    private static Process StartChild(string directory, string scope, string scenario, bool verbose) =>
        Start(["--child", directory, scope, scenario], verbose);

    private static Process Start(string[] arguments, bool verbose)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["CLIPBOARD_TOOL_DIAG"] = verbose ? "1" : "0";
        var process = Process.Start(info)!;
        // 从启动时持续排空 stderr，避免 Windows 管道缓冲区填满后阻塞故意崩溃的子进程。
        ChildErrors[process.Id] = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static int Wait(Process process, bool expectedFailure = false)
    {
        if (!process.WaitForExit(25000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            var arguments = process.StartInfo.ArgumentList;
            if (arguments.Count > 1 && arguments[0] == "--child")
            {
                var path = Path.Combine(arguments[1], "diag.log");
                if (File.Exists(path)) Console.Error.WriteLine(File.ReadAllText(path));
            }
            throw new TimeoutException("隔离子进程超时");
        }
        var error = ChildErrors[process.Id].GetAwaiter().GetResult();
        ChildErrors.Remove(process.Id);
        if (!expectedFailure && process.ExitCode != 0 && error.Length > 0) Console.Error.WriteLine(error);
        return process.ExitCode;
    }

    private static void Toggle(PanelWindow panel) =>
        SendMessageW(panel.Hwnd, 0x0312, new IntPtr(1), IntPtr.Zero);

    private static void MenuCommand(IntPtr hwnd, TrayContextMenu menu, string header)
    {
        _ = SendMessageW(hwnd, 0x8001, IntPtr.Zero, new IntPtr(0x0205));
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("窗口状态未到达");
            await Task.Delay(20);
        }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void Crash() => throw new InvalidOperationException(PrivateMarker);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnobservedTask()
    {
        var task = Task.Run(Crash);
        if (!SpinWait.SpinUntil(() => task.IsCompleted, 3000)) throw new TimeoutException("任务未完成");
        return new WeakReference(task);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")]
    private static extern int WerSetFlags(uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
