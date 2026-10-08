Status: ready-for-agent
Type: task
Execution: resolved
Blocked by: 无

# 切换微软雅黑并重新打开应用

按 [规格](../spec.md) 实施。

## 验收

- 普通界面文字实际渲染使用 Microsoft YaHei；等宽资源不变。
- 不再打包思源黑体或复制其许可；微软雅黑依赖系统已安装字体。
- 构建和字体验证通过，发布资源检查与约定/规格审查通过。
- 打开新构建供用户测试，桌面视觉效果由用户验收。

## Comments

- 2026-10-08：用户明确指定替换。当前系统注册表确认安装 Microsoft YaHei 系列。

## Answer

- 普通文字使用 Microsoft YaHei → Segoe UI，Font.Mono 原值保持；思源黑体资源与分发配置已移除，ADR 同步。
- 实际 GlyphRun 确认使用系统微软雅黑，470 项测试通过，Release 发布检查通过。
- 新测试构建已打开，面板窗口可见；真实视觉效果留待用户验收。
- 详见 [验证记录](../evidence.md)。
