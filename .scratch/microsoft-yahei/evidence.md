# 微软雅黑验证记录

日期：2026-10-08。

## 结果

- 系统字体注册表包含 Microsoft YaHei、Bold、Light，应用不分发字体文件。
- 验证程序读取真实 App.xaml；中文与拉丁样例实际 GlyphRun 使用系统 `C:/WINDOWS/FONTS/MSYH.TTC`，族名 Microsoft YaHei，无缺字。
- Normal 与 Medium 的界面请求保持原值；WPF 为 Medium 请求匹配系统字体的 Normal 字重。
- Font.Mono 保持 `JetBrains Mono, Cascadia Code, Consolas`。
- [字体样例](font-preview.png)已渲染检查；样例不能代替真实桌面布局验收。
- 全量测试：470 项通过，0 失败，0 跳过。
- Release win-x64 自包含发布成功，程序集资源不再包含 OTF，发布目录不含思源黑体许可。
- 已打开新的 Debug 测试构建，PID 3216，枚举确认 ClipboardTool 面板窗口可见；当前已安装副本未更新。
- 真实桌面的截断、换行、视觉密度由用户测试验收。

## 审查

- Standards：0 项问题。本地追踪约定、字体集中配置与 ADR 同步符合要求。
- Spec：0 项问题。微软雅黑、等宽区域、资源移除和新实例启动符合规格，没有修改字号或布局。
- 两个独立代理分别审查相对 `4e054195d9f95ddc18a05181edbad2ba90074541` 的本次暂存改动。

## 复现

在仓库根目录用 PowerShell 7 执行：

```powershell
dotnet run --project .scratch/microsoft-yahei/verify/Verify.csproj --artifacts-path .scratch/microsoft-yahei/artifacts -- .scratch/microsoft-yahei/font-preview.png
# 现有图片测试使用输出目录上方三层的固定样例路径，需在隔离目录补齐。
Copy-Item -LiteralPath ClipboardTool.Tests/ReferencePng -Destination .scratch/microsoft-yahei/artifacts/ReferencePng -Recurse -Force
dotnet test ClipboardTool.Tests/ClipboardTool.Tests.csproj --artifacts-path .scratch/microsoft-yahei/artifacts --logger 'console;verbosity=minimal'
dotnet publish ClipboardTool.Presentation.Wpf/ClipboardTool.Presentation.Wpf.csproj -c Release -r win-x64 --self-contained true --artifacts-path .scratch/microsoft-yahei/release-artifacts -o .scratch/microsoft-yahei/publish
```
