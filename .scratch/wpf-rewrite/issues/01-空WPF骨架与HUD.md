Status: ready-for-agent
Execution: resolved
Type: task

# T01：空 WPF 骨架 + 可复用 HUD + 临时托盘/呼出 + 基线采样

## 功能编号

F15、F16（尺寸公式与工作区居中）、F41–F44（HUD 骨架，按 `02-spec/04-界面还原规格.md`）、
F29/F30（临时托盘）、F40（采样设施雏形）。

## 阻塞

无。前置已解除：P1 焦点试验 9/9 通过（[prototype/p1-focus-noactivate/REPORT.md](../../../prototype/p1-focus-noactivate/REPORT.md)），
[ADR-0002](../../../docs/adr/0002-面板焦点与停靠策略.md) 已定焦点策略；.NET SDK 10.0.401 已装
（注意：新 shell 需 `export PATH="/c/Program Files/dotnet:$PATH"`，或等 PATH 刷新）。

## 目标（可运行结果）

1. 根目录 `ClipboardTool.sln` + 五工程骨架：`ClipboardTool.Domain`、`ClipboardTool.Application`、
   `ClipboardTool.Infrastructure.Windows`、`ClipboardTool.Presentation.Wpf`、`ClipboardTool.Tests`；
   `global.json` 锁 SDK 10.0.401；`.gitignore` 覆盖 bin/obj/.vs。
2. 发布构建（`net10.0-windows` x64）可运行：全局 `Ctrl+Shift+V` 呼出无边框面板（RegisterHotKey 雏形），
   浏览态不抢焦点、Esc/再次呼出键停靠（ADR-0002 策略）。
3. HUD 按 `04-界面还原规格.md` 画三段骨架：搜索头（36 DIP 井）、虚拟化列表（先占位条目）、页脚；
   明暗两套 ResourceDictionary（色值按规格 token 表）；外壳 36 DIP 圆角、2 DIP #757575、四边 1 DIP。
4. 呼出几何：显示器全屏物理高÷DPI 得 DIP 高×7/8（四舍五入）、宽=高÷2（四舍五入）、光标所在屏工作区居中、主屏兜底。
5. 临时托盘：左键单击抬起呼出、右键菜单（显示面板/退出），图标临时加载，五档精确版归 T07。
6. 基线采样 console 工具（Infrastructure 或独立 `tools/` 工程）：按间隔对指定 PID 采样
   PrivateMemorySize64/WorkingSet64/HandleCount/GDI 对象出 CSV，供性能计划用。

## 红绿测试（Tests 工程，领域层）

- Geometry 尺寸公式：1080p@100%→473×945；1440p@200%→315×630；1440p@175%→360×720；
  工作区不够时主屏兜底；停靠坐标（右缘外 20 DIP、y=工作区顶、无显示器 -10000,0）。
- HotkeyPlan：默认呼出键 Ctrl+Shift+V；目标键集合推导与差量骨架（注册执行归 Infrastructure）。

## 允许改动范围

根目录新建工程文件与 `.gitignore`；五工程内部新建；不改 `04-reference/`；HUD 视觉值只照抄规格表，不自由发挥。

## 真机步骤（完成判据）

1. `dotnet build -warnaserror` 全量零警告；`dotnet test` 全绿。
2. Release 自包含发布运行：面板出现在光标所在屏工作区中心、尺寸与公式一致；
   呼出前后前台窗口不变（记事本输入法光标仍在）；任务栏与 Alt+Tab 无按钮；Esc 停靠后再呼出正常。
3. 采样工具对本进程出 CSV，含四类指标列。

## 证据记录

- **实现提交号**：91aef9f（T01 全部实现）；评审修复在该提交内完成（两轴 code-review 后落地）。
- **测试输出摘要**：`dotnet build -warnaserror` 全量零警告零错误；`dotnet test` 18/18 通过
  （PanelGeometry 尺寸 3 例：1080p@100%→473×945、1440p@200%→315×630、1440p@175%→360×720；
  落位/停靠 7 例：工作区居中、恰好放下不兜底、过小主屏兜底、无光标屏主屏兜底、右缘外 20 DIP、无显示器 -10000,0；
  HotkeyPlan 8 例：默认键、差量增删、浏览态 Esc 计划、DisplayName 推导）。
