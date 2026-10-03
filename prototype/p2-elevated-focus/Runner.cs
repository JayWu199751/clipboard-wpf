using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace P2Focus;

internal sealed record TargetInfo(string Name, IntPtr Hwnd, IntPtr EditHwnd, uint Pid, uint Tid, string Kind, string Integrity);

/// <summary>
/// run 模式编排：以管理员运行，对普通/提权靶窗与真实记事本执行
/// 恢复+注入、失效拒绝、最小化恢复、快速路径零操作与失败路径可观测五组判据。
/// </summary>
internal static class Runner
{
    static readonly List<string> LogLines = new();
    static readonly List<(string Id, bool Pass, string Detail)> Criteria = new();

    static IntPtr hPanel, hPanelEdit;
    static string clipboardNote = "";

    public static string ReportPath => Path.Combine(AppContext.BaseDirectory, "p2-report.md");

    public static int Run()
    {
        FocusRestore.PumpHook = Win32.Pump;
        Paths.EnsureDir();
        var swTotal = Stopwatch.StartNew();
        string il = Win32.IntegrityOfCurrent();
        LogLine($"runner 完整性 {il}；工作目录 {Paths.Dir}");
        if (!il.StartsWith("high"))
        {
            Criteria.Add(("C0-runner", false, $"runner 完整性={il}，必须管理员运行（用 run-runner.ps1）"));
            WriteReport(il);
            return 1;
        }

        string? savedText = null;
        try { savedText = Win32.GetClipboardText(); } catch { }
        clipboardNote = savedText != null ? "原剪贴板为文本，结束时还原" : "原剪贴板非文本/为空，不还原";

        hPanel = ProbeWindows.Create("P2PanelWnd", "P2 面板（模拟）", 380, 250, out hPanelEdit);
        Win32.Pump(100);

        // A 与普通记事本由普通权限 shell（start-target.ps1）先行启动
        var a = LoadTarget("A", 15000);
        if (a != null) a = a with { Integrity = Win32.IntegrityOfProcess(a.Pid) };
        var n1 = LoadTarget("N1", 5000);
        if (n1 != null) n1 = n1 with { Integrity = Win32.IntegrityOfProcess(n1.Pid) };

        // B 与提权记事本 N2 均由普通侧脚本先行启动（静默提权），runner 只读握手；
        // 早期版本由 runner 拉起子进程后立即激活，B 的 UI 线程会可复现地冻结
        var b = LoadTarget("B", 20000);
        if (b != null) b = b with { Integrity = Win32.IntegrityOfProcess(b.Pid) };
        var n2 = LoadTarget("N2", 20000);
        if (n2 != null) n2 = n2 with { Integrity = Win32.IntegrityOfProcess(n2.Pid) };

        LogLine($"目标 A：{(a == null ? "缺失" : $"hwnd={a.Hwnd:X} {a.Integrity}")}；" +
                $"目标 B：{(b == null ? "缺失" : $"hwnd={b.Hwnd:X} {b.Integrity}")}；" +
                $"记事本 N1：{(n1 == null ? "缺失" : $"{n1.Hwnd:X} {n1.Integrity}")}；" +
                $"记事本 N2(提权)：{(n2 == null ? "缺失" : $"{n2.Hwnd:X} {n2.Integrity}")}");

        // 让刚启动的靶窗线程完成初始化，降低激活偶发失败的窗口
        Win32.Pump(600);

        // ---- 判据（C3 放最后：需要先关闭 A/B，其余判据要用活靶） ----
        if (a == null || !a.Integrity.StartsWith("medium"))
            Criteria.Add(("C1", false, $"目标 A 未就绪或完整性非 medium（实测 {(a?.Integrity ?? "缺失")}）；A 必须由普通 shell 启动"));
        else
            Gate("C1", () => ProbeRoundTrip(a, "C1-high-to-normal"));

        if (b == null || !b.Integrity.StartsWith("high"))
            Criteria.Add(("C2", false, $"提权靶 B 未就绪或完整性非 high（实测 {(b?.Integrity ?? "缺失")}）"));
        else
            Gate("C2", () => ProbeRoundTrip(b, "C2-high-to-elevated"));

        if (n1 == null || !n1.Integrity.StartsWith("medium"))
            Supp("S1", $"普通记事本未就绪（{(n1?.Integrity ?? "缺失")}）");
        else
            Supp("S1", () => NotepadRoundTrip(n1, "S1-notepad-normal"));

        if (n2 == null || !n2.Integrity.StartsWith("high"))
            Supp("S2", $"提权记事本未就绪（{(n2?.Integrity ?? "缺失")}）");
        else
            Supp("S2", () => NotepadRoundTrip(n2, "S2-notepad-elevated"));

        if (a == null)
        {
            Criteria.Add(("C4a", false, "目标 A 未就绪"));
            Criteria.Add(("C4b", false, "目标 A 未就绪"));
        }
        else
        {
            Gate("C4a", () => MinimizedTest(a));
            Gate("C4b", () => FastPathTest(a));
        }

        if (b == null)
            Criteria.Add(("C3", false, "无提权靶 B 可做句柄失效试验"));
        else
            Gate("C3", () => InvalidHandleTest(b, a));

        Gate("C5", ObservabilityTest);

        // ---- 清理 ----
        CloseTarget(a);
        CloseTarget(b);
        CloseTarget(n1);
        CloseTarget(n2);
        if (savedText != null) { try { Win32.SetClipboardText(savedText); } catch { } }
        LogLine($"总耗时 {swTotal.Elapsed.TotalSeconds:F1}s；{clipboardNote}");

        WriteReport(il);
        return Criteria.Count(c => !c.Id.StartsWith("S") && !c.Pass);
    }

