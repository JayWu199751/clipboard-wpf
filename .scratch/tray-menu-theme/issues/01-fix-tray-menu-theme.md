Status: ready-for-agent
Type: task
Execution: resolved

# 01：修复托盘菜单主题

## 事实与原因

原菜单通过 CreatePopupMenu / AppendMenuW / TrackPopupMenuEx 绘制，未接入任何应用主题资源。
最小复现：STA 创建真实 TrayIconHost，调用原 BuildMenu 后用 GetMenuInfo 读取菜单背景。

命令：`dotnet test ClipboardTool.Tests/ClipboardTool.Tests.csproj --filter FullyQualifiedName~TrayMenuThemeTests`。
修复前结果：1 例失败，背景句柄为零；消息「实际托盘菜单没有应用背景画刷，仍使用系统默认菜单皮肤，无法匹配应用暗色」。

此问题范围小且原因直接可观测，省略历史二分、额外诊断插桩与多假设试验。

## 实现

- TrayIconHost 通过渲染回调呈现菜单，命令继续经 MenuItemSelected 的原 id 分发。
- TrayContextMenu 复用应用资源字典，菜单和子菜单模板中的全部颜色由 DynamicResource 解析。
- App 负责创建和释放菜单；主题仍由 ThemeService 与 ApplyPanelTheme 驱动。
- 保留工作区既有 Explorer 重启后托盘重建、应用图标等修改。

## 验证

- 主题回归：5/5 通过，覆盖实际菜单与子菜单颜色、替换资源后的更新、系统应用/任务栏明暗相反、分隔线与勾选、命令只分发一次。
- 全量测试：470/470 通过，零跳过。
- Release：`dotnet build ClipboardTool.sln -c Release --no-restore` 通过，零警告、零错误。
- WPF 实际控件渲染：亮暗菜单与子菜单 PNG 均已检查，无截字或布局溢出。
- 隔离桌面 smoke：真实 Win32 托盘右键消息进入宿主后打开 WPF Popup；亮暗各验证背景、右方向键展开子菜单、命令单次分发并关闭、Esc 关闭，共 10 项通过。
- 验证工具：[preview/Program.cs](../preview/Program.cs)，`dotnet run --project .scratch/tray-menu-theme/preview/Preview.csproj -- --smoke`。
- 渲染：[暗色](../evidence/tray-menu-dark.png)、[亮色](../evidence/tray-menu-light.png)。
- 验证边界：未通过真实鼠标右键点击托盘、点击菜单外部或跨 DPI 矩阵验收。当前既有应用实例未替换，用户手动安装新安装包后生效。

## Comments

2026-10-08：完成修复。原 Win32 菜单没有应用主题渲染入口；将呈现交给 WPF 后，菜单和子菜单复用面板同一资源树。原命令 id、主题权威、托盘图标明暗判定及既有托盘恢复修改保留。

2026-10-08：按用户要求构建安装版，未执行安装。
- 构建：`pwsh.exe -File installer/build-installer.ps1`，自包含 win-x64 发布与 NSIS 打包均成功。
- 产物：`C:\Users\10854\Code\Clipboard-WPF\installer\ClipboardTool-Setup.exe`，62,960,878 字节（60.0 MiB）。
- SHA-256：`64DB7595714439876C55CEE516B05ADC7B85814B8202CA685ADDBB252C7CD938`。
- 发布程序集确认含 `themes/traymenu.baml`；包含 WPF 桌面运行时 `PresentationFramework.dll`。
- 构建日志：[installer-build.log](../evidence/installer-build.log)。
