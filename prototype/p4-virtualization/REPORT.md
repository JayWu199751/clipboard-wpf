# P4 变高卡片虚拟化试验报告

问题：VirtualizingStackPanel + Recycling 在**变高**图片卡（150 DIP 缩略图 + 三行文字 + meta）下是否可靠。
日期：2026-10-03。系统：Windows 11 x64，2560×1600 @175%。SDK：.NET 10.0.401，net10.0-windows。构建：Release，零警告。

## 复现

```bash
cd prototype/p4-virtualization
dotnet build -c Release
./bin/Release/net10.0-windows/P4VirtualizationProbe.exe   # 自动序列，约 40 s 后自行退出
```

产物（exe 同目录）：`p4-report.md`（判据表 + 运行日志）、`p4-trace.log`（逐条落盘 trace）、
`p4-shot-top.png` / `p4-shot-bottom.png`（顶部/底部截图）。
自检退出码即结果（全过 0，有失败 1，异常 2）。重复运行数字可复现（见判据表）。

## 原型构成

- 200 条混合卡：67 张图片卡（150 DIP 缩略图 + 三行文字 + meta）+ 133 张文字卡（1–3 行，变高）。
- 缩略图源为程序生成的 4K BMP（3840×2160 24bpp ≈ 24.9 MB，无压缩、无内嵌缩略图，WIC 需全量解码），
  按 `DecodePixelWidth=320` 解码；解码工厂层**按 Id 记忆化**并计数——解码风暴会直接反映在计数与耗时上。
- 列表配置与主工程 `PanelWindow.xaml`（T01–T05 现状）一致：
  `VirtualizingStackPanel` + `VirtualizationMode=Recycling` + `ScrollUnit=Pixel`、滚动条 Collapsed、
  列表区 Margin 12/12/12/16（顶端保护留白与页脚分隔线上方间距在滚动区外恒定存在）。
- 输入注入：KeyDown/KeyUp 直接 raise 到 ListBox 元素（触发与真实按键一致的 Selector 导航 + BringIntoView 路径）。
  真实输入管线的按键到达性已由 P1 试验（SendInput）覆盖，本票不重复验证。
  注：不用 `Keyboard.FocusedElement` 路线——Recycling 换绑后焦点容器可能已承载其他条目，
  聚焦逻辑会触发错误条目的 BringIntoView（试验中实际踩坑：视口被拉到列表尾）。

## 容器计数方法

1. **realized 数**：视觉树遍历数 `ListBoxItem`（微软文档口径：不能只看 XAML 虚拟化属性，要数容器）。
2. **可视数**：realized 容器中矩形与视口垂直相交（含部分可见）的数量。
3. **创建实例数**：容器首次 realize 时经 `Loaded` EventSetter 分配自增 InstanceId
   （ConditionalWeakTable 弱引用持有）；distinct InstanceId 总数 = 真正创建的实例数。
4. **换绑证据**：滚动前后各拍「InstanceId → 承载条目 index」快照，同 id 不同 index 即复用。

## 判据表（10/10 通过，两轮运行数字一致）

| # | 判据 | 结论 | 证据 |
|---|------|------|------|
| ①a | 200 条混合卡真实滚动，无重复解码风暴 | **PASS** | 分页滚动覆盖全列表，解码请求 65 次 ≤ 图片卡 67（每卡至多 1 次），累计 697–716 ms；往返滚动后增量解码 **0 次**（记忆化缓存生效） |
| ①b | 选中项全程完整可见 | **PASS** | 高频导航 200 步逐帧判定不完整可见 0 步 |
| ②a | realized ≈ 可视 + 有限缓存 | **PASS** | 初始 realized=6 / 可视=5；底部 realized=6；全程创建实例 **6 个**承载 200 条 |
| ②b | Recycling 生效（复用证据） | **PASS** | 整页滚动前后快照对比，5 个实例换绑到不同 index；6→9 个容器 |
| ③a | 高频导航（长按重复）滚动即时跟随 | **PASS** | 200 步 Down 均步 8.8–21.8 ms（含一帧布局渲染等待，阈值 40 ms），每步选中项均完整可见 |
| ③b | 顶端保护留白、底部到页脚分隔线 | **PASS** | 顶端：偏移 0、首卡 top=0（上方 12 DIP 恒定留白）；底部：最后卡 top=505.1 + 高 268.6 = 773.7 恰贴视口底（773.7），之下即页脚分隔线 |
| ④a | 变高测量无滚动位置漂移 | **PASS** | index 100（变高区间中部）视口内顶部 40.00 → 精确回归 40.00，\|Δ\|=0.00 DIP |
| ④b | 返回顶部回到初始留白 | **PASS** | Home 后偏移 0.0 = 初始 0.0 |

截图：`p4-shot-top.png`（选中首卡完整、顶端留白）、`p4-shot-bottom.png`（最后一张图片卡完整可见、页脚分隔线在其下）。

## 结论与给 09 票的配置建议

**变高图片卡下 VirtualizingStackPanel + Recycling + Pixel 滚动单位可靠**，四条判据全过。

1. 沿用现配置：`IsVirtualizing=True` + `VirtualizationMode=Recycling` + `ScrollUnit=Pixel`，无需改动。
2. 缩略图解码必须**按条目 Id 记忆化**（容器复用换绑后绑定重取走缓存，增量 0）；09 票图片链路照此实现。
3. 顶端保护留白 / 底部页脚间距放 ListBox Margin（滚动区外恒定存在），不要用会随滚动的 Padding。
4. Recycling 是**行为级**属性：换绑后焦点容器不再是原条目，键盘导航注入/焦点管理须锚定选中项而非焦点元素。
5. 09 票保留本容器计数方法（distinct InstanceId + 换绑快照）为回归证据。

## 未覆盖项

- 鼠标滚轮/触摸惯性滚动（自检走键盘导航；滚轮走 ScrollViewer 同一偏移管线，风险低）。
- 插入置顶/新条目不丢行为（工单提及，本票未构造；归 09 票实现时验证）。
- 搜索过滤后条目数骤变时的选中保持（SearchRules 夹紧逻辑已在 T03 覆盖，虚拟化叠加未重复验证）。
- 释放模式的内存回收曲线（本票只验证解码计数，不做内存剖析）。
- 远端/慢速解码源的异步占位与取消（本票解码为同步内存流）。
