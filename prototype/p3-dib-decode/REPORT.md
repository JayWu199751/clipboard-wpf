# P3 试验报告：DIBV5 透明截图解码（工单 07）

日期：2026-10-03。分支：`ticket/07-p3-dib-decode`（基线 25f2327 = integration/wpf-rewrite）。
环境：Windows 11 x64，.NET SDK 10.0.401，net10.0-windows；Rust 参照侧 cargo 1.98.1。

## 结论一句话

C# 逐行移植 legacy `dib.rs` 可逐字节复刻解码行为（判据 1/2/4 全过）；WPF WIC 编码与
legacy image crate 的 PNG **内容哈希不一致**（判据 3 主判据不成立），但规范化像素比较
成立且向后兼容——**结论落 ADR-0005 第 4 条：身份判定用 PNG 解码 → 规范化 RGBA → SHA-1**。

## 复现命令

```bash
# C# 侧（无头可跑；56 例约 2 分钟）
export PATH="/c/Program Files/dotnet:$PATH"
cd prototype/p3-dib-decode && dotnet test

# 重新生成参照 PNG（可选；参照资产已固化在 ReferencePng/，dotnet test 不依赖 Rust）
cd prototype/p3-dib-decode/rust-reference && cargo build --release && cd ..
python rust-reference/gen_samples.py
for f in ReferencePng/*.dib; do
  ./rust-reference/target/release/dib-reference.exe "$f" "${f%.dib}.png"
done
```

## 参照 PNG 的生成方式（如实记录）

本机**没有**旧版管线产出的现存 PNG 实件（kit 内与仓库 `artifacts/` 均无；T03 的
`TestAssets/images/sample.png` 是 1×1 手工 PNG，非管线产出）。故判据 3 的参照由
**legacy 算法直接运行生成**：`rust-reference/` 内将 legacy `dib.rs` 原封复制
（sha256 `1a5ad36d5403519915e23fcbda3bcbb8209a9bf456fd3b8c6bff18e6c2a01280`），
依赖 `image 0.25.10`（Cargo.toml 语义版本 `image = "0.25"` png feature，与 legacy 声明相同），
经其 `to_png` 输出 PNG 并计算 SHA-1，产物固化于 `ReferencePng/`（manifest.json 记录）。
这等价于「旧版产出的 PNG」，但与旧版真实安装包内的 image 小版本可能存在差异，列为未覆盖项。

## 判据表

| # | 判据 | 结论 | 证据 |
|---|---|---|---|
| 1 | 样例 DIB 字节矩阵逐例解码与 legacy 一致 | **通过** | `DibDecodeMatrixTests`（12 例：头 40/52/56/108/124 × 32bpp BI_RGB/BITFIELDS × 24bpp × 上下行序 × 行对齐）+ `CSharp解码器与legacy参照链路_像素逐字节一致`（4/4：C# 解码器输出 == legacy 链路 PNG 解码像素，逐字节） |
| 2 | 透明截图 alpha 保留 | **通过（自动侧）** | V5 BI_RGB alphaMask=0xff000000、V3/V4/V5 BITFIELDS 四掩码、V2 无 alpha 强制 255、无掩码缺省 255；参照 PNG 解码像素含 alpha 且逐字节一致。实拍比对列入真机人工项 |
| 3 | PNG 编码 SHA-1 与旧版一致；若不一致给规范化方案 | **哈希不一致 → 规范化方案成立** | 4/4 样例 WIC vs legacy SHA-1 全部不同（对照输出见下）；`规范化像素比较_新旧行PNG解码后身份等价` 4/4 通过；`WPF编码器自身确定性` 通过。方案与决策落 [ADR-0005](../../docs/adr/0005-DIB解码与PNG编码参数.md) |
| 4 | 畸形数据拒绝不崩溃 | **通过** | `DibDecodeRejectTests`（31 例：调色板/1/4/8/16bpp、RLE4/8、JPEG、CMYK、CORE 头、非白名单头、planes≠1、宽≤0、高=0、像素不足/行截断、掩码不连续/超 16 位/alpha 坏、BI_PNG 缺失/截断、空数组；`像素数据不足时不分配也不崩` 与 legacy 同名测试对齐） |

