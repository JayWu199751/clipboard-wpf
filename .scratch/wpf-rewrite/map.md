# WPF 重写：事实地图与工作计划

资料入口：`docs/wpf-rewrite-kit/`（基准提交 `b05ba281`，2026-10-03）。
目标：把 ClipboardTool 从 Tauri 2 + React 19 + Rust 重写为 C# + 原生 WPF，功能/行为/旧存档等价，动机是降低常驻内存。

## 已作决策（资料包既定，不重新访谈）

| 决策 | 依据 |
|---|---|
| C# + 原生 WPF，`net10.0-windows`、x64；UI 仅 XAML，不嵌 WebView2 | 02-spec/03；用户已拍板 |
| 存档目录 `%APPDATA%\ClipboardTool`、JSON 键名、条目 id、时间单位（epoch 毫秒）、PNG 文件名 `<id>.png` 全部保持 | 02-spec/02 契约 |
| 条目身份只看内容；置顶块/普通块落位；200 上限含置顶 | ADR-0003/0004，F03–F05 |
| 粘贴链路六步顺序与四种结果文案 | ADR-0005，F11–F13 |
| 已生效键集合由执行注册者维护；模式单一串行写入者 | ADR-0006/0010 |
| 提权常驻 + 计划任务静默通道 + 登录触发器开机启动 | ADR-0001/0002，F35–F36 |
| 三态主题偏好经 ResourceDictionary 落地；托盘只看任务栏主题 | ADR-0012，F26–F28 |
| vital 无条件读数 + verbose 门禁 + 512KB 轮转 | ADR-0013，F39–F40 |
| 内存预算是初始目标，先测旧版进程树基线再冻结 | 02-spec/03 |

## F01–F48 → 模块/ticket 覆盖表

| ticket | 覆盖功能 | 内容 |
|---|---|---|
| T01 | F15、F16（部分）、F41–F44（骨架）、F29/F30（临时托盘） | 空 WPF 发布构建、可复用 HUD、临时托盘/呼出、性能基线采样设施 |
| T02 | F01、F02（文字侧）、F09–F13、F14 | 文字通知→存档→卡片→安全粘贴→停靠全链路 + 失败路径 |
| T03 | F03–F08、F37 | 旧档加载/设置往返、内容身份、两块落位、裁剪 |
| T04 | F18–F23 | 四态键位、搜索、导航重复、IME 让位、选中项完整可见 |
| T05 | F24、F25、F07（编辑态）、F45–F47 | 置顶、内联备注、延迟删除与跨停靠撤销、页脚/toast |
| T06 | F02（图片侧）、F42–F44（图卡）、F05（图片裁剪） | DIBV5→PNG→有界缩略图→位图粘贴 + 虚拟化证据 |
| T07 | F26–F34 | 三态主题、托盘图标五档、换键捕获、单实例、清空历史 |
| T08 | F35–F38、F39–F40（收尾） | 提权清单、静默任务、登录触发器、NSIS 安装/升级/卸载 |
| T09 | F48 + 全量 | 无障碍、性能前后对比、验收矩阵逐项、切换回退演练 |

依赖：T01←SDK/窗口试验；T02←T01；T03←T02；T04←T03；T05←T04；T06←T03+DIB/虚拟化试验；T07←T04+IPC 决策；T08←T07；T09←T05/T06/T08。

## 拟采用模块边界

- **ClipboardTool.Domain**：History、PanelModes、PendingDeletion、Search、HotkeyPlan、PasteChain、Geometry、Settings 纯规则；POCO/不可变值；零 WPF/Win32 依赖，可脱离框架测试。
- **ClipboardTool.Application**：PanelCoordinator、HistoryService、PasteService、ThemeService、StartupService；串行命令、结果契约 `{ ok, message }`、事件；端口构造注入（时间/ID/文件/剪贴板/热键/焦点/窗口）。
- **ClipboardTool.Infrastructure.Windows**：ClipboardMessageSource、ClipboardReader/Writer、DibDecoder、FocusAdapter、HotkeyExecutor、MouseHook、SingleInstance、ScheduledTask、JsonStore、Diagnostics、ThumbnailCache。
- **ClipboardTool.Presentation.Wpf**：App、PanelWindow、PanelViewModel、SearchHeader、文字/图片 DataTemplate、InlineNote、Footer、Toast、CaptureOverlay、主题字典、AutomationPeer。
- **ClipboardTool.Tests**：领域协议、旧档往返、平台字节与接口集成测试；独立可选真机探针。

