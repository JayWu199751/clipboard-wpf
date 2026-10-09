using System.Windows.Threading;
using ClipboardTool.Application;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>进程级异常取证。只观察，不把未处理异常标为已处理，也不改变 WPF/CLR 退出策略。</summary>
public sealed class ProcessDiagnostics : IDisposable
{
    private readonly DiagnosticLog _log;
    private readonly Dispatcher _dispatcher;
    private int _exitRecorded;
    private int _crashed;

    public ProcessDiagnostics(DiagnosticLog log, Dispatcher dispatcher)
    {
        _log = log;
        _dispatcher = dispatcher;
        _dispatcher.UnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public void CompleteExit(int code)
    {
        if (Interlocked.Exchange(ref _exitRecorded, 1) == 0)
        {
            _log.Vital($"exit code={code}");
        }
    }

    private void Report(string source, Exception exception, bool terminating)
    {
        _log.Panic(source, exception);
        _log.Vital($"unhandled-exception source={source} terminating={terminating}");
        if (terminating)
        {
            Interlocked.Exchange(ref _crashed, 1);
            _log.Vital($"exit-requested source={source} code=runtime");
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        Report("wpf-dispatcher", e.Exception, terminating: true);

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Report("appdomain", exception, e.IsTerminating);
        }
    }

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        Report("unobserved-task", e.Exception, terminating: false);

    private void OnProcessExit(object? sender, EventArgs e)
    {
        // CLR 异常退出不一定到达 WPF OnExit；若尚未给出退出码，不把崩溃误记成成功。
        var code = Environment.ExitCode;
        if (Volatile.Read(ref _crashed) != 0 && code == 0)
        {
            if (Interlocked.Exchange(ref _exitRecorded, 1) == 0)
            {
                _log.Vital("exit code=runtime reason=unhandled-exception");
            }
            return; // CLR 的最终崩溃码由运行时决定，不能把估算值当成实际退出码。
        }
        CompleteExit(code);
    }

    public void Dispose()
    {
        _dispatcher.UnhandledException -= OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
        TaskScheduler.UnobservedTaskException -= OnTaskException;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }
}
