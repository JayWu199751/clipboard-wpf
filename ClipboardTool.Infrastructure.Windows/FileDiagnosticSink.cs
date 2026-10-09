using System.IO;
using System.Security.Cryptography;
using System.Text;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// F40 日志文件：每次短开短关，主实例/二实例用同一路径的命名互斥量串行轮转与追加。
/// 等锁最多 50ms；占用或失败放弃本条，不持有后台线程或常驻文件句柄。
/// </summary>
public sealed class FileDiagnosticSink : IDiagnosticSink
{
    public const long RotationLimitBytes = 512 * 1024;
    private readonly string _directory;
    private readonly string _mutexName;

    public FileDiagnosticSink(string directory)
    {
        _directory = Path.GetFullPath(directory);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(_directory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()));
        _mutexName = @"Local\ClipboardTool.Diagnostics." + Convert.ToHexString(key);
    }

    public void AppendDiagnostic(string entry) => Append("diag.log", entry, rotate: true);
    public void AppendPanic(string entry) => Append("panic.log", entry, rotate: false);

    private void Append(string filename, string entry, bool rotate)
    {
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(50));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true; // 前一进程崩溃后仍可继续取证。
                }
                if (!acquired)
                {
                    return;
                }
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, filename);
                // legacy ADR-0013：已有文件严格超过 512KB 才轮转；正好等于不轮转。
                if (rotate && File.Exists(path) && new FileInfo(path).Length > RotationLimitBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, entry, new UTF8Encoding(false));
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (Exception)
        {
            // 诊断失败不影响启动、呼出、停靠或退出。
        }
    }
}