关键线程防线：剪贴板独占读取在专用 STA 出口串行；模式状态单一串行写入者；UI 不 `.Result/.Wait()`；钩子回调只记录不处理；跨线程 BitmapSource 先 Freeze。

## 仅用户能决定的问题（已全部拍板，2026-10-03）

1. **Windows 最低支持版本：Win10 22H2+ 与 Win11**。测试矩阵不含 1607–21H2 分版本降级分支。
2. **发布方式：自包含**。NSIS 包携带 .NET 运行时，用户机器无需预装 Desktop Runtime；安装包约 70–100MB。
3. **字体：不打包**，沿用回退链 Inter→Segoe UI、JetBrains Mono→Cascadia Code→Consolas，与旧版渲染一致。
4. **安装身份：新产品身份**（用户明确未取「保持原身份」建议）。新版 NSIS 有独立 productName/GUID：旧 Tauri 版不会被新版覆盖升级；切换路径=备份存档→退出并卸载旧版→安装新版→验证；两代不得同时运行（争抢呼出键/共写存档，见资料包总提示词）。新版自身后续升级走新身份的 NSIS 升级逻辑。
5. 正式内存门槛——**推迟**：先测旧版基线，再按实测让用户确认（资料包明确此顺序）。

决策 1/2/4 已落 [docs/adr/0001-发布形态与最低系统.md](../../docs/adr/0001-发布形态与最低系统.md)。

## 风险最高的可运行试验（每个试验单独立项，隔离数据）

| # | 问题 | 成功判据 |
|---|---|---|
| P1 | WPF 浏览态不抢焦点，搜索/备注可聚焦，退出后能恢复 | 无边框窗口显示时前台不变；`ShowActivated=false`/`WS_EX_NOACTIVATE` + 输入态切换；键盘消息可达 |
| P2 | 提权进程向普通/管理员目标恢复焦点并注入 Ctrl+V | UIPI 下管理员→管理员注入成功；句柄失效零误注入 |
| P3 | DIBV5 透明截图→PNG（32bpp BI_RGB/BITFIELDS、头 40/52/56/108/124、上下行序）与旧 PNG 哈希往返 | alpha 保留、掩码校验、与旧实现产出同哈希或记 ADR 规范化 |
| P4 | 变高图片卡在 Recycling 虚拟化下滚动测量与选中项完整可见 | realized containers ≈ 可视数+有限缓存；边界滚动不贴圆角带 |
| P5 | 跨 DPI 圆角外透明区鼠标穿透且圆角内可交互（WM_NCHITTEST） | 角外点击落到下层窗口；圆角内按钮可点；125%/150% 下行为一致 |

HTML 原型不能证明 P1–P5；这些是 T01/T02/T06/T07 的阻塞或并行试验。

## 执行记录

