# WPF 目标架构与性能计划

本页是实现建议及初始预算，区别于 [当前功能](01-功能清单.md) 与 [硬契约](02-存档与平台契约.md)。尚未生成 WPF 工程，所有预算均未实测。官方依据见 [参考索引](../04-reference/README.md)。

## 建议技术基线

2026-10-03 核验微软支持政策，.NET 10 为 LTS，建议 C# + `net10.0-windows` WPF、x64。开始实现时检查 SDK/Windows 支持范围并锁 `global.json` 与依赖；当前资料制作环境未发现 dotnet 命令，不据此安装 SDK。MVVM 可以简洁手写或使用少量 CommunityToolkit.Mvvm，是否引入由实际复杂度决定。

生产 UI 仅 XAML；托盘可用 WinForms NotifyIcon + 精确 HICON 或直接 Shell_NotifyIcon，小接口隔离；不要仅为菜单引一整套主题控件库。P/Invoke 用 LibraryImport/明确结构布局与所有权，UI 不直接散落 Win32。JSON 用 System.Text.Json 配字段显式契约；PNG 编解码先以 WIC/WPF 能力做 DIB/透明与哈希兼容试验，必要依赖有明确原因。

## 建议模块

| 项目/模块 | 对外职责与边界 |
|---|---|
| ClipboardTool.Domain | History、PanelModes、PendingDeletion、Search、HotkeyPlan、PasteChain、Geometry、Settings 纯规则；普通 POCO/不可变值；不依赖 WPF/WinForms/PInvoke |
| ClipboardTool.Application | PanelCoordinator、HistoryService、PasteService、ThemeService、StartupService；串行命令、结果、事务与事件；必需端口构造注入 |
| ClipboardTool.Infrastructure.Windows | ClipboardMessageSource、ClipboardReader/Writer、DibDecoder、FocusAdapter、HotkeyExecutor、MouseHook、SingleInstance、ScheduledTask、JsonStore、Diagnostics、ThumbnailCache |
| ClipboardTool.Presentation.Wpf | App 启动与资源、PanelWindow、PanelViewModel、SearchHeader、文字/图片 DataTemplate、InlineNote、Footer、Toast、CaptureOverlay、主题字典与 AutomationPeer |
| ClipboardTool.Tests | 领域协议、旧档往返、平台字节与接口集成测试；独立可选的 Windows 真机探针与 UI 验收 |

深模块只给具名操作，内部状态/缓存/模式不外泄；避免 ViewModel 成为第二份 HistoryStore。UI 列表保留条目元数据投影，完整正文按需求引用或磁盘读，缩略图是资源句柄不是巨大的 base64。历史落位变化发布 id/差量或轻量快照，不反复复制全部图片数据。

ViewModel 临时态：selectedId、query、searchActive、noteDraft、captureStatus、focusError、toast。持久领域态：History/Settings。系统事实：已生效键集合、当前前台/焦点、窗口真矩形、任务存在/触发器、主题注册表。每类只有一处真源；输入与取消操作不提前覆盖已提交字段。

## 内存设计

1. ListBox/ItemsControl 使用 VirtualizingStackPanel 与 Recycling；变高图片卡的滚动测量、选中项可见、返回顶部、插入置顶时保留行为。不能外包无界 ScrollViewer、CanContentScroll=false 或直接添加容器令虚拟化失效。验证实际 realized containers 接近可视数+有限缓存，不能只看 XAML 属性。
2. 元数据与缩略图分离。按可视区及少量预取队列加载，解码到实际缩略图物理像素（约 150 DIP 高 × DPI），复制才打开全尺寸原图。BitmapImage 用 OnLoad 关闭流、Freeze 跨线程；取消旧请求、校验 id/代次，防回收容器显示错图。
3. 缩略图缓存初始建议「至多 32 张且估算解码像素不超过 24 MiB」双上限 LRU；按 stride×height 计解码占用，附加对象开销另外观测。移除/裁剪/清空立即失效；停靠后允许回收不可见缩略图，原始 PNG 不常驻。缓存超大单项不永久持有。
4. 来源图标与 hash 缓存有界，避免每个窗口标题生成新图标副本；源 iconDataUrl 是旧档可读字段，不强迫运行期同时持字符串、PNG bytes、Bitmap 三份。
5. 长正文不在列表元素里构建全部 Run，仅三行可视投影；搜索匹配完整正文，120ms 防抖取消旧查询，查询与 highlighted spans 有界生命周期。不要静默截断保存正文以省内存。
6. 监听消息阻塞等待，无正常态高频轮询；未占用无 timer；方向键重复仅按住时运行。后台线程数与队列容量可观察且有退出协议，不复制旧版每条一次性线程布局。
7. PInvoke handles 用 SafeHandle 或明确 Dispose；HICON、HBITMAP、HGLOBAL 所有权、hook 委托寿命、事件解绑、CancellationTokenSource/timer、文件流都列清。SendInput/GetGUIThreadInfo 在 x64 结构布局正确。
8. UI 不每次呼出销毁/重建；窗口透明层/AllowsTransparency、阴影与大面积 Effect 的开销要测；先保持现有视觉与命中，后按证据选实现。不要用常驻 GC.Collect、EmptyWorkingSet 或最小化技巧美化数字。

