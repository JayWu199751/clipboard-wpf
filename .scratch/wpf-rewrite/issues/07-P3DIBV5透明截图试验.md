Status: ready-for-agent
Execution: resolved
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

日期：2026-10-03。分支 `ticket/07-p3-dib-decode`（基线 25f2327）。

**提交链**：30756a5（认领）→ 417a51e（解码器核心+判据 1/2 矩阵 12 例）→ b2c6897（判据 4+BI_PNG，43 例）→ aff5171（判据 3 参照资产与哈希对照，56 例）→ 5915506（ADR-0005+REPORT）。合并集成分支后最终哈希见 map.md 执行记录。

**判据表**：

| # | 判据 | 结论 |
|---|---|---|
| 1 | 样例矩阵逐例解码与 legacy 一致 | **通过**：矩阵 12 例（头 40/52/56/108/124 × 格式 × 行序 × 对齐）+ 跨实现 4/4 逐字节一致（C# 解码 == legacy 链路 PNG 解码像素） |
| 2 | alpha 保留 | **通过（自动侧）**：V5 alphaMask/V3–V5 四掩码/无掩码缺省 255/V2 强制 255 全验；实拍比对归真机 |
| 3 | PNG 哈希与旧版一致，否则规范化方案 | **哈希不一致（4/4 样例）→ 规范化像素比较成立（4/4）**：身份判定改 PNG 解码→规范化 RGBA→SHA-1，向后兼容，落 [ADR-0005](../../../docs/adr/0005-DIB解码与PNG编码参数.md) 第 4 条 |
| 4 | 畸形数据拒绝不崩溃 | **通过**：31 例拒绝用例全过（格式/位深/头/掩码/长度/溢出/BI_PNG 截断），不分配不崩 |

测试 56 例两轮全绿；报告见 [prototype/p3-dib-decode/REPORT.md](../../../prototype/p3-dib-decode/REPORT.md)。

**哈希对照输出摘要**（全对照见 REPORT）：

```
v5-rgb-alpha-bottomup（PixPin 回归形状）: legacy c8e7e11e… / WPF 6fdb6e65… 一致=False
v5-bitfields-alpha-topdown:              legacy 1480b476… / WPF fd365c32… 一致=False
v0-rgb-32bpp-bottomup:                   legacy 9b9c1881… / WPF 71bf58ab… 一致=False
v0-rgb-24bpp-bottomup:                   legacy ff8f6095… / WPF d929e536… 一致=False
规范化像素 SHA-1 等价 = True（4/4）；WIC 同进程编码确定性 = True
```

**参照 PNG 生成方式**（如实记录）：本机无旧版管线产出的现存 PNG 实件；参照由 legacy
算法直接运行生成——`rust-reference/` 原封复制 legacy `dib.rs`（sha256 1a5ad36d…）+
image 0.25.10（与 legacy 声明同语义版本），产物固化 `ReferencePng/` + manifest.json。

**未覆盖项/真机待人工**：① 旧版实际产出 PNG 实件逐字节比对（影响编码字节，不影响规范化结论）；② 截图工具实拍 alpha 比对（PixPin/系统截图真实 CF_DIBV5 流）；③ WIC 编码跨进程持久确定性（同进程已钉）；④ 大图耗时实测归 T06。**对 T06 关键踩坑：WPF 解码须用 Bgra32 非预乘取像素，Pbgra32 预乘会损失半透明 RGB 精度。**