测试合计 56 例，两轮连续全绿。

## 哈希对照输出摘要

```
[v5-rgb-alpha-bottomup（PixPin 回归形状）]
  legacy PNG SHA-1 = c8e7e11ef6819e35f77efba18213be4b3c8dfdf4（86 字节）
  WPF    PNG SHA-1 = 6fdb6e65e1ab71464e10b037438bb23bf46ecdab（133 字节）
  逐字节一致 = False
[v5-bitfields-alpha-topdown]
  legacy = 1480b4766e18b4349bd26a8b38429aeb7bc5b2bd（94 字节）
  WPF    = fd365c321a4779b14a83f33baa9ef552f40fc659（142 字节）  一致 = False
[v0-rgb-32bpp-bottomup]
  legacy = 9b9c188109fc9b834e11a68a25e0b4edbbe94c2c（77 字节）
  WPF    = 71bf58abe64ec27abc121c57248ba7a37c33ec50（124 字节）  一致 = False
[v0-rgb-24bpp-bottomup]
  legacy = ff8f609551ec4de359b20a45cdbf215dcb21b753（94 字节）
  WPF    = d929e536519d705d12ae0eb448d79759959d9acb（134 字节）  一致 = False
[规范化像素比较] 新旧行 PNG 解码后像素 SHA-1 等价 = True（4/4）
```

不一致原因：WIC `PngBitmapEncoder` 与 image crate(png) 的 zlib 压缩与 filter 策略不同
（WIC 输出偏大）。两侧 PNG 均为合法非预乘 RGBA8，解码像素一致，故内容身份用规范化
像素哈希维持。WIC 同像素同进程编码确定性已钉住（测试）；跨进程持久确定性未验（见下）。

## 试验中踩坑（对 T06 有用）

- **Pbgra32 预乘陷阱**：WPF 解码 PNG 时 `FormatConvertedBitmap` 转默认的 Pbgra32 会预乘
  alpha，半透明像素 RGB 有不可逆精度损失（本试验判据 1 两例因此失败后修复）。T06 实现
  必须以 Bgra32（非预乘）取像素，或按规范化方案解码时显式转 Bgra32。
- V2(52) 头没有 alpha 字段：掩码写不进头（位置越界），legacy 强制 a_mask=0 → A=255。
- 构造测试时注意：`Build` 的行补齐逻辑会把「截断最后一行」自动补齐，真截断用例要
  让最后一行长度恰为 4 的倍数才不被补。

## 未覆盖项 / 真机待人工

1. **legacy 真实产出 PNG 实件比对**：本机无旧版剪贴板管线产出的 PNG 存件；参照由
   legacy 算法直接运行生成（image 0.25.10）。若旧版实际锁定更早 image 小版本，编码字节
   可能有差——不影响结论（规范化比较不依赖编码字节），但如需逐字节确证须在真机用旧版
   实际截图留档比对。
2. **截图工具实拍 alpha 比对（判据 2 后半）**：PixPin/系统截图真实 CF_DIBV5 数据流
   解码结果与旧版历史条目肉眼比对，待 T06 真机验证一并做。
3. **WIC 编码跨进程/重启持久确定性**：同进程确定性已测；跨进程理论上同为 WIC 同版本
   输出，风险极低，正式实现可在 T06 验收时用两进程编码同像素复核一次。
4. **超大图性能**：试验样例为小图；DibDecoder 为 O(宽×高) 纯字节变换，无递归无分配放大，
   预期无风险，真实分辨率（4K 截图）耗时实测归 T06。
5. **WIC 解码器 BMP 路径的行为**：本试验未评估 WIC 自带 BMP 解码是否也踩 image crate
   的 12 字节 bug（决策 1 已绕开：自写解码，不依赖 WIC BMP 解码，此问题不再相关）。
