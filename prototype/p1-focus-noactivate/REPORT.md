# P1 焦点试验报告

问题：WPF 无边框面板能否**不抢焦点**显示、按模式切换**可聚焦**、停靠**屏外不销毁**并复用同一 HWND？
日期：2026-10-03。系统：Windows 10.0.26300 x64（Win11）。SDK：.NET 10.0.401。构建：Release，零警告。

## 复现

```bash
cd prototype/p1-focus-noactivate
dotnet build -c Release
./bin/Release/net10.0-windows/P1FocusProbe.exe            # 自动序列，报告落同目录 p1-report.md
./bin/Release/net10.0-windows/P1FocusProbe.exe --manual   # 人工复核：点击/打字/IME/Esc
```

## 自动判据（9/9 通过）

见运行输出 `bin/Release/net10.0-windows/p1-report.md`：显示/呼出/停靠全程前台 HWND 不变（A1/A2/D2）、
初始停靠与再停靠均 IsWindowVisible=TRUE 且矩形在屏外（A1b/D1）、
输入态清 WS_EX_NOACTIVATE 后 AttachThreadInput 级联激活成功且 SendInput 真实键入到达 TextBox（B1/B2）、
Esc 经 KeyDown 清输入并归还前台（C1/C2）、再呼出复用同一 HWND（D2）。

## 结论

1. 浏览态不抢焦点可行：`ShowActivated=False` + `WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW`（WPF `ShowInTaskbar=False`）。
2. 输入态可聚焦可行：清除 `WS_EX_NOACTIVATE` → 聚焦 → 激活失败时 `AttachThreadInput` 级联（legacy focus_paste.rs 同思路）。
3. 停靠不销毁可行：移屏外保持可见，同 HWND 再呼出落地，几何回读正常。
4. 键盘输入走真实系统管线（SendInput KEYEVENTF_UNICODE）到达面板 TextBox。
5. 本机验证范围：普通权限进程 → 普通窗口前台。提权→管理员目标恢复归 P2。

## 待人工/后续

- 鼠标点击卡片不激活、真实打字与中文 IME、多显示器/分数 DPI：`--manual` 及 T01/T04 真机步骤覆盖。
- 屏外坐标被系统钳位到 -32768（试验停靠位远超旧版「右缘外 20 DIP」；T01 按旧版策略实现，判据不受影响）。

## 结论去向

- ADR-0002（面板焦点与停靠策略）。
- T01 工单（`.scratch/wpf-rewrite/issues/01-*.md`）按此实现 PanelWindow。
