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
- 待人工：P1 `--manual` 复核（点击卡片不激活、真实打字/IME、多屏）；正式内存门槛推迟到旧版基线实测后。
