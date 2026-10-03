# ADR-0005：DIB 解码与 PNG 编码参数

日期：2026-10-03。状态：已接受（依据 P3 试验证据）。

## 背景

图片条目（F02/F03）要求 CF_DIBV5/CF_DIB → PNG，条目身份按 PNG 内容 SHA-1 去重
（02-spec/02 §4）。legacy 自解 DIB 的原因已钉死：arboard 把 CF_DIBV5 直接喂 image 的 BMP
解码器，那个解码器在「BI_BITFIELDS + V4/V5 头」上把像素起点多算 12 字节（掩码本来就在
头里），整幅图错 12 字节 → UnexpectedEof → 截图一条进不了历史（PixPin、系统截图全中）。
WPF/WIC 能否等价复刻 legacy `dib.rs` 解码并保持内容身份兼容，是 T06 的阻塞试验 P3；
证据见 [prototype/p3-dib-decode/REPORT.md](../../prototype/p3-dib-decode/REPORT.md)
（2026-10-03，net10.0-windows x64，56/56 测试全绿，判据表 1–4 全过）。

## 决策

1. **解码自写、逐行移植 legacy `dib.rs`，不用 WIC 的 BMP 解码器**（`DibDecoder.cs` 即蓝本）：
   只认头大小 40/52/56/108/124；32bpp BI_RGB/BI_BITFIELDS（含 alpha 掩码）、24bpp BI_RGB、
   BI_PNG 验头透传。掩码位置规则：BI_BITFIELDS 且 40 头时 12 字节掩码接在头后；
   V2(52) 起掩码在头内，**像素起点 = 头大小，不得再加 12 字节**——这正是 image crate 的 bug 形状。
   调色板、16bpp、BI_JPEG、RLE、CMYK 一律拒绝，与 legacy 覆盖面一致，不做隐性承诺。
2. **alpha 语义与 legacy 完全一致**：三个颜色掩码全缺按 B,G,R 补齐；V2(52) 无 alpha 字段
   强制 A=255；V3(56) 起 alpha 掩码非零才认（PixPin 写法：compression=BI_RGB 但
   bV5AlphaMask=0xff000000，第四字节确实是 alpha）；无 alpha 掩码时 A 缺省 255。
   行序：默认自下而上，负高度自上而下；行按 4 字节对齐，最后一行允许缺填充；
   所有长度/掩码/溢出校验在分配像素缓冲之前完成，畸形数据返回失败不抛异常。
3. **PNG 编码用 WPF `PngBitmapEncoder`（WIC），像素源固定非预乘 Bgra32**：
   试验实测 WIC 与 legacy image crate 的 PNG 输出逐字节不同（filter/压缩参数不同，
   4/4 样例 SHA-1 全部不一致，且 WIC 输出更大）。**不追求字节级兼容编码器**，因为：
   a) 编码确定性成立（同像素同哈希，测试钉住）；b) 身份兼容由规范化像素比较维持（下条）。
4. **身份判定引入规范化像素比较，保持「身份只看内容」且向后兼容**：
   条目身份 = SHA-1(PNG 解码 → 规范化 RGBA 字节（Bgra32 非预乘、自上而下行序）)。
   旧版 PNG（image crate 编码）与新 PNG（WIC 编码）在同一像素下得到同一规范化哈希
   （4/4 样例证实）。存量条目的 id 与 PNG 文件名不变；去重命中时对旧 PNG 解码算
   规范化哈希即可，无需迁移键。实现注记：解码必须用 Bgra32（非预乘）取像素——
   WPF 默认 Pbgra32 会预乘 alpha，半透明像素 RGB 有不可逆精度损失（试验中实测踩坑）。

## 后果

- T06 的 `Infrastructure.Windows.DibDecoder` 按 `DibDecoder.cs` 移植；`HistoryService`
  的图片身份判定改用规范化像素 SHA-1（本 ADR 第 4 条），不再直接哈希 PNG 字节。
- 新图片落盘仍用 WIC PNG 编码（第 3 条），体积比 legacy 输出大（小样例约 +40 字节，
  大图相对差异收敛），可接受；若未来需要压缩比优化另行 ADR，不影响身份判定。
- 判据 2 的「与截图工具实拍比对」与跨进程编码持久确定性属真机人工项（见 REPORT）。
- 若未来引入 16bpp/调色板等格式支持，属扩展而非兼容性变更，需更新本 ADR 的拒绝清单。