    // ---- 判据实现 ----

    // 主路径：靶窗前台并持焦 → 快照 → 面板拿走前台 → 恢复+注入 → 回读前台/焦点/内容
    static (bool, string) ProbeRoundTrip(TargetInfo t, string tag)
    {
        var payload = Payload(tag);
        if (!ClearTarget(t)) return (false, "清空靶窗编辑框失败");
        if (!Win32.SetClipboardText(payload)) return (false, "写剪贴板失败");

        // 新启动的靶窗线程偶发未就绪，激活失败重试（每次失败后留出抽水时间）
        bool activated = false;
        for (int i = 0; i < 3 && !activated; i++)
        {
            activated = ActivateWindow(t.Hwnd);
            if (!activated) Win32.Pump(250);
        }
        if (!activated) return (false, "前置激活靶窗失败（重试 3 次未成功）");
        Win32.Pump(120);
        if (ForegroundRoot() != t.Hwnd) return (false, $"前置激活后前台不是靶窗（root={ForegroundRoot():X}）");
        var snap = FocusRestore.Snapshot();
        if (snap == null || snap.Value.Hwnd != t.Hwnd.ToInt64())
            return (false, "焦点快照失败或顶层不匹配");
        string snapNote = $"快照 hwnd={snap.Value.Hwnd:X} focus={snap.Value.FocusHwnd:X} pid={snap.Value.Pid} tid={snap.Value.Tid}";

        if (!ActivateWindow(hPanel)) return (false, "激活面板失败");
        Win32.Pump(120);
        if (ForegroundRoot() != hPanel) return (false, $"面板未成为前台（root={ForegroundRoot():X}）");

        var (ok, fail) = FocusRestore.RestoreAndPaste(snap.Value, true);
        Win32.Pump(80);
        bool content = WaitTargetText(t, payload, 3000);
        bool fgBack = ForegroundRoot() == t.Hwnd;
        bool focusBack = FocusOf(t.Tid) == t.EditHwnd;
        return (ok && content && fgBack && focusBack,
            $"{snapNote}；ok={ok} fail={fail?.Stage}:{fail?.Reason} [{FocusRestore.LastRun}]；" +
            $"内容落靶={content}；前台归还={fgBack}；焦点归还={focusBack}");
    }