- 2026-10-03：环境检查。git 基线 `c27e4e7`（133 文件）。`.NET SDK` 缺失 → winget 安装 `Microsoft.DotNet.SDK.10`（完成，10.0.401；新 shell 需手动 `export PATH="/c/Program Files/dotnet:$PATH"`）。winget v1.29.380 可用。
- 2026-10-03：工作流前置就绪（本地 tracker/分诊标签/领域文档布局）；四项发布决策已拍板并落 [ADR-0001](../../docs/adr/0001-发布形态与最低系统.md)。
- 2026-10-03：**P1 焦点试验 9/9 通过**（[prototype/p1-focus-noactivate/REPORT.md](../../prototype/p1-focus-noactivate/REPORT.md)）——浏览态不抢焦点、输入态 AttachThreadInput 级联可激活、真实键入到达、Esc 归还、停靠屏外不销毁同 HWND 复用。结论落 [ADR-0002](../../docs/adr/0002-面板焦点与停靠策略.md)，T01 阻塞解除。
- 2026-10-03：T01 工单已建：[issues/01-空WPF骨架与HUD.md](issues/01-空WPF骨架与HUD.md)（ready-for-agent）。
- 2026-10-03：**to-tickets 完成，13 张工单全部发布**（用户确认拆分）。编号对照：实现票
  [01](issues/01-空WPF骨架与HUD.md)=T01、[03](issues/03-T02文字复制粘贴闭环.md)=T02、[04](issues/04-T03旧档与身份落位.md)=T03、
  [05](issues/05-T04四态键位与搜索.md)=T04、[06](issues/06-T05置顶备注延迟删除.md)=T05、[09](issues/09-T06图片链路.md)=T06、
  [11](issues/11-T07系统集成.md)=T07、[12](issues/12-T08提权与安装.md)=T08、[13](issues/13-T09收尾验收与切换.md)=T09；
  试验票 [02](issues/02-P2提权焦点恢复试验.md)=P2、[07](issues/07-P3DIBV5透明截图试验.md)=P3、
  [08](issues/08-P4变高卡片虚拟化试验.md)=P4、[10](issues/10-P5跨DPI圆角穿透试验.md)=P5。
  P1 不占编号（已完成，REPORT 在 prototype/p1-focus-noactivate/）。
  当前沿（阻塞全解可立即开工）：01 骨架、02 P2、07 P3、08 P4、10 P5。
- 2026-10-03：**T01 完成（提交 91aef9f）**：五工程骨架 + `ClipboardTool.sln`/`global.json`/`.gitignore`；
  Domain PanelGeometry/HotkeyPlan 18 例测试全绿（尺寸三档、落位/停靠公式、差量、DisplayName）；Presentation/Infrastructure 按 ADR-0002 落地。
  本机 2560×1600@175% 回读验证：呼出 929,128/700×1400 物理px=400×800 DIP、停靠 2595,56、全程前台不变、同 HWND、样式 0x8080008（NOACTIVATE|TOOLWINDOW|TOPMOST）；
  键位按状态差量（停靠 {呼出键}/浏览 {呼出键,Esc 停靠}，F18 让位模型）；呼出落地三条件回读（legacy landing_verdict）；明暗 token 字典逐值对齐 theme.css；
  tools/BaselineSampler 四指标 CSV。评审发现并修复 bug：DockStateChanged 先于 Attach 触发致热键注册到空 HWND（执行者现暴露 Attached 并忽略未挂接 ApplyPlan）。
  待人工：真实键盘 WM_HOTKEY 送达、IME、多屏 DPI 交叉、托盘点击（见[工单证据记录](issues/01-空WPF骨架与HUD.md)）。01 票 Execution 已置 resolved。
- 2026-10-03：**P2 提权焦点恢复试验通过（连续两轮 6/6 门禁 + 负控）**（[prototype/p2-elevated-focus/REPORT.md](../../prototype/p2-elevated-focus/REPORT.md)）——
  high 进程向 medium/high 目标恢复+注入全链路成功（探针靶窗与真实记事本双证据，恢复后前台/焦点/内容三重回读）；
  失效快照与 HWND 复用（PID/TID 校验）在 restore 阶段拒绝且零注入；paste 阶段失败分开报可演示；
  负控实测 SendInput 返回 4/4 但 low→high 内容被 UIPI 静默过滤（「成功必须回读」钉住）。
  legacy focus_paste.rs 的 C# 移植（FocusRestore.cs）为 03 票 FocusAdapter 蓝本，结论落 [ADR-0003](../../docs/adr/0003-提权焦点恢复与注入策略.md)。
  探针侧观察：runner 拉起子进程后立即激活会冻结靶窗 UI 线程；改为普通侧预启动+充分初始化后稳定。
  02 票 Execution 已置 resolved；03（T02）阻塞解除。
