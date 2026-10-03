using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// T08 工单 12 真机验证只读探针（本机 schtasks/reg 在代理沙箱黑名单内，计划任务操作
// 全部经产品自身代码路径：应用启动收敛建任务、NSIS 卸载器删任务；本探针只读取证）。
// 子命令：
//   procs       —— 列出 ClipboardTool 进程及提权态（TokenElevation，UAC 直启判据）
//   regkey      —— 读卸载注册表项（NSIS 安装/卸载判据；--present 期望存在）
//   archive     —— %APPDATA%\ClipboardTool 内容清单（存档保留判据）
// 退出码：0=全部断言过，1=断言失败，2=环境异常。

return args.FirstOrDefault() switch
{
    "procs" => Probe.Procs(),
    "regkey" => Probe.RegKey(expectPresent: args.Contains("--present")),
    "regkey-views" => Probe.RegKeyViews(),
    "archive" => Probe.Archive(),
    _ => Probe.Usage(),
};

internal static class Probe
{
    public static int Usage()
    {
        Console.WriteLine("用法: E2eT08 <procs|regkey [--present]|archive>");
        return 2;
    }

    public static int Procs()
    {
        var procs = Process.GetProcessesByName("ClipboardTool");
        if (procs.Length == 0)
        {
            Console.WriteLine("procs: 无 ClipboardTool 进程");
            return 1;
        }
        var failures = new List<string>();
        foreach (var p in procs)
        {
            var elevated = TryQueryElevated(p.Id, out var err);
            Console.WriteLine($"pid={p.Id} elevated={(elevated is null ? "未知" : elevated)} path={TryPath(p) ?? "?"} err={err ?? "-"}");
            if (elevated is null)
            {
                failures.Add($"pid={p.Id} 提权态查询失败");
            }
            else if (elevated == false)
            {
                failures.Add($"pid={p.Id} 未提权（requireAdministrator 产物直启必须提权）");
            }
        }
        Console.WriteLine(failures.Count == 0 ? "procs: PASS" : $"procs: FAIL {string.Join("; ", failures)}");
        return failures.Count == 0 ? 0 : 1;
    }

    public static int RegKey(bool expectPresent)
    {
        // 键名=产品 GUID（新产品身份，与旧 Tauri 版 Uninstall\ClipboardTool 互不覆盖）；64 位视图
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7E4A9C31-8F2D-4B6A-9C05-3A1D8E52F7B4}";
        using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(key);
        var present = k is not null;
        Console.WriteLine($"regkey present={present} (期望={expectPresent})");
        if (present)
        {
            Console.WriteLine($"  DisplayName={k!.GetValue("DisplayName")} DisplayVersion={k.GetValue("DisplayVersion")}");
            Console.WriteLine($"  UninstallString={k.GetValue("UninstallString")}");
        }
        var pass = present == expectPresent;
        Console.WriteLine(pass ? "regkey: PASS" : "regkey: FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>64/32 位双注册表视图读卸载键：鉴别 NSIS（32 位进程 → WOW6432Node）与旧 Tauri 残留。</summary>
    public static int RegKeyViews()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClipboardTool";
        foreach (var (view, label) in new[] { (RegistryView.Registry64, "view64"), (RegistryView.Registry32, "view32") })
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(key);
            if (k is null)
            {
                Console.WriteLine($"{label}: <缺>");
            }
            else
            {
                Console.WriteLine($"{label}: DisplayName={k.GetValue("DisplayName")} Version={k.GetValue("DisplayVersion")} " +
                    $"UninstallString={k.GetValue("UninstallString")} InstallLocation={k.GetValue("InstallLocation")}");
            }
        }
        return 0;
    }

    public static int Archive()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardTool");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("archive: 目录不存在");
            return 1;
        }
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"archive: 目录存在，文件数={files.Count}");
        foreach (var f in files.Take(10))
        {
            Console.WriteLine($"  {Path.GetRelativePath(dir, f)} ({new FileInfo(f).Length} B)");
        }
        return 0;
    }

    private static bool? TryQueryElevated(int pid, out string? error)
    {
        error = null;
        try
        {
            const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            const int TokenElevation = 20;
            var h = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                error = new Win32Exception().Message;
                return null;
            }
            try
            {
                if (!Native.OpenProcessToken(h, 0x0008 /*TOKEN_QUERY*/, out var token))
                {
                    error = new Win32Exception().Message;
                    return null;
                }
                using (token)
                {
                    if (!Native.GetTokenInformation(token, TokenElevation, out int elevation, sizeof(int), out _))
                    {
                        error = new Win32Exception().Message;
                        return null;
                    }
                    return elevation != 0;
                }
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string? TryPath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null; // 跨提权边界读不到主模块属预期（本探针常为普通权限）
        }
    }
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, int access, out SafeTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(SafeTokenHandle token, int infoClass, out int info, int len, out int retLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}

internal sealed class SafeTokenHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeTokenHandle() : base(true) { }
    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}