- **真机证据**（本机 2560×1600@175%，DIP 空间换算后物理回读，DPI 感知探针）：
  - 初始停靠 `(2595, 56)` = 工作区右缘 2560 + 20 DIP×1.75、y=工作区顶 ✓；
  - 呼出落地 `(929, 128) 700×1400 物理px` = 400×800 DIP（1600/1.75×7/8=800）按 DIP 工作区居中 ✓；
  - 呼出/停靠全程前台 HWND 不变 ✓；扩展样式 `0x8080008` = NOACTIVATE|TOOLWINDOW|TOPMOST（任务栏与 Alt+Tab 无按钮）✓；
  - WM_HOTKEY 投递序列：呼出→Esc 停靠→再呼出，全程同一 HWND（停靠不销毁）✓，Esc 键按状态差量注册/注销（F18 让位模型）✓；
  - 采样工具对本进程出 CSV：`artifacts/smoke-sample.csv`，含 private_memory_bytes/working_set_bytes/handle_count/gdi_objects 四列 ✓。
- **发布**：`dotnet publish -c Release -r win-x64 --self-contained` → `publish/ClipboardTool/`（未压缩 141MB；ADR-0001 的 70–100MB 口径是 NSIS 压缩后安装包，待 T08 落地核对）。
- **待验证项（真机人工）**：
  1. 真实键盘按下 Ctrl+Shift+V / Esc 的系统级 WM_HOTKEY 送达（本会话 SendInput 无法注入交互桌面输入流；注册参数已核实，WM_HOTKEY 处理链已用消息投递验证）；
  2. 记事本场景下呼出前后输入法光标仍在（前台不变已自动验证，IME 归 P2/人工）；
  3. 多显示器/分数 DPI 交叉呼出（P5 范围）；
  4. 点击面板不激活、托盘左键单击呼出（托盘回调路径同 WM_HOTKEY，未真机点击）。
- **评审处理记录**：Standards/Spec 两轴各 4 项发现，除两项骨架种子（CommandResult/EffectiveKeys，T02/T04 消费）与工单流程元数据豁免外，全部修复：
  键名展示改由 HotkeyPlan 推导（提示=行为硬约束，搜索井 chip 隐藏至 T04）、呼出落地补三条件回读、FocusAdapter 显式 TOOLWINDOW、
  PlatformTarget=x64、PanelWindow 落位去重、ScreenMetrics 参数团改 PixelRect；修复过程中发现并修掉一个真实 bug：
  DockStateChanged 在 Attach 之前触发导致热键注册到空 HWND（执行者现暴露 Attached 并在未挂接时忽略 ApplyPlan）。

## Answer

T01 交付完成（提交 91aef9f）。五工程骨架 + `ClipboardTool.sln`（经典格式，.NET 10 的 `dotnet new sln` 默认产出 .slnx，已按工单要求重建为 .sln）+ `global.json`（10.0.401, latestPatch）+ `.gitignore` 扩充（TestResults/publish/artifacts/binlog）。

要点：
1. **几何**（F15/F16）：Domain 纯函数 `PanelGeometry`——尺寸=物理高÷DPI×7/8（AwayFromZero 四舍五入）、宽=高÷2；呼出=光标屏工作区居中、放不下主屏兜底；停靠=右缘外 20 DIP、y=工作区顶、无显示器 (-10000,0)。物理换算（逐屏 DPI）与落地回读（可见/矩形相符/工作区内三条件，失败重试一次）归 Infrastructure。
2. **键位**：Domain `HotkeyPlan` 只推导目标集合与差量（含 `DisplayName` 提示推导，消灭写死键名）；Infrastructure `HotkeyExecutor` 持有已生效键集合并执行 Register/Unregister；Presentation 按面板状态重算计划——停靠态 {Ctrl+Shift+V}，浏览态 {Ctrl+Shift+V, Esc}（F18 让位模型）。
3. **面板窗**（ADR-0002）：`ShowActivated=False` + NOACTIVATE/TOOLWINDOW 常驻，停靠屏外不销毁同 HWND 复用；36 DIP 圆角 + 2 DIP #757575 壳 + 四边 1 DIP 留白；60/*/30 三行；Topmost 有 legacy `alwaysOnTop` 依据。
4. **HUD**：搜索井 36 DIP 胶囊（Border.Strong 描边、图标 15、主题按钮 24/图标 15 占位、搜索键 chip 留位至 T04）、虚拟化 ListBox（Recycling，200 占位卡，卡片 CornerRadius12/上下12左右16/hover 仅边加深/选中 tint+细边）、页脚（顶部分隔线、mono 10.5、Esc 停靠 chip）。明暗两套 ResourceDictionary 逐值对齐 theme.css token，默认暗色。
5. **托盘**：Shell_NotifyIcon 临时实现，左键单击抬起呼出、右键菜单（显示面板 <呼出键展示>/退出），隐藏窗口收回调。
6. **采样**：`tools/BaselineSampler`——`--pid/--interval-ms/--duration-ms/--out`，四指标 CSV，自动建输出目录，Ctrl+C 优雅退出。

后续工单衔接：搜索井 chip/页脚注册表归 T04，三态主题偏好切换归 T07，粘贴链与真实历史归 T02，五档托盘图标归 T07。
