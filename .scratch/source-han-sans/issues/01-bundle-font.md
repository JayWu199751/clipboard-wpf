Status: ready-for-agent
Type: task
Execution: resolved
Blocked by: 无

# 内置思源黑体并替换普通界面文字

按 [规格](../spec.md) 实施。用户已确认字体内置与保留等宽区域。

## 验收

- 普通文字引用内置的 Source Han Sans CN，Regular 与 Medium 能加载。
- Font.Mono 不变，面板与托盘菜单继续共用 Font.Sans。
- 官方字体许可随发布分发，ADR 同步。
- 通过构建、实际字体资源验证和全量测试。

## Comments

- 2026-10-08：开始实施。工作区已有托盘菜单主题相关修改；本任务提交仅包含字体相关改动。

## Answer

- 已内置 Regular 与 Medium，两份字体原文件合计 16,835,780 字节；普通界面字体引用 Source Han Sans CN，等宽资源保持原值。
- 验证程序从真实 App.xaml 载入资源，并核验实际 GlyphRun 使用内置字体与对应字重，无缺字；[渲染样例](../font-preview.png)。
- 全量测试 470 项通过；Release win-x64 自包含发布成功，程序集包含两份 OTF，输出许可 SHA-256 与原文件一致。
- 首轮隔离构建时，8 项图片测试因固定位于输出目录上方三层的样例路径失败；复制原样例到隔离目录的预期位置后全量通过。
- 真实桌面换行、截断和视觉密度未验收；当前运行的应用与已安装副本未更新。
- 复现命令和发布路径见 [验证记录](../evidence.md)。
