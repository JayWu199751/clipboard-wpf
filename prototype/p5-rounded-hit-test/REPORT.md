# P5 跨 DPI 圆角穿透试验报告

问题：36 DIP 圆角外壳在分数 DPI 下，圆角外透明部分鼠标**穿透**到下层窗口、圆角内正常交互；
浏览态（NOACTIVATE）与输入态（激活）分别成立；命中几何由当前 radius 与 DPI 决定。

日期：2026-10-03（自动序列末次运行 2026-10-04 00:14）。系统：Windows 10.0.26300 x64（Win11），
真机 2560×1600 @175% 单显示器，DPI awareness=PerMonitorV2。SDK：.NET 10.0.401。构建：Release，零警告。

## 复现

```bash
cd prototype/p5-rounded-hit-test
dotnet build -c Release
dotnet test P5GeometryTests.csproj                       # 几何纯函数 19 例（四档 DPI 参数化）
./bin/Release/net10.0-windows/P5RoundedHitProbe.exe      # 自动序列，报告与截图落 bin 同目录后自动退出
./bin/Release/net10.0-windows/P5RoundedHitProbe.exe --manual   # 人工复核：角内/角外点击、键入、穿透观察
```

## 判据表（工单 4 条）

| # | 判据 | 结论 | 证据 |
|---|---|---|---|
| ① | 角外点击落到下层窗口（前台/焦点变化可观测） | **通过** | 浏览态 B3/B6（靶窗计数+1、前台不变=已是靶窗）；输入态 C2（前台探针→靶窗+靶计数+1）；归因控制 C5（仅 alpha，通过）与 C3（仅 HTTRANSPARENT，未过→见核心结论 2） |
| ② | 角内（搜索井、卡片、页脚）正常接收点击与键盘 | **通过** | 浏览态 B1/B2/B4（井/卡/页脚计数、前台不变不激活）；输入态 C1（点击激活+SendInput 真实键入 "p5" 到达搜索框）、C4（重复激活）；紧贴弧边界内侧 B5（0.32R，壳内可达） |
| ③ | 四角 × 100/125/150/175% 命中区域与视觉圆角一致 | **175% 真机通过；100/125/150% 数学通过、真机待人工** | 真机 175%：A4 弧扫 64 点（4 角×8 方向×内外）与纯函数全一致 + B5/B6 真实点击把弧边界夹紧到 ±4% R；四档换算与判定：19 例参数化单测（36 DIP→36/45/54/63 px）；运行时行为在单机上无法改变系统 DPI，未实测（见未覆盖项） |
| ④ | 输入态与浏览态均正确；多显示器负坐标屏成立 | **双态通过；负坐标消息级通过、真实多屏待人工** | 双态：B 组（NOACTIVATE）与 C 组（激活）全过；负坐标：A5 窗口实际移到 (-300,-200) + 有符号 lParam 解析经真实 WndProc 判定一致（消息注入模拟，非真实多屏） |

自动断言 17 条：16 通过，1 未过（C3 为**设计中的负发现**，见下）。逐条证据见
[artifacts/p5-report.md](artifacts/p5-report.md)，截图 [artifacts/](artifacts/)（真机 175% 探针实拍 + 四档 DPI 命中几何示意，后者为模拟渲染并已在图内标注）。

## WM_NCHITTEST 处理结论

1. **穿透机制归因（本试验最重要产出）**：控制矩阵 C3（角部改不透明→只剩 HTTRANSPARENT 机制）**未过**——
   真实点击被丢弃、前台变 NULL；C5（关钩子→只剩分层 alpha）**通过**。结论：**弧外真实点击的跨线程/跨进程
   穿透由分层窗口逐像素 alpha（AllowsTransparency 的 0 alpha 区）承担**，浏览态/输入态一致。
   `HTTRANSPARENT` 对真实鼠标输入**只在同一线程队列内向下转发**；跨线程时点击被系统丢弃
   （与 Raymond Chen 对 HTTRANSPARENT 的语义描述一致）。注意 `WindowFromPoint` 查询会尊重 HTTRANSPARENT
   （C3 证据中返回靶窗），不能用查询结果推断真实点击路由。
