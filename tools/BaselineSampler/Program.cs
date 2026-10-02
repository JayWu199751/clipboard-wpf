using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipboardTool.Tools.BaselineSampler;

/// <summary>
/// 性能基线采样（F40 设施雏形）：按固定间隔对指定 PID 采样
/// PrivateMemorySize64 / WorkingSet64 / HandleCount / GDI 对象数，输出 CSV。
/// 用法：BaselineSampler [--pid &lt;n|self&gt;] [--interval-ms 1000] [--duration-ms 60000] [--out &lt;path&gt;]
/// 不带 --out 时写 stdout。
/// </summary>
internal static class Program
{
    private const uint GdiObjects = 0; // GR_GDIOBJECTS

    private static bool _stopRequested;

    private static int Main(string[] args)
    {
        uint pid = (uint)Environment.ProcessId;
        var intervalMs = 1000;
        var durationMs = 60_000;
        string? outPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length && args[i + 1] != "self":
                    if (!uint.TryParse(args[++i], out pid))
                    {
                        Console.Error.WriteLine("无效 --pid");
                        return 2;
                    }
                    break;
                case "--pid":
                    i++;
                    break;
                case "--interval-ms" when i + 1 < args.Length:
                    intervalMs = int.Parse(args[++i]);
                    break;
                case "--duration-ms" when i + 1 < args.Length:
                    durationMs = int.Parse(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    return 2;
            }
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _stopRequested = true;
        };

        using var process = pid == Environment.ProcessId
            ? Process.GetCurrentProcess()
            : Process.GetProcessById((int)pid);

        using var writer = outPath is null
            ? Console.Out
            : new StreamWriter(File.Create(EnsureDirectory(outPath))) { AutoFlush = true };

        writer.WriteLine("timestamp,pid,private_memory_bytes,working_set_bytes,handle_count,gdi_objects");

        var clock = Stopwatch.StartNew();
        while (!_stopRequested && clock.ElapsedMilliseconds < durationMs)
        {
            try
            {
                process.Refresh();
                var gdi = GetGuiResources(process.Handle, GdiObjects);
                writer.WriteLine(
                    $"{DateTime.Now:yyyy-MM-dd'T'HH:mm:ss.fff},{pid},{process.PrivateMemorySize64}," +
                    $"{process.WorkingSet64},{process.HandleCount},{gdi}");
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"目标进程不可采样（可能已退出或权限不足）：{error.Message}");
                return 1;
            }

            Thread.Sleep(intervalMs);
        }

        return 0;
    }

    private static string EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        return fullPath;
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
}
