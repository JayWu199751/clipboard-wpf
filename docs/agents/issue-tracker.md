# 任务追踪：本地 Markdown

本仓库的需求、规格和工单保存在 `.scratch/` 下。

## 文件约定

- 每个功能使用一个目录：`.scratch/<feature-slug>/`。
- 规格保存在 `.scratch/<feature-slug>/spec.md`。
- 实施工单每项一个文件：
  `.scratch/<feature-slug>/issues/<NN>-<slug>.md`。
- 工单编号从 `01` 开始。
- 工单顶部使用 `Status:` 记录分诊标签，
  名称见 `triage-labels.md`。
- 评论和讨论追加到文件底部的 `## Comments` 下。

## 技能操作

- “发布到任务追踪系统”：按文件约定创建对应文档。
- “获取相关工单”：读取用户提供的路径；
  仅提供编号时，在对应功能目录中查找。

## 探索任务

- 任务地图：`.scratch/<effort>/map.md`，
  记录笔记、已作决策和待解问题。
- 子任务：`.scratch/<effort>/issues/<NN>-<slug>.md`。
- `Type:` 记录 research、prototype、grilling 或 task。
- `Execution:` 记录 open、claimed 或 resolved，
  与 `Status:` 的分诊标签分开维护。
- `Blocked by:` 记录阻塞任务编号；
  所有阻塞任务均为 resolved 后才可执行。
- 按编号选择最早的、未认领且无阻塞的 open 任务。
- 开始工作前将 Execution 设为 claimed 并保存。
- 完成后在 `## Answer` 下追加结果，
  将 Execution 设为 resolved，
  并向 map.md 的决策部分追加结论和文件链接。
