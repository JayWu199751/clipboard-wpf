# P2 提权焦点恢复试验报告

问题：**提权（管理员）进程能否向普通与管理员目标窗口恢复焦点并注入 Ctrl+V？**
日期：2026-10-03。系统：Windows 10.0.26300 x64（Win11），2560×1600@175%。SDK：.NET 10.0.401。构建：Release，零警告。
机器 UAC 策略：`ConsentPromptBehaviorAdmin=0`（管理员静默提权，无提示）、`EnableLUA=1`（UIPI 生效）。

## 结论（TL;DR）

1. **提权进程 → 普通目标、提权进程 → 提权目标，恢复+注入全链路均成功**（探针靶窗与真实记事本各自通过），
   单次恢复+注入 7–29ms，激活级联首轮 `SetForegroundWindow` + `AttachThreadInput` 一次成功。
2. **UIPI 高→低/高→高注入畅通；低→高被系统过滤且 SendInput 返回值仍为 4**（负控证实）——
   「注入是否成功」只能靠目标侧回读判断，不能信返回值/GetLastError，与 02-spec/02 §2 的告诫一致。
3. 失效句柄（窗口已销毁 / HWND 复用致 PID/TID 不匹配）在 restore 阶段被拒绝，**零注入**，
   `restore`/`paste` 两阶段失败分开报（`{ stage, reason }`），失败路径无「向前台随便粘贴」降级。
4. legacy `focus_paste.rs` 的算法（快速路径零操作、最小化恢复、4ms 步进回读、有限激活级联、
   AttachThreadInput 对称解绑、SendInput 数量校验）在 C# 移植下行为等价，直接作为 T02 FocusAdapter 的实现蓝本。

## 复现

```bash
cd prototype/p2-elevated-focus
dotnet build -c Release
EXE='C:\...\prototype\p2-elevated-focus\bin\Release\net10.0-windows\P2Focus.exe'
# 1) 普通侧：启动靶 A（medium）、靶 B（静默提权）、普通记事本 N1、提权记事本 N2，各写握手 JSON
powershell -NoProfile -ExecutionPolicy Bypass -File start-target.ps1 -Exe "$EXE"
# 2) 提权侧：静默提权运行编排，门禁判据报告落 bin/Release/net10.0-windows/p2-report.md（退出码=失败判据数）
powershell -NoProfile -ExecutionPolicy Bypass -File run-runner.ps1 -Exe "$EXE"
# 3) 负控（普通权限运行）：medium 进程向 high 靶直接 SendInput，报告落 p2-negative-report.md
./bin/Release/net10.0-windows/P2Focus.exe negative
```

靶窗内容证据不依赖跨进程读窗口文本：每个靶窗进程自记事件日志
（`%TEMP%\p2-elevated-focus\target-<名>-events.log`，`len + SHA1(UTF-16)` 指纹，50ms 轮询 + 2s 心跳），
跨完整性可读，注入是否到达由靶窗自己作证。

## 判据表（连续两轮运行，2026-10-03 11:12:41 与 11:13:22，退出码均 0）

| 工单判据 | 结果 | 证据 |
|---|---|---|
| 1. 提权→普通目标：恢复+注入落靶 | **PASS**（C1，探针靶 A + S1 真实记事本） | runner 完整性 high(0x3000)、靶 A medium(0x2000)；恢复后前台 root、GUI 焦点、内容指纹三重回读全中；记事本经 `WM_GETTEXT@RichEditD2DPT` 回读到虚构载荷 |
| 2. 提权→管理员目标：同样成功 | **PASS**（C2，探针靶 B + S2 真实提权记事本） | 靶 B 完整性 high(0x3000)（token 回读）；同上三重回读全中；UIPI 高→高允许，实测确认 |
| 3. 目标句柄失效：零注入、阶段分开报 | **PASS**（C3） | B 正常 WM_CLOSE 后失效快照→`restore:restore_failed` 且 SendInput 计数=0；HWND 复用防御（有效 HWND+死 PID/TID）同样拒绝、零注入；A 关闭后失效快照同样拒绝 |
| 4a. 恢复最小化目标 | **PASS**（C4a） | `IsIconic=true`→恢复后 `IsIconic=false`、前台/焦点/内容三重回读全中（26–30ms） |
| 4b. 快速路径零操作 | **PASS**（C4b） | 目标仍前台且焦点未变时 `fastpath=True activate=0 attach=0 alt=0 ms=0`，注入照常落靶，前台/焦点未变 |
| 5. 失败路径可观测、无静默降级 | **PASS**（C5 + C3） | `paste` 阶段失败可演示（stage=paste/reason=paste_send_failed，恢复成功但零注入）；restore 失败时前台与面板牺牲编辑框未受扰（不存在「向当前前台粘贴」的降级路径） |
| 负控：低→高注入被过滤 | **PASS**（预期行为） | medium 进程向已验证为前台的 high 靶 SendInput Ctrl+V：**返回 4/4**，但靶窗自记日志显示内容从未到达、牺牲窗干净、前台保持 |

