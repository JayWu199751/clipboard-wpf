namespace ClipboardTool.Application;

/// <summary>诊断落盘端口；测试使用隔离替身，绝不写入用户的真实现场。</summary>
public interface IDiagnosticSink
{
    void AppendDiagnostic(string entry);
    void AppendPanic(string entry);
}

/// <summary>
/// F40 两档诊断：vital 无门禁，verbose 仅接受环境变量值 1。
/// 调用方只传事件与技术读数；异常只记录类型/错误码/堆栈，不记录可能包含剪贴板正文的 Message。
/// 全部格式化与落盘失败在此隔离，不影响业务结果。
/// </summary>
public sealed class DiagnosticLog(IDiagnosticSink sink, string? verboseSetting, int processId)
{
    public static DiagnosticLog None { get; } = new(new NullSink(), null, 0);

    public bool VerboseEnabled { get; } = verboseSetting == "1";

    public void Vital(string message) => Write("vital", message);

    public void Verbose(string message)
    {
        if (VerboseEnabled)
        {
            Write("verbose", message);
        }
    }

    public void Panic(string source, Exception exception)
    {
        try
        {
            var trace = DescribeException(exception, 0);
            sink.AppendPanic($"{Prefix("panic")} source={SingleLine(source)} {trace}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 诊断不可把原始故障替换成日志写入故障。
        }
    }

    private void Write(string level, string message)
    {
        try
        {
            sink.AppendDiagnostic($"{Prefix(level)} {SingleLine(message)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 磁盘满、拒绝访问、轮转占用等均不阻断主链路。
        }
    }

    private string Prefix(string level) => $"{DateTimeOffset.UtcNow:O} {level} pid={processId}";

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private static string DescribeException(Exception exception, int depth)
    {
        var trace = $"type={exception.GetType().FullName} hresult=0x{exception.HResult:X8}";
        if (exception.StackTrace is { } stack)
        {
            trace += Environment.NewLine + stack;
        }
        if (depth < 8)
        {
            IEnumerable<Exception> children = exception is AggregateException aggregate
                ? aggregate.InnerExceptions
                : exception.InnerException is { } inner ? [inner] : Array.Empty<Exception>();
            foreach (var child in children)
            {
                trace += Environment.NewLine + "caused-by " + DescribeException(child, depth + 1);
            }
        }
        return trace;
    }

    private sealed class NullSink : IDiagnosticSink
    {
        public void AppendDiagnostic(string entry) { }
        public void AppendPanic(string entry) { }
    }
}
