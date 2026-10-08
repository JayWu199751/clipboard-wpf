# 思源黑体验证记录

> 本记录对应历史提交 `4e05419`。后续用户指定改为微软雅黑并移除思源黑体，见 [新验证记录](../microsoft-yahei/evidence.md)。本页复现命令及字体来源说明仅对历史提交适用。

日期：2026-10-08。

## 字体与资源

- 从 Adobe 官方仓库 2.005R 下载，未修改 OTF 文件；版本、URL 和 SHA-256 见历史提交中的 `ClipboardTool.Presentation.Wpf/Assets/Fonts/SourceHanSans/README.md`。
- 验证程序载入实际 `App.xaml`，不执行应用启动链路；实际 GlyphRun 的 URI 分别是程序集内的 Regular、Medium，字重一致，中文及拉丁样例没有缺字。
- `Font.Mono` 仍为 `JetBrains Mono, Cascadia Code, Consolas`。
- [渲染样例](font-preview.png)仅验证字体加载，不能代替真实桌面验收。

## 命令

在仓库根目录，用 PowerShell 7 执行：

```powershell
dotnet run --project .scratch/source-han-sans/verify/Verify.csproj --artifacts-path .scratch/source-han-sans/artifacts -- .scratch/source-han-sans/font-preview.png
dotnet test ClipboardTool.Tests/ClipboardTool.Tests.csproj --artifacts-path .scratch/source-han-sans/artifacts --logger 'console;verbosity=minimal'
# 现有图片测试使用输出目录上方三层的固定样例路径；隔离目录需补齐原样例。
Copy-Item -LiteralPath ClipboardTool.Tests/ReferencePng -Destination .scratch/source-han-sans/artifacts/ReferencePng -Recurse -Force
dotnet test ClipboardTool.Tests/ClipboardTool.Tests.csproj --artifacts-path .scratch/source-han-sans/artifacts --no-build --no-restore --logger 'console;verbosity=minimal'
dotnet publish ClipboardTool.Presentation.Wpf/ClipboardTool.Presentation.Wpf.csproj -c Release -r win-x64 --self-contained true --artifacts-path .scratch/source-han-sans/release-artifacts -o .scratch/source-han-sans/publish
```

## 结果与边界

- 全量测试：470 通过，0 失败，0 跳过。
- Release win-x64 自包含发布成功；输出在 `.scratch/source-han-sans/publish/`。
- 发布程序集的 `ClipboardTool.g.resources` 包含两份 OTF；`Assets/Fonts/SourceHanSans/LICENSE.txt` 存在且 SHA-256 与源文件一致。
- 当前运行的应用和已安装副本未更新；没有构建或执行安装程序。
- 实际桌面上的换行、截断、视觉密度未验收。

## 约定审查（Standards）

0 项问题。规格、工单和字段符合本地 Markdown 约定；分发决策已同步 ADR；字体配置集中于共用资源和项目资源声明。

## 规格审查（Spec）

0 项问题。普通文字与等宽区域符合确认范围，Regular 与 Medium 内置字体及许可覆盖构建和发布，未增加字体选择设置或修改现有字号与布局。

审查范围为相对 `6f44e99375fd496462459e36b2f4f18ca9d19e6c` 的本次字体暂存改动；由两个独立审查代理分别检查约定和规格。
