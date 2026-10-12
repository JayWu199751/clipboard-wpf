# 开机启动后面板呼出被遮挡：排障与验证

日期：2026-10-12。

## 现场

生产实例 PID 8852，`%APPDATA%\ClipboardTool\diag.log` 显示：快捷键已注册，快捷键/托盘菜单/第二实例呼出均到达，矩形回读正确且窗口可见。原生探针确认 DWM 未隐藏窗口，但中心命中后面的普通窗口；EnumWindows 中面板实际 Z 序也在普通窗口后方，虽然扩展样式仍带置顶位。用户确认排障期间一度能看到面板，随后仍检出遮挡。

反馈命令：`pwsh.exe -NoProfile -File .scratch/autostart-summon/probe.ps1`。现场结果：`FAIL 呼出后面板没有出现在屏幕中心命中窗口`。清理后的探针只发送真实呼出请求并读取面板状态，无改样式、重绘、调整尺寸等临时动作。当前用户旧实例未替换，因此不能把隔离验证说成已修好正在运行的安装版本。

## 修复

- `WindowPlacer.PlaceSummonDip` 原来使用 SWP_NOZORDER，保留错误层级。改为 NOTOPMOST → TOPMOST，两步均带 NOACTIVATE；停靠沿用原路径。
- 只重复指定 TOPMOST、或 TOPMOST → TOP，未通过“仍有置顶位但已被遮挡”的回归测试；NOTOPMOST → TOPMOST 通过。
- 原生落位调用失败不再被矩形/可见性检查掩盖；日志追加 placed 结果。
- `App.SummonPanel` 对已呼出面板不再直接跳过：重新落位与刷新层级，保留协调器输入模式和原焦点快照。初次呼出仍走既有状态机重置流程。

## 自动证据

- 新增 `WindowSummonOrderTests` 两种原生遮挡场景：修复前均在呼出后中心命中断言失败，修复后通过；同时验证前台窗口不变。
- `dotnet test ClipboardTool.Tests/ClipboardTool.Tests.csproj --no-restore --verbosity minimal`：487 通过，0 失败，0 跳过。
- `dotnet build ClipboardTool.sln -c Release --no-restore --verbosity minimal`：0 警告，0 错误。
- `dotnet run --project tools/E2eT15/E2eT15.csproj -- --evidence .scratch/autostart-summon/evidence`：完整隔离流程通过。真实 App 初次呼出后创建原生置顶遮挡窗口，再经托盘菜单显示，实际中心重新命中 WPF 面板，面板不激活。旧 App 的 already-visible 跳过分支会在此超时，修复后通过。覆盖普通/详细日志、第二实例、退出与异常流程。测试使用独立存档与实例通道。
- E2E 遮挡夹具使用原生窗口，避免新增 WPF 窗口对验证进程退出生命周期的影响；打开托盘菜单本身可能改变前台，故 E2E 验证面板未激活，严格前台不变由原生窗口测试验证。

## 安装包

`pwsh.exe -NoProfile -File installer/build-installer.ps1` 已成功，日志见 [installer-build.log](installer-build.log)。执行前核对清理目录为本仓库的 `installer/publish`。

- 路径：`C:\Users\10854\Code\Clipboard-WPF\installer\ClipboardTool-Setup.exe`
- 大小：62,968,717 字节（v1.0.1 版本元数据更新后重新构建）。
- SHA256：`FD2E8E3D52A7837BAFC15D76E3708514D6841A46225BCC9FC44E9568F2E93614`
- 未安装、未修改用户设置与计划任务。重启后的真实登录启动、物理快捷键和托盘操作仍需用户安装后验收。

用户随后要求提交、推送并上传 Release：同步程序集、应用清单、安装包版本为 1.0.1；重新构建安装包并额外执行 Release 全量测试，487/487 通过。发布说明见 `docs/releases/v1.0.1.md`。