- 2026-10-03：**T02 完成（提交 0919efd）**：文字复制粘贴闭环端到端可用（E2E 8 判据连续 4 轮全过）。
  Domain PasteChain/PollBaseline/HistoryStore（legacy 23 例移植）、Application 四端口+监听编排+ModeExecutor 专用执行线程（ADR-0004）、
  Infrastructure message-only 事件源（异步就绪+看门狗）/独占读写（进程内 ClipboardOps lease）/FocusPasteRestore 正式化。
  评审两轴发现并修复**卡片双击真 bug**（MouseDoubleClick 为 Direct 路由事件绑 ListBox 收不到，改 PreviewMouseDoubleClick 隧道——此前链路从未被双击触发过）、
  UI 线程 Wait、自写基线欠账与写+同步原子化；测试 41/41 全绿，构建零警告。
  待人工：真实键盘 Enter、目标窗口关闭失败路径、光标原位、浏览器输入框（见[工单证据记录](issues/03-T02文字复制粘贴闭环.md)）。
  03 票 Execution 已置 resolved。
- 待人工：P1 `--manual` 复核（点击卡片不激活、真实打字/IME、多屏）；T02 真机人工项见 03 票；正式内存门槛推迟到旧版基线实测后。
- 2026-10-03：**T03 完成（提交 f5a4bbf）**：旧档加载与身份落位端到端落地（122/122 测试全绿，零警告）。
  Domain.History 完整规则（身份/两块落位/裁剪/备注 Rune 归一化/Load 过滤重建）、HistoryArchive 旧档往返
  （serde 语义对齐：缺省归一化 vs 类型坏整条作废）、Domain.Settings 三键容错（auto_start 兼容）；
  Infrastructure JsonStore（.tmp→替换原子写、坏档 backup/ 备份、读失败 Unreadable）+ ImageFileStore；
  HistoryService 持久化收敛（变更+落盘同临界区、四态读档分派、读失败本会话禁写防覆盖原件）。
  两轴评审修复 P0：读失败按缺档会以空表覆盖从未读出的原件（新增 Unreadable 禁写态）。
  真机人工项（存档副本演练、只读恢复）见 [04 票证据记录](issues/04-T03旧档与身份落位.md)。
  待办移交：备注编辑器 UTF-16 上限与存储 Rune 语义对齐归 T05；落盘失败提示归 T05 toast；诊断日志归 T08。
  04 票 Execution 已置 resolved；05（T04）/07（P3）/08（P4）/09（T06）阻塞解除。
- 2026-10-03：**T04 完成（提交 4e583bb）**：四态键位与搜索落地（191/191 测试全绿，零警告，新增 69 例）。
  Domain.PanelModes 四态全矩阵（NAV 8 键、搜索态四键让位、IME 组合全停、捕获连呼出键让位）+
  Domain.Search（多词 AND/五字段/保序/高亮拼回/夹紧边界停住，口径=Invariant 小写后 Ordinal，与 legacy 一致）；
  Application.PanelCoordinator 状态机移植（五步 enter_input、互斥退出、快照生命周期收敛、
  try_set 捕获换键）+ NavRepeater（300ms/50ms、GetAsyncKeyState 轮询松键）+ SearchDebouncer（120ms+代次）；
  HotkeyExecutor 抽 IHotkeyNative（注册失败不上账可测）；Presentation 全接线（呼出重置/搜索井/
  清除保焦点/空态/正文高亮/占位备注编辑器）。
  两轴评审修复：焦点快照双源收敛、IME 组合取消兜底 + 备注 Enter ImeProcessed 守卫、
  NoteSpans 数据侧就绪（卡片渲染随 T05）、ResetQuery 收口、死事件移除。
  真机人工项（IME 候选、长按手感、搜索实操、切主题联验）见 [05 票证据记录](issues/05-T04四态键位与搜索.md)。
  05 票 Execution 已置 resolved；06（T05）阻塞解除；09（T06）仍阻塞于 07/08。
