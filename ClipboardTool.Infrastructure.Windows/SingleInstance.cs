using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClipboardTool.Infrastructure.Windows;

/// <summary>
/// 单实例呼出协议（F34）：第二实例经 named pipe 向原进程投递的一行消息。
/// 串在此收口——服务端只认它，其余消息（哪怕是近似的拼写）一律拒绝。
/// </summary>
public static class SummonProtocol
{
    public const string SummonRequest = "CLIPBOARDTOOL summon v2";
}

/// <summary>
/// 单实例 mutex 门（F34）：TryAcquire 判「谁是首个实例」。abandoned（前实例崩溃退出）
/// 视为可获得——否则一次崩溃会把后续所有启动都挡在门外。
/// mutex 内核对象按线程计可重入（同进程内同线程再 WaitOne 会误判成功），故用
/// createdNew + 进程内已发放表双重把守；mutex/pipe 名建议按用户身份拼接
/// （本用户隔离；普通↔提权是同一用户 SID，天然互通）。
/// </summary>
public sealed class SingleInstanceGate : IDisposable
{
    private static readonly object LocallyHeldLock = new();
    private static readonly HashSet<string> LocallyHeld = new(StringComparer.Ordinal);

    // 获锁 gate 的根集：gate 是普通对象，宿主若只把它放在局部变量里，GC 回收会终结内核
    // Mutex（句柄关闭=锁释放）→ 单实例门失效，后续启动会整份跑起来（真机 E2E 实测的缺陷）。
    // 获锁期间自持引用，Dispose 时移除；App 侧无需关心。
    private static readonly List<SingleInstanceGate> RootedGates = [];

    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly bool _createdNew;
    private bool _acquired;

    public SingleInstanceGate(string name)
    {
        _name = name;
        _mutex = new Mutex(initiallyOwned: true, name, out _createdNew);
    }

    /// <summary>尝试成为首个实例；重复调用不改变判定（所有权在释放前独占）。</summary>
    public bool TryAcquire()
    {
        if (_acquired)
        {
            return true;
        }
        lock (LocallyHeldLock)
        {
            if (LocallyHeld.Contains(_name))
            {
                return false; // 本进程内已有别的 gate 持有：不靠内核可重入误判
            }
            if (_createdNew)
            {
                _acquired = true; // 内核对象由本构造新建 → 本进程是首个持有者
            }
            else
            {
                try
                {
                    _acquired = _mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    // 前一个持有者没 Release 就退了：所有权已经落到本进程手上
                    _acquired = true;
                }
            }
            if (_acquired)
            {
                LocallyHeld.Add(_name);
                lock (RootedGates)
                {
                    RootedGates.Add(this); // 根集保活：见 RootedGates 注释
                }
            }
            return _acquired;
        }
    }

    public void Dispose()
    {
        if (_acquired)
        {
            _mutex.ReleaseMutex();
            lock (LocallyHeldLock)
            {
                LocallyHeld.Remove(_name);
            }
            lock (RootedGates)
            {
                RootedGates.Remove(this);
            }
            _acquired = false;
        }
        _mutex.Dispose();
    }
}

/// <summary>
/// 呼出请求的服务端（F34）：named pipe 循环接受连接，一行一请求；只接受合法呼出动作
/// （SummonProtocol.SummonRequest），非法消息静默丢弃——管道不被当成第二控制通道。
/// 接收回调在服务端后台线程触发，宿主自行归队（与「状态挂上前不许调用」的启动红线一致）。
/// </summary>
public sealed class SummonServer : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <summary>合法呼出请求送达（每条一次）。可能来自服务端后台线程。</summary>
    public event Action? SummonReceived;

    public SummonServer(string pipeName) => _pipeName = pipeName;

    /// <summary>消息校验：只有标准呼出串合法（收口在协议，不在调用点）。</summary>
    public static bool IsValidRequest(string? message) => message == SummonProtocol.SummonRequest;

    /// <summary>启动接受循环（后台线程）。重复调用无操作。</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        _loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer(_pipeName);
                using var reader = new StreamReader(server);
                await server.WaitForConnectionAsync(_stopping.Token);
                var line = await reader.ReadLineAsync(_stopping.Token);
                if (IsValidRequest(line))
                {
                    SummonReceived?.Invoke();
                }
                // 非法请求：静默丢弃，继续下一条连接
            }
            catch (Exception) when (_stopping.IsCancellationRequested
                || server is null || !server.IsConnected)
            {
                break; // 停机/管道已毁：退出循环
            }
            catch (IOException)
            {
                // 客户端连上又断了：继续接受下一条
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    /// <summary>带 ACL 的管道：本用户与管理员组可读写（普通↔提权完整性互通；真机判据④实测）。</summary>
    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = new PipeSecurity();
        var rules = new (SecurityIdentifier Sid, PipeAccessRights Rights)[]
        {
            (WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite),
            (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite),
        };
        foreach (var (sid, rights) in rules)
        {
            security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
        }
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 循环线程因停机异常退出属预期
        }
        _stopping.Dispose();
    }
}

/// <summary>
/// 呼出请求的客户端（F34，第二实例侧）：对未就绪的服务端有限等待（启动期的服务端
/// 可能还没建好管道），就绪即投递；超时放弃——宁可这次不呼出，也不无限挂住第二实例。
/// </summary>
public static class SummonClient
{
    /// <summary>单次连接尝试的超时（短试快重试，总时长由 timeoutMs 把守）。</summary>
    private const int ProbeTimeoutMs = 150;

    public static async Task<bool> SummonAsync(string pipeName, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(Math.Min(ProbeTimeoutMs, timeoutMs)).ConfigureAwait(false);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync(SummonProtocol.SummonRequest).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                // 服务端这 150ms 没接受连接：继续等
            }
            catch (IOException)
            {
                // 管道不存在或对端中断：稍候重试
            }
        }
        return false; // 超时放弃
    }
}