明细见自动报告：`bin/Release/net10.0-windows/p2-report.md`（门禁 C1–C5 + 补充 S1/S2）与
`p2-negative-report.md`（负控）。

## 关键约束与实现要点（给 T02 FocusAdapter / ADR 的输入）

1. **快照四元组（顶层 HWND/焦点 HWND/PID/TID）必须整组校验**：HWND 是可复用资源，
   仅 `IsWindow` 不足以防错窗口；PID/TID 不匹配即拒绝且不发任何输入。
2. **每步激活用回读验证**：`SetForegroundWindow` 返回值不作数，`wait_for_foreground`
   以 4ms 步进轮询前台归属（成功即刻返回，预算 48ms）；激活级联依次为
   ASFW 授权 → 无害 Alt 取输入权 → `SwitchToThisWindow` → `ShowWindowAsync+SetWindowPos`，
   每级之间都回读。
3. **注入数量校验**：`SendInput` 必须返回 4 才算发出；但即使返回 4 也可能被 UIPI 静默过滤
   （负控），**最终成败以目标侧回读为准**（正式实现中即 02-spec 的结果契约文案与 `panel:focus-error` 同源）。
4. **AttachThreadInput 必须对称解绑**；失败（跨完整性被拒）时级联仍继续（探针实测 attach=1 成功路径，
   解绑对称执行）。
5. **注入目标必须是恢复验证过的目标**：restore 失败绝不降级向前台粘贴（C3/C5 证据）。
6. 探针工程结构：`FocusRestore.cs` 是 legacy 算法的忠实移植（含 `RunStats` 计数器），
   `P2_FORCE_PASTE_FAIL=1` 环境变量仅用于演示 paste 阶段失败路径，正式实现不得保留。

## 未覆盖项 / 待人工

1. **真实键盘到达**：本试验注入是合成输入（SendInput），「用户真实按键→OS 送达」不在范围（T02 真机步骤覆盖，
   见记忆 `win-verification-constraints`：热键链需 PostMessage 直投 + 人工项单列）。
2. **多显示器/分数 DPI 交叉**：本机单屏 175%，跨 DPI 恢复行为归 P5/T01 真机项。
3. **提权记事本二次验证**：N2 走 System32 notepad.exe（WinUI 版，编辑器 `RichEditD2DPT`），
   经 `WM_GETTEXT` 回读成功；未覆盖 UWP 打包形态之外的编辑器（如 Visual Studio 提权窗口）——机制上同为
   高→高注入，风险低。
4. **靶窗偶发冻结（探针侧观察，非能力缺陷）**：早期版本由 runner 拉起子进程后立即激活，
   靶 B 的 UI 线程可复现地不响应（心跳线程存活、WM_CLOSE 不达）；改为「普通侧预启动 + 充分初始化后由
   runner 激活」后连续两轮全绿。正式实现的启示：**恢复目标应处于稳定运行态，粘贴链路对激活失败
   必须有重试与明确报错**（与判据 5 一致）。
5. 本机 UAC 处于「静默提权」策略（ConsentPromptBehaviorAdmin=0），提权启动无需人工点 UAC；
   在默认策略机器上 `start-target.ps1` 的 B/N2 步骤会弹一次 UAC，属预期。

## 结论去向

- ADR-0003（焦点恢复与注入策略，本仓库 `docs/adr/0003-提权焦点恢复与注入策略.md`）。
- 工单 03（T02 文字闭环）按此实现 `Infrastructure.Windows.FocusAdapter`：`FocusRestore.cs` 即蓝本。
- 真机步骤中「管理员目标粘贴」验收项的预演证据（本报告判据表）。