微软区分 UI 虚拟化与数据虚拟化：前者不会自动丢掉全部绑定数据；图片缓存与原图按需加载是本项目另外承担的资源策略（[WPF 控件性能](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls)）。

## 基线与初始预算

**先测旧版再冻结预算**。以下是讨论用目标，不是用户已拍板的承诺；如达不到，报告构成与优化证据，让用户按实际结果确认门槛。预算按发布 x64、无调试器、同机三次独立进程取中位数与范围。

| 场景 | 初始目标 |
|---|---|
| 空历史启动稳定后停靠 60s | Private Bytes ≤ 60 MiB；空闲 CPU 平均 ≤ 0.5%（记录逻辑核数量与口径） |
| 200 条文字（每条约 1KiB）停靠 | Private Bytes ≤ 80 MiB |
| 160 条文字 + 40 张 4K PNG、完整滚动后停靠 30s | Private Bytes ≤ 120 MiB，持有全尺寸原图数为 0；与旧进程树同场景比较 |
| 100 次呼出/停靠、200 次滚动/编辑/撤销 | 最后稳定 Private Bytes 较首轮稳定值增量 ≤ 10 MiB，句柄/GDI 无持续上升趋势 |
| 热呼出到首个可用帧 | p95 ≤ 100ms，首次单独报告；不含人为等待热键按下 |
| 普通文字复制通知到列表可见 | p95 ≤ 100ms；4K 截图单独测，不能因错误抹基线而「很快」 |
| 恢复焦点并注入 | 成功路径 p95 ≤ 150ms；目标失效及时失败且零误注入 |

上述任一预算受控于数据与字体/主题/DPI；无限长文本、大图解码时峰值另记，不以稳态上限限制用户有效内容。旧版占用需要包含 exe 和全部 WebView2 子进程，不能用一个旧主进程对比整个 WPF 进程。

测量时标记：构建提交/Release、Windows/.NET 版本、DPI/屏幕/CPU/RAM、数据条数/PNG 字节/尺寸、主题、提权、采样间隔、运行 1/2/3、操作序列。Private Bytes 与 Working Set 分开；GC.GetTotalMemory 不代表原生/图像占用，系统压工作集后观察 Private Bytes/资源数是否依然高。

优先记录 Win32 Process PrivateMemorySize64、WorkingSet64、HandleCount、CPU 时间差与 GetGuiResources；托管堆/GC 可用运行时计数器或 profiler 补充。发布模式可用开发诊断探针采样，避免生产永久加载 profiler。表格模板见 `05-validation/02-性能记录模板.md`。

## 纵向 ticket 建议

| ticket | 可运行的结果 | 阻塞 |
|---|---|---|
| T01 | 空 WPF 发布构建 + 可复用 HUD + 临时托盘/呼出 + 基线采样 | SDK/窗口风险试验 |
| T02 | 一次文字通知→存档→卡片→安全粘贴→停靠，失败路径可测 | T01，粘贴/线程决策 |
| T03 | 旧档加载/设置往返 + 内容身份/两块落位/裁剪 | T02 |
| T04 | 搜索/导航重复/IME + 选中项完整可见 | T03 |
| T05 | 置顶/内联备注/延迟删除与跨停靠撤销 | T04 |
| T06 | DIBV5 图片→PNG→有界缩略图→位图粘贴 + 虚拟化证据 | T03，DIB/虚拟化试验 |
| T07 | 三态主题/托盘图标/换键/单实例完整链路 | T04，跨权限 IPC 决策 |
| T08 | 提权清单/静默任务/登录触发器 + 安装升级卸载 | T07 |
| T09 | 全功能等价审查、内存前后对比、真机清单与切换回退 | T05/T06/T08 |

这只是拆票起点，`to-tickets` 需要按实际风险重排并为每票给功能编号/红绿测试/手动步骤。不要未经 `to-spec` 合成就把决策图直接拿去实现。

待决项只问有实际影响的选择：Windows 最低支持版本；正式内存门槛；框架依赖或自包含发布的安装体积/维护取舍；字体是否随包分发及许可；是否要求升级保持原安装包产品身份。C# + WPF 与现有功能等价已是既定目标。