2. **钩子的正确角色**：WM_NCHITTEST 钩子按纯函数（radius DIP × `VisualTreeHelper.GetDpi` 实时值）判定，
   弧内返回 HTCLIENT、弧外返回 HTTRANSPARENT。价值：a) 与视觉弧同一 radius 常量、严格同源；
   b) 抗锯齿 fringe（alpha 1–254）与任何 alpha>0 越界像素的点击被**丢弃**而非误落入面板（浏览态不产生误交互）；
   c) 弧内命中显式确定，不依赖 WPF 默认。但**不能**把 HTTRANSPARENT 当作跨进程穿透机制。
3. **几何口径**：窗口本地物理坐标 = lParam 屏幕物理坐标 − `GetWindowRect` 原点（lParam 低/高 16 位
   **有符号**解析，负坐标屏必须）；radius 物理 px = 36 × 当前 DPI（175% → 63 px）；恰在弧上归弧内。
4. **生产形态建议（给 11 票）**：`AllowsTransparency=True`（穿透机制）+ WM_NCHITTEST 钩子（几何确定）+
   PerMonitorV2；禁止整窗 `WS_EX_TRANSPARENT`（搜索井/卡片/页脚必须可点，工单明确）；DPI 变化监听
   `DpiChanged` 更新缓存。

## 各 DPI 结果

| DPI | radius 物理值 | 几何判定 | 运行时行为 | 证据 |
|---|---|---|---|---|
| 100% | 36 px | 单测 4/4 组通过 | **真机待人工** | 单测参数化 + [artifacts/p5-overlay-100pct.png](artifacts/p5-overlay-100pct.png)（模拟渲染） |
| 125% | 45 px | 同上 | **真机待人工** | 同上 [125pct](artifacts/p5-overlay-125pct.png) |
| 150% | 54 px | 同上 | **真机待人工** | 同上 [150pct](artifacts/p5-overlay-150pct.png) |
| 175% | 63 px | 单测 + 真机弧扫 64 点 + 边界夹紧 | **真机实测通过** | [p5-report.md](artifacts/p5-report.md) A1–A4/B5/B6 + [探针实拍](artifacts/p5-probe-175pct.png) |

单机无法更改系统 DPI 档位；运行时跨档行为未实测（诚实记录，未伪造）。数学口径已由四档参数化单测钉死，
风险点（用系统常量而非每窗口实时 DPI）已凭 175% 真机 GetDpi=1.75 回读验证排除。

## 一次性异常记录

某轮运行中 B2 一次注入点击计了 3 次 down（当轮 B5 壳计数同步多 2，复跑未再现）。加做 D1 诊断：
3 次注入点击（间隔 600ms > 双击时限）→ 恰 3 次 down、ClickCount 全为 1
（[p5-report.md](artifacts/p5-report.md) D1）。判定为瞬态环境噪声，非 WPF/钩子行为；
卡片交互语义（单击仅选中）的严格验证归 T02/T04 已有覆盖。

## 未覆盖项（真机待人工）

- 100/125/150% 系统档位下的运行时穿透与激活行为（需改显示设置）。
- 真实多显示器负坐标屏（A5 为窗口移位 + 消息注入模拟，已注明）。
- 物理鼠标/触屏/笔的真实点击手感（本试验用 SendInput 注入）与真实键盘 WM_KEYDOWN 路径。
- IME 组合中点击角外的行为；半透明阴影类特效（若有）对 fringe 命中的影响。
- 分层窗口（AllowsTransparency）在目标机器的渲染开销（性能归 T09）。

## 结论去向

- [ADR-0006 面板窗口命中穿透策略](../../docs/adr/0006-面板窗口命中穿透策略.md)。
- 11 票（T07 系统集成）PanelWindow 命中策略按「允许透明 + WM_NCHITTEST 纯函数钩子」接入；
  F17 验收矩阵「角外点击下层」的预演证据即本报告判据①。
