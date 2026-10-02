# 事实源与官方资料

`legacy/` 是基准提交 `b05ba281438f713c676b94d3de89f679cfc383cd` 的只读参考快照。为便于整包带到新仓库，保留当前领域/平台源码、UI、测试、脚本、图标、配置、锁文件、CONTEXT、README、架构、设计和全部 ADR。不包含 node_modules、target、用户真实存档或诊断日志。本次根 README/changelog 的资料入口修改不包含在基准快照里。

`manifest.json` 记录基准、取材范围与各文件 SHA-256，可验证参考文件未被修改。旧源码用于读行为/迁移测试，禁止把这一份当作第二个日常开发工程。新规格在 `02-spec/` 维护，基准快照永远不回填。

本目录 `.gitattributes` 将只读 legacy 快照按原始字节存储，避开 autocrlf 的检出转换；源码仍是普通可读文件。资料包其他文本使用仓库 CRLF 约定。

## 按任务定位

| 要解决的事情 | 先读 |
|---|---|
| 条目身份/置顶块/普通块/备注/上限 | history.rs 与 ADR-0003/0004 |
| 粘贴顺序与错误 | paste_chain.rs 的9例、focus_paste.rs、ADR-0005 |
| 状态机/键位/注册差量/死锁 | panel_modes.rs、modes.rs、hotkeys.rs、architecture线程节、ADR-0006/0008/0010 |
| 系统通知/剪贴板占用/基线 | clipboard_events.rs、clipboard.rs、poll_baseline.rs、ADR-0011 |
| 截图透明/头/掩码 | dib.rs、ADR-0009、desktop-tool-pitfalls |
| 提权/静默通道/意图事实 | startup.rs、tasks.rs、build.rs、ADR-0001/0002 |
| JSON字段/旧档容错 | history.rs、settings.rs、ADR-0007 |
| 窗口落地/停靠/DPI | panel_window.rs、modes.rs、panel-state.ps1 |
| 主题与图标 | settings.rs、tray.rs、themeSync.ts、ADR-0012 |
| HUD组件/选中/滚动/撤销 | App.tsx、ClipCard.tsx、SearchHeader.tsx、panelView.ts、pendingDeletion.ts及浏览器测试 |
| 已知待真机问题 | legacy/README「待真机验证」；本包验收矩阵 |

## 官方资料（2026-10-03 核验）

- [.NET 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)：技术基线选受支持 LTS；当时 .NET 10 是 LTS。具体补丁号在实现时再次核验，不在本包硬写旧补丁。
- [WPF 控件性能](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls)：UI 虚拟化与数据虚拟化不同，Recycling 与可能禁用虚拟化的条件；支持本包缓存/容器计数方案。
- [WPF 线程模型](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model)：Dispatcher 归属与异步调度；支持 UI 线程边界设计。
- [AddClipboardFormatListener](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener)：变化向注册 HWND 投递 WM_CLIPBOARDUPDATE，返回值需检查；不能把它推导为已释放剪贴板。
- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)：HWND/线程归属、失败、MOD_NOREPEAT、旧注册显式解除；F12 和部分 Windows 组合有系统限制，所以捕获码表能表示不等于注册一定成功。
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)：注入返回数及 UIPI 限制；不能从注入调用成功推导已经在正确控件完成粘贴。

这些链接提供平台/API依据；具体产品契约来自基准源码与 ADR，性能预算是本包建议。没有用第三方博客或搜索摘要给未知平台行为下定论。
