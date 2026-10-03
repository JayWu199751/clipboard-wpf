using System.Diagnostics;
using System.IO;

namespace P2Focus;

/// <summary>
/// negative 模式（普通权限运行）：medium 进程向 high 完整性靶窗直接 SendInput Ctrl+V。
/// 预期：SendInput 返回 4（官方文档：被 UIPI 阻断时返回值与 GetLastError 均不指示失败），
/// 但靶窗自记事件日志显示内容从未到达——证明「注入成功」只能靠回读判断，不能信返回值。
/// </summary>
internal static class NegativeControl
{
    public static int Run()
    {
        FocusRestore.PumpHook = Win32.Pump;
        Paths.EnsureDir();
        var lines = new List<string>();
        string il = Win32.IntegrityOfCurrent();
        lines.Add($"负控进程完整性：{il}（预期 medium）");
        if (il.StartsWith("high"))
        {
            lines.Add("负控进程已提权，UIPI 负控无意义，跳过。");
            WriteReport(lines, 2);
            return 2;
        }

        // 提权靶窗：UAC 静默策略下由 medium 进程发起 runas 也能无提示完成
        try { File.Delete(Path.Combine(Paths.Dir, "target-NEG.json")); } catch { }
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--target --name NEG --exit-after 25")
                { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex) { lines.Add($"提权靶窗启动失败：{ex.Message}"); WriteReport(lines, 2); return 2; }

        var t = Runner.LoadTarget("NEG", 15000);
        if (t == null) { lines.Add("未取得 NEG 握手文件。"); WriteReport(lines, 2); return 2; }
        lines.Add($"NEG 靶窗 hwnd={t.Hwnd:X}；完整性回读：{Win32.IntegrityOfProcess(t.Pid)}（预期 high）");

        var hSac = ProbeWindows.Create("P2SacrificeWnd", "P2 牺牲窗（误注检测）", 360, 220, out var hSacEdit);
        Win32.Pump(100);

        string payload = $"P2-NEG-CTRL-{Guid.NewGuid():N}"[..26];
        if (!Win32.SetClipboardText(payload)) { lines.Add("写剪贴板失败。"); WriteReport(lines, 2); return 2; }

        if (!Runner.ActivateWindow(t.Hwnd))
        { lines.Add("无法激活 NEG 为前台，负控跳过（不向未验证目标注入）。"); WriteReport(lines, 2); return 2; }
        Win32.Pump(150);
        IntPtr root = Runner.ForegroundRoot();
        if (root != t.Hwnd)
        { lines.Add($"NEG 前台回读不符（root={root:X}），负控跳过。"); WriteReport(lines, 2); return 2; }
        lines.Add($"NEG 已验证为前台（root={root:X}）；直接 SendInput Ctrl+V（无恢复步骤）。");

        uint sent = Win32.SendCtrlV();
        lines.Add($"SendInput 返回 {sent}/4（被 UIPI 阻断时按文档不报错）。");

        bool arrived = Runner.WaitTargetText(t, payload, 2500);
        string sacText = Win32.GetWindowText(hSacEdit) ?? "";
        bool sacGot = sacText.Contains(payload, StringComparison.Ordinal);
        IntPtr rootAfter = Runner.ForegroundRoot();
        lines.Add($"NEG 自记日志显示内容到达：{arrived}（预期 false）");
        lines.Add($"牺牲窗收到内容：{sacGot}（预期 false）；NEG 前台保持：{rootAfter == t.Hwnd}（预期 true）");

        // medium 进程无法向 high 窗口投 WM_CLOSE（UIPI 拦截），靠 --exit-after 25 自清
        WriteReport(lines, (arrived || sacGot) ? 1 : 0);
        return (arrived || sacGot) ? 1 : 0;
    }

    static void WriteReport(List<string> lines, int code)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# P2 负控报告：medium 进程向 high 靶窗注入（UIPI）");
        sb.AppendLine();
        sb.AppendLine($"日期：{DateTime.Now:yyyy-MM-dd HH:mm:ss}；OS：{Environment.OSVersion.VersionString}");
        sb.AppendLine();
        foreach (var l in lines) sb.AppendLine($"- {l.Replace("|", "\\|")}");
        sb.AppendLine();
        sb.AppendLine(code switch
        {
            0 => "**结论：注入被 UIPI 过滤，靶窗未收到内容；SendInput 返回值仍为 4。**",
            1 => "**结论：内容到达了 high 靶窗——UIPI 未按预期过滤（需复查机器策略）！**",
            _ => "**结论：负控未完成（跳过）。**",
        });
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "p2-negative-report.md"), sb.ToString());
    }
}
