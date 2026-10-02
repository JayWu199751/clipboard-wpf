Status: ready-for-agent
Type: prototype

# 07: P3 DIBV5 透明截图解码试验

## 阻塞

None（可立即开始；结论阻塞 09 图片链路票）。

## 问题

WIC/WPF 能力能否复刻 legacy `dib.rs` 解码器并保持**内容身份**兼容？
范围：CF_DIBV5→CF_DIB、32bpp BI_RGB/BI_BITFIELDS（含 alpha）、24bpp BI_RGB、BI_PNG；
头大小 40/52/56/108/124；上行序/下行序；行对齐与跨度校验；V4/V5 掩码不得在头内重复加 12 字节；
调色板/16bpp/JPEG/RLE/CMYK 明确不支持。风险点：PNG 编码器不同可能导致旧 PNG 内容哈希不同，
影响「图片按 PNG 内容 SHA-1 判身份」的去重（资料包 02-spec/02 §4）。

## 成功/失败判据

1. 构造样例 DIB 字节矩阵（头×格式×行序×对齐）逐例解码，像素与 legacy 实现输出一致（用 legacy dib.rs 的测试向量/逻辑对照）。
2. 透明截图 alpha 通道保留（与截图工具实拍比对）。
3. 相同像素经本实现 PNG 编码后 SHA-1 与旧版产出的现存 PNG 哈希一致；**若不一致**，给出向后兼容的规范化像素比较方案（仍维持身份只看内容），供 ADR 决策。
4. 掩码/长度/溢出校验拒绝畸形数据不崩溃。

## 交付

`prototype/p3-dib-decode/`（含样例字节构造器与对照测试）+ `REPORT.md`（复现命令、判据表、哈希对照结论、未覆盖项）。

## 结论去向

- Infrastructure.Windows.DibDecoder 实现策略与 PNG 编码参数 ADR；09 票直接采用。
- 若哈希不兼容：规范化比较 ADR（必要时）。

## 证据记录

（完成后填：提交、判据表、哈希对照输出、日期）
