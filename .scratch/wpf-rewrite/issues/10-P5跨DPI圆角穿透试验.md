Status: in-progress
Execution: claimed
Type: prototype

# 10: P5 跨 DPI 圆角穿透试验

## 阻塞

None（可立即开始；结论阻塞 11 系统集成票）。

## 问题

36 DIP 圆角外壳在分数 DPI（125%/150%/175%）下：圆角外透明部分鼠标**穿透**到下层窗口、
圆角内正常交互；WM_NCHITTEST/命中策略在浏览态与输入态分别验证（不能只给窗口整体设 ClickThrough，
否则搜索不可点；也不能全窗永久穿透）。命中几何由当前 radius 决定。

## 成功/失败判据

1. 角外点击：下层窗口收到点击（前台/焦点变化可观测）。
2. 角内（含搜索井、卡片、页脚）：正常接收点击与键盘。
3. 四角 × 100/125/150/175% 下命中区域与视觉圆角一致（几何换算 DIP↔物理正确）。
4. 输入态（激活）与浏览态（NOACTIVATE）行为均正确；多显示器负坐标屏上成立。

## 交付

`prototype/p5-rounded-hit-test/` + `REPORT.md`（判据表、各 DPI 截图、WM_NCHITTEST 处理结论、未覆盖项）。

## 结论去向

- PanelWindow 命中策略（window_set_ignore_mouse 等价物）实现 ADR；11 票采用；
  F17 验收矩阵「角外点击下层」的预演证据。

## 证据记录

（完成后填：提交、判据表、截图路径、日期）