    static (bool, string) NotepadRoundTrip(TargetInfo t, string tag)
    {
        var payload = Payload(tag);
        if (!Win32.SetClipboardText(payload)) return (false, "写剪贴板失败");
        if (!ActivateWindow(t.Hwnd)) return (false, "前置激活记事本失败");
        Win32.Pump(150);
        if (ForegroundRoot() != t.Hwnd) return (false, $"前台不是记事本（root={ForegroundRoot():X}）");
        var snap = FocusRestore.Snapshot();
        if (snap == null || snap.Value.Hwnd != t.Hwnd.ToInt64()) return (false, "快照失败");
        if (!ActivateWindow(hPanel)) return (false, "激活面板失败");
        Win32.Pump(120);
        var (ok, fail) = FocusRestore.RestoreAndPaste(snap.Value, true);
        Win32.Pump(100);
        bool fgBack = ForegroundRoot() == t.Hwnd;
        var (content, via) = NotepadContains(t.Hwnd, payload);
        return (ok && fgBack && content,
            $"ok={ok} fail={fail?.Stage}:{fail?.Reason} [{FocusRestore.LastRun}]；前台归还={fgBack}；内容回读={content}（{via}）");
    }

    static (bool, string) InvalidHandleTest(TargetInfo b, TargetInfo? a)
    {
        // 1) 关闭提权靶 B：WM_CLOSE；若 UI 线程无响应未退出则强杀（如实标注）
        Win32.PostMessageW(b.Hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        bool bClosedClean = WaitWindowGone(b.Hwnd, 5000) && WaitProcessExit(b.Pid, 3000);
        if (!bClosedClean) { try { using var p = Process.GetProcessById((int)b.Pid); if (!p.HasExited) p.Kill(); } catch { } }
        bool bGone = !Win32.IsWindow(b.Hwnd);

        // 2) 失效快照 → restore 阶段拒绝、零注入（SendMessage 一次都没发）
        var (ok1, fail1) = FocusRestore.RestoreAndPaste(ToTarget(b), true);
        bool reject1 = !ok1 && fail1 is { Stage: "restore", Reason: "restore_failed" };
        bool zero1 = FocusRestore.LastRun.SendInputKeys == 0;

        // 3) HWND 复用防御：有效 HWND（面板）+ 已死进程的 PID/TID → PID/TID 双校验必须拒绝
        string panelBefore = Win32.GetWindowText(hPanelEdit) ?? "";
        var fake = new FocusTarget(hPanel.ToInt64(), hPanelEdit.ToInt64(), b.Pid, b.Tid);
        var (ok2, fail2) = FocusRestore.RestoreAndPaste(fake, true);
        bool reject2 = !ok2 && fail2 is { Stage: "restore" };
        bool zero2 = FocusRestore.LastRun.SendInputKeys == 0;

        // 4) 普通靶 A 正常关闭后的失效快照 → 同样拒绝（medium 方向收尾）
        bool aGone = false, reject3 = false, zero3 = false;
        string fail3Note = "无 A";
        if (a != null)
        {
            Win32.PostMessageW(a.Hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline && Win32.IsWindow(a.Hwnd)) Win32.Pump(50);
            aGone = !Win32.IsWindow(a.Hwnd);
            var (ok3, fail3) = FocusRestore.RestoreAndPaste(ToTarget(a), true);
            reject3 = !ok3 && fail3 is { Stage: "restore", Reason: "restore_failed" };
            zero3 = FocusRestore.LastRun.SendInputKeys == 0;
            fail3Note = $"{fail3?.Stage}:{fail3?.Reason}";
        }

        bool untouched = (Win32.GetWindowText(hPanelEdit) ?? "") == panelBefore;

        return (bGone && reject1 && zero1 && reject2 && zero2 && (!aGone || (reject3 && zero3)) && untouched,
            $"B 关闭={(bClosedClean ? "WM_CLOSE 正常" : "无响应→强杀")} 销毁={bGone}；" +
            $"失效(B)→restore 拒绝={reject1} 零注入={zero1}；复用防御(有效HWND+死PID/TID)→拒绝={reject2} 零注入={zero2}；" +
            $"A 关闭后失效→{fail3Note} 零注入={zero3}；前台与面板编辑框未受扰={untouched}");
    }

    static (bool, string) MinimizedTest(TargetInfo t)
    {
        var payload = Payload("C4a-minimized");
        if (!ClearTarget(t)) return (false, "清空失败");
        if (!Win32.SetClipboardText(payload)) return (false, "写剪贴板失败");
        if (!ActivateWindow(t.Hwnd)) return (false, "前置激活失败");
        Win32.Pump(120);
        var snap = FocusRestore.Snapshot();
        if (snap == null || snap.Value.Hwnd != t.Hwnd.ToInt64()) return (false, "快照失败");

        Win32.ShowWindow(t.Hwnd, Win32.SW_MINIMIZE);
        Win32.Pump(400);
        bool iconic = Win32.IsIconic(t.Hwnd);
        bool fgMoved = ForegroundRoot() != t.Hwnd;
        if (!fgMoved) { ActivateWindow(hPanel); Win32.Pump(120); }

        var (ok, fail) = FocusRestore.RestoreAndPaste(snap.Value, true);
        Win32.Pump(80);
        bool restored = !Win32.IsIconic(t.Hwnd);
        bool fgBack = ForegroundRoot() == t.Hwnd;
        bool content = WaitTargetText(t, payload, 3000);
        return (iconic && fgMoved && ok && restored && fgBack && content,
            $"最小化={iconic} 前台已离开={fgMoved} ok={ok} fail={fail?.Stage}:{fail?.Reason} " +
            $"还原可见={restored} 前台归还={fgBack} 内容落靶={content} [{FocusRestore.LastRun}]");
    }

    static (bool, string) FastPathTest(TargetInfo t)
    {
        var payload = Payload("C4b-fastpath");
        if (!ClearTarget(t)) return (false, "清空失败");
        if (!Win32.SetClipboardText(payload)) return (false, "写剪贴板失败");
        if (!ActivateWindow(t.Hwnd)) return (false, "前置激活失败");
        Win32.Pump(150);
        if (ForegroundRoot() != t.Hwnd) return (false, "前台不是靶窗");
        var snap = FocusRestore.Snapshot();
        if (snap == null) return (false, "快照失败");
        var fgBefore = Win32.GetForegroundWindow();
        var focusBefore = FocusOf(t.Tid);

        var (ok, fail) = FocusRestore.RestoreAndPaste(snap.Value, true);
        var st = FocusRestore.LastRun;
        bool content = WaitTargetText(t, payload, 3000);
        bool unchanged = Win32.GetForegroundWindow() == fgBefore && FocusOf(t.Tid) == focusBefore;
        bool fast = st.FastPathHit && st.ActivateAttempts == 0 && st.AttachCalls == 0 && st.ElapsedMs < 50;
        return (ok && fast && content && unchanged,
            $"ok={ok} fail={fail?.Stage}:{fail?.Reason} [{st}] " +
            $"零操作判据(fastpath+0activate+0attach+<50ms)={fast} 内容落靶={content} 前台/焦点未变={unchanged}");
    }

    static (bool, string) ObservabilityTest()
    {
        // paste 阶段失败路径演示：恢复（快速路径）成功后注入失败 → stage=paste、reason=paste_send_failed
        Environment.SetEnvironmentVariable("P2_FORCE_PASTE_FAIL", "1");
        try
        {
            if (!ActivateWindow(hPanel)) return (false, "激活面板失败");
            Win32.Pump(120);
            var snap = FocusRestore.Snapshot();
            if (snap == null) return (false, "快照失败");
            string before = Win32.GetWindowText(hPanelEdit) ?? "";
            var (ok, fail) = FocusRestore.RestoreAndPaste(snap.Value, true);
            string after = Win32.GetWindowText(hPanelEdit) ?? "";
            bool stagePaste = fail != null && fail.Stage == "paste" && fail.Reason == "paste_send_failed";
            bool noInject = after == before;
            return (!ok && stagePaste && noInject,
                $"强制注入失败→stage=paste/reason=paste_send_failed={stagePaste}；恢复成功但零注入={noInject}；" +
                "「restore 失败绝不向当前前台注入」由 C3 的前台/牺牲框未受扰佐证");
        }
        finally { Environment.SetEnvironmentVariable("P2_FORCE_PASTE_FAIL", null); }
    }

    // ---- 记事本读回 ----

    static (bool, string) NotepadContains(IntPtr hwnd, string payload)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var child in ChildWindows(hwnd))
            {
                string cls = Win32.ClassName(child);
                if (cls.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase))
                {
                    string? text = Win32.GetWindowText(child);
                    if (text != null && text.Contains(payload)) return (true, $"WM_GETTEXT@{cls}");
                }
            }
            try
            {
                string? uia = UiaText(hwnd);
                if (uia != null && uia.Contains(payload)) return (true, "UIA");
            }
            catch { /* UIA 不可用则继续轮询 */ }
            Win32.Pump(120);
        }
        return (false, "回读不可用");
    }

    static List<IntPtr> ChildWindows(IntPtr hwnd)
    {
        var list = new List<IntPtr>();
        Win32.EnumChildWindows(hwnd, (c, l) => { list.Add(c); return true; }, IntPtr.Zero);
        return list;
    }

    static string? UiaText(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root == null) return null;
        string best = "";
        var values = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
        foreach (AutomationElement el in values)
        {
            try
            {
                if (el.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern vp)
                { var v = vp.Current.Value ?? ""; if (v.Length > best.Length) best = v; }
            }
            catch { }
        }
        if (best.Length > 0) return best;
        var texts = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
        foreach (AutomationElement el in texts)
        {
            try
            {
                if (el.GetCurrentPattern(TextPattern.Pattern) is TextPattern tp)
                { var v = tp.DocumentRange.GetText(20000) ?? ""; if (v.Length > best.Length) best = v; }
            }
            catch { }
        }
        return best.Length > 0 ? best : null;
    }

    // ---- 共用 ----

    internal static TargetInfo? LoadTarget(string name, int timeoutMs)
    {
        string path = Path.Combine(Paths.Dir, $"target-{name}.json");
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (File.Exists(path))
            {
                try { return ParseTarget(File.ReadAllText(path), name); }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            Win32.Pump(25);
        }
        return null;
    }

    static TargetInfo ParseTarget(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        long hwnd = r.GetProperty("hwnd").GetInt64();
        long edit = r.TryGetProperty("editHwnd", out var e) ? e.GetInt64() : 0;
        long pid = r.GetProperty("pid").GetInt64();
        long tid = r.TryGetProperty("tid", out var t) ? t.GetInt64() : 0;
        string kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "probe" : "probe";
        if (tid == 0) tid = Win32.GetWindowThreadProcessId((IntPtr)hwnd, out _);
        return new TargetInfo(name, (IntPtr)hwnd, (IntPtr)edit, (uint)pid, (uint)tid, kind, "");
    }

    // 前置激活统一走 FocusRestore.ActivateWindow（级联唯一实现）；LastRun 计数随 RestoreAndPaste 重置，不受污染
    internal static bool ActivateWindow(IntPtr hwnd) => FocusRestore.ActivateWindow(hwnd);

    internal static bool WaitTargetText(TargetInfo t, string expected, int timeoutMs)
    {
        string marker = Markers.Compute(expected);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (LastTextMarker(t.Name) == marker) return true;
            Win32.Pump(25);
        }
        return false;
    }

    internal static bool ClearTarget(TargetInfo t)
    {
        if (t.EditHwnd == IntPtr.Zero) return false;
        Win32.SetWindowText(t.EditHwnd, "");
        return WaitTargetText(t, "", 3000);
    }

    static string? LastTextMarker(string name)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(Paths.Dir, $"target-{name}-events.log"));
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                int idx = lines[i].IndexOf("| text | ", StringComparison.Ordinal);
                if (idx >= 0) return lines[i][(idx + 9)..];
            }
        }
        catch (IOException) { }
        return null;
    }

    static FocusTarget ToTarget(TargetInfo t) => new(t.Hwnd.ToInt64(), t.EditHwnd.ToInt64(), t.Pid, t.Tid);

    internal static IntPtr ForegroundRoot() => Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT);

    static IntPtr FocusOf(uint tid)
    {
        var info = new Win32.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<Win32.GUITHREADINFO>() };
        return tid != 0 && Win32.GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    static bool WaitWindowGone(IntPtr hwnd, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!Win32.IsWindow(hwnd)) return true;
            Win32.Pump(50);
        }
        return !Win32.IsWindow(hwnd);
    }

    static bool WaitProcessExit(uint pid, int timeoutMs)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            p.Refresh();
            if (p.HasExited) return true;
            p.WaitForExit(timeoutMs);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; } // 进程已不存在
        catch { return false; }
    }

    static void CloseTarget(TargetInfo? t)
    {
        if (t == null) return;
        try
        {
            if (Win32.IsWindow(t.Hwnd))
                Win32.PostMessageW(t.Hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            WaitWindowGone(t.Hwnd, 2500);
            try { using var p = Process.GetProcessById((int)t.Pid); if (!p.HasExited) p.Kill(); } catch { }
        }
        catch { }
    }

    static string Payload(string tag) => $"P2-{tag}-{Guid.NewGuid():N}"[..30];

    static void Gate(string id, Func<(bool, string)> body)
    {
        try
        {
            var (pass, detail) = body();
            Criteria.Add((id, pass, detail));
            LogLine($"[{id}] {(pass ? "PASS" : "FAIL")} {detail}");
        }
        catch (Exception ex) { Criteria.Add((id, false, $"异常：{ex.Message}")); LogLine($"[{id}] 异常 {ex}"); }
    }

    static void Supp(string id, Func<(bool, string)> body)
    {
        try
        {
            var (pass, detail) = body();
            Criteria.Add((id, pass, $"（补充，非门禁）{detail}"));
            LogLine($"[{id}] {(pass ? "PASS" : "FAIL")} {detail}");
        }
        catch (Exception ex) { Criteria.Add((id, false, "（补充，非门禁）异常：" + ex.Message)); }
    }

    static void Supp(string id, string why) => Criteria.Add((id, false, $"（补充，非门禁）{why}"));

    static void LogLine(string s) { LogLines.Add(s); }

    static void WriteReport(string il)
    {
        var gates = Criteria.Where(c => !c.Id.StartsWith("S")).ToList();
        var supps = Criteria.Where(c => c.Id.StartsWith("S")).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# P2 提权焦点恢复试验：自动判据报告（run 模式）");
        sb.AppendLine();
        sb.AppendLine($"日期：{DateTime.Now:yyyy-MM-dd HH:mm:ss}；OS：{Environment.OSVersion.VersionString}；runner 完整性：{il}");
        sb.AppendLine($"UAC：ConsentPromptBehaviorAdmin={Reg("ConsentPromptBehaviorAdmin")}（0=管理员静默提权），EnableLUA={Reg("EnableLUA")}（1=UIPI 生效）");
        sb.AppendLine();
        sb.AppendLine("## 门禁判据（对齐工单五条）");
        sb.AppendLine();
        sb.AppendLine("| 判据 | 结果 | 证据 |");
        sb.AppendLine("|---|---|---|");
        foreach (var (id, pass, detail) in gates)
            sb.AppendLine($"| {id} | {(pass ? "PASS" : "FAIL")} | {Md(detail)} |");
        sb.AppendLine();
        sb.AppendLine("## 补充证据（真实记事本，非门禁）");
        sb.AppendLine();
        foreach (var (id, pass, detail) in supps) sb.AppendLine($"- **{id}**：{(pass ? "PASS" : "FAIL")} — {detail}");
        sb.AppendLine();
        sb.AppendLine("## 阶段日志");
        sb.AppendLine();
        foreach (var l in LogLines) sb.AppendLine($"- {Md(l)}");
        File.WriteAllText(ReportPath, sb.ToString());
        LogLines.Add($"报告已写 {ReportPath}");
    }

    static string Md(string s) => s.Replace("|", "\\|").Replace(Environment.NewLine, " ");

    static string Reg(string name) =>
        Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", name, null)?.ToString() ?? "?";
}