- 2026-10-03：**T05 完成（提交 687ae60）**：置顶备注延迟删除落地（215/215 测试全绿，零警告，新增 24 例）。
  Domain.PendingDeletion 状态机（pendingDeletion.ts 逐语义移植）+ Application.PendingDeletionService
  （摘除遮罩/6 秒撤销窗口/到期真删/失败恢复/强退保留）+ Domain.PanelFooterRegistry（页脚键位注册表，
  组件零字面键名）；Z 置顶/Del 延迟删除/B 内联备注四路径（保存/取消/失焦/强退差异）全接线；
  toast 栈（入 240/出 160/普通 2600/撤销 6000）+ 页脚三分支（六组/备注含字数/焦点错误覆盖）+
  ≤340 窄档收紧；meta 行两层等高 16 几何零位移 + 图钉 + 「·」分隔 + 备注高亮。
  两轴评审修复：页脚备注态刷新 bug、Esc 取消被静默改成保存、保存路径重入、幽灵 toast、
  dim 前导空白、hide 清编辑态、复制成功绿 toast 等（详见 [06 票证据记录](issues/06-T05置顶备注延迟删除.md)）。
  遗留记录：PanelFooterRegistry 落点补记；E2eT02 诊断失真归 T08；启动诊断日志（26a0022）真机验证后移除。
  真机人工项（Del 撤销/多条互不干扰/跨停靠计时/强退保留/备注几何/emoji/窄窗页脚）见 06 票。
  06 票 Execution 已置 resolved；07（P3）/08（P4）/10（P5）可开工；09（T06）仍阻塞于 07/08。
- 2026-10-03：**P3 DIBV5 解码试验通过（合并 7dab3eb）**：12 例字节矩阵（头 40/52/56/108/124 × 格式 × 行序 × 对齐）逐例与 legacy 一致 + 跨实现强验证 4/4；V5 alphaMask/四掩码/缺省 255 全验；31 例畸形数据拒绝不崩。
  **PNG 哈希不兼容（4/4）→ 身份判定改规范化 RGBA SHA-1**（PNG 解码 → Bgra32 非预乘 → SHA-1，新旧 PNG 通吃、id/文件名不迁移），落 [ADR-0005](../../docs/adr/0005-DIB解码与PNG编码参数.md)；WIC 同进程编码确定性已钉。
  56/56 原型测试两轮全绿，主方案 215/215 不回退。T06 踩坑移交：WPF 取像素必须 Bgra32 非预乘（Pbgra32 预乘有不可逆精度损失）。
  真机待人工（旧版实件逐字节比对、截图工具实拍 alpha、WIC 跨进程确定性）见 [07 票证据记录](issues/07-P3DIBV5透明截图试验.md)。07 票 Execution 已置 resolved。
- 2026-10-03：**P4 变高卡片虚拟化试验通过（合并 aa09ac4）**：200 混合卡分页真实滚动——解码请求 65≤67（按 Id 记忆化，往返增量 0）、高频导航 200 步均 8.8–21.8ms/步（阈值 40）选中项全程完整可见、变高漂移 |Δ|=0.00 DIP、Home 回初始 12 DIP 留白偏移 0；**Recycling 可靠：全程仅 6 容器实例承载 200 条，换绑快照 5 实例换绑不同 index**。
  09 票配置结论：沿用 VSP+Recycling+ScrollUnit=Pixel；缩略图解码按 Id 记忆化；顶端/底距放 ListBox Margin（滚动区外恒定）；**Recycling 是行为级属性：换绑后焦点容器不对应原条目，焦点/导航锚定选中项而非焦点元素**（实测踩坑：聚焦错容器把视口拉到列表尾）；容器计数法（distinct InstanceId+换绑快照）保留为回归证据。
  10/10 自检两轮全绿（合并后主线复跑 10/10），主方案 215/215 不回退。未覆盖项（滚轮/触摸惯性、插入置顶、过滤骤变、内存曲线、异步慢解码源）见 [08 票证据](issues/08-P4变高卡片虚拟化试验.md)，Execution 已置 resolved。
- 2026-10-03：**P5 跨 DPI 圆角穿透试验通过（合并 22b282f）**：穿透机制=分层窗口逐像素 alpha（AllowsTransparency 0 alpha 区）+ WM_NCHITTEST 钩子（弧内 HTCLIENT/弧外 HTTRANSPARENT；纯函数 CornerHitGeometry，radius 36 DIP × GetDpi 实时值，lParam 必须有符号解析）；175% 真机弧扫 64 点全一致 + 真实点击把边界夹紧到 ±4%R；浏览态/输入态全过；负坐标经移窗 (-300,-200)+有符号 lParam 消息级验证（模拟）。
  **11 票接入红线（ADR-0006）：禁止整窗 WS_EX_TRANSPARENT；HTTRANSPARENT 只能当 fringe 防误触的丢弃语义——真实点击跨窗口转发不可靠（实测）；WindowFromPoint 尊重 HTTRANSPARENT，不能用它推断真实点击路由**。
  几何单测 19/19（主线复验）、自检连续两轮 17/17（含跨带归因控制）、主方案 215/215。真机待人工（100/125/150% 运行时、真实多屏负坐标、物理鼠标/触屏、IME 组合点角外）见 [10 票证据](issues/10-P5跨DPI圆角穿透试验.md)，Execution 已置 resolved。
