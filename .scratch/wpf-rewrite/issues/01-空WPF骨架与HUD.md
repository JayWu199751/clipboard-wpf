Status: ready-for-agent
Execution: claimed
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

（完成后填：实现提交号、测试输出摘要、真机截图/日志路径、待验证项）
