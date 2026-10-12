Status: ready-for-agent
Type: task
Execution: resolved

# 重启后托盘正常但面板呼出被遮挡

## 事实

- 用户报告重启后开机启动成功，快捷键与托盘显示窗口均无可见效果。
- 当前实例 PID 8852，日志确认快捷键注册成功，hotkey/tray-menu/instance 均执行呼出。
- 呼出矩形 964,114,1594,1374，IsWindowVisible=True，DWM cloaked=0。
- EnumWindows 的实际 Z 序中，面板在普通应用窗口后方；面板仍带 WS_EX_TOPMOST。
- 现有 PlaceSummonDip 复用带 SWP_NOZORDER 的 PlacePhysical，呼出不重新落实置顶。
- 排障期间用户确认曾恢复显示，之后探针再次检出遮挡；不能把暂时恢复当修复。

## 完成判据

- [x] 每次呼出重新落实 HWND_TOPMOST，不激活浏览面板。
- [x] 停靠保持既有落位行为。
- [x] 原生窗口回归测试覆盖置顶标记丢失、置顶标记仍在但被较新窗口遮挡。
- [x] 构建、全量测试通过，生成可手动安装的修复包。
- [x] 不将自动验证等同于重启登录验收。

## Comments

2026-10-12：使用 ask-matt 路由到 diagnosing-bugs；探针通过真实单实例管道呼出并读取 OS 窗口事实，不读取剪贴板正文。

2026-10-12：已完成，详见 [验证记录](../evidence.md)。呼出先 NOTOPMOST 再 TOPMOST，托盘/第二实例对已呼出面板也刷新落位与层级，不重复重置状态机。安装包已生成，未安装；重启登录仍待用户验收。