- 2026-10-03：**T06 图片链路完成（合并 35e5e3e）**：DibDecoder/PngEncoder 正式化（P3 蓝本 56 例迁入主测试）+ 身份判定改**规范化 RGBA SHA-1**（ADR-0005，旧 image crate 与新 WIC 编码同像素同身份，Bgra32 非预乘）；IImageFileStore.HashPng / IClipboardWriter.WriteImage（DIBV5 主格式保透明 + 40 头 DIB 兼容，绝不写文件路径）；ThumbnailCache 双上限 LRU（≤32 张/≤24 MiB 解码像素估算）按 Id 记忆化、失效含在途、代次防错图、停靠清缓存/呼出预热；图卡 150 DIP contain + 棋盘格底 + 文件名行；ContainerDiagnostics 环境变量门控（正常运行零开销）。
  **E2E 43 项断言全过**（tools/E2eT09，真实存档备份恢复）：系统截图+合成透明截图（CF_DIB/DIBV5）入库像素零差异、同图重复制身份命中不重写文件、双击复制 CF_DIBV5 回读 153600 像素零差异；4K 混合 206 条滚动 139.9ms/批、realized 峰值 13=distinct 实例（Recycling 真机证据）；200 裁剪删 PNG 联动、清空后 images/ 零残留。
  测试 302/302（215+87），构建零警告。待人工：画图/Office 目测观感（位图层已像素级验证）、Office 多格式复制；来源应用捕获 F06 未接线（meta 恒显未知来源，不在本票范围）。
  **新观察点入档（交 T09/终评跟进）**：约 20 条撤销窗口并存且容器持续重建时个别 Del 无效（停靠排空后恢复，疑似 Items 重建竞态中 SelectedItemId 短暂失效）。09 票 Execution 已置 resolved。
- 2026-10-04：**T07 系统集成完成（合并 01fd8a3 + 修复提交）**：合入 T06 E2E 成果（8fa0547）后真机 E2E **40/40 断言全过**（tools/E2eT07，注册表备份/恢复 + 数据目录备份/恢复 + 退出零残留），全量测试 397/397 绿、零警告。四判据全过：系统亮暗广播端到端（面板跟 AppsUseLightTheme、托盘跟 SystemUsesLightTheme，自定义模式两轴独立）+ 托盘像素证据；175% 选 28px 档 + 搜索中切主题保文本焦点；换键占用恢复/持久化/重启保留/捕获不让位；双实例呼出 + 普通→提权 ACL 投递（本机静默提权实测）。托盘「清空历史」真实右键路径全链路（3 条→0 条、images/ 空）。
  **E2E 揪出三枚真缺陷并修复（各带回归测试）**：① 二实例呼出 UI 线程同步等待死锁（Task.Run + ConfigureAwait(false)）；② 单实例 Gate 被 GC 后 Mutex 终结器释放锁（RootedGates 钉住）；③ RegGetValueW 本机恒败 1630（换 RegQueryValueExW + REG_DWORD 校验）。次修正：IsPanelOnScreen 动态屏宽；E2E 托盘菜单走真实右键路径、flyout 用鼠标收起（Esc 会停靠已显面板）。
  待人工：175% 托盘图标目测锐度、托盘变色像素复核（截图存 evidence-t07/）、他机提权 UAC。见 [11 票证据记录](issues/11-T07系统集成.md)。11 票 Execution 已置 resolved；12（T08）阻塞解除。
- 2026-10-04：T07 分支合并入集成主线（合并 4d35502），主线独立复验 397/397 绿、构建零错误；evidence-t07 截图与 tools/E2eT07 随分支入库。
- 当前前沿：12（T08）开工；13（T09）仅余 12 阻塞。
