# Clipboard WPF

一个面向 Windows 的本地剪贴板历史工具，使用 C#、WPF 和 .NET 10 编写。常驻系统托盘，通过快捷键呼出面板，搜索、置顶并粘贴此前复制的文字和图片。

## 安装

从 [Releases](https://github.com/JayWu199751/clipboard-wpf/releases/latest) 下载 `ClipboardTool-Setup.exe`，运行安装即可。

- 支持 Windows 10 22H2 / Windows 11，x64。
- 安装包自带 .NET 桌面运行时，无需另行安装。
- 安装及正式版运行需要管理员权限；可在托盘菜单设置开机启动。
- 从旧 Tauri 版切换时，请先备份数据并退出旧版，详见[切换与回退指南](docs/acceptance/02-切换与回退指南.md)。

## 功能

- 自动记录文字、图片和截图，保留最多 200 条历史（含置顶），重复内容合并。
- 显示来源应用，为条目添加备注、置顶或删除；删除后有 6 秒撤销时间。
- 搜索正文、备注和来源信息，支持多个关键词。
- 选择历史内容后复制并粘贴回原窗口。
- 亮色、暗色和跟随系统主题，托盘菜单同步主题。
- 自定义全局呼出快捷键，支持多显示器和不同 DPI。

## 快捷键

默认使用 **Ctrl + Shift + V** 呼出或收起面板，也可以点击托盘图标。

浏览面板时：

| 按键 | 操作 |
| --- | --- |
| ↑ / ↓ | 选择条目 |
| Enter | 复制并粘贴选中条目 |
| Space | 搜索 |
| Z | 置顶 / 取消置顶 |
| B | 编辑备注 |
| Delete | 删除，可在 6 秒内撤销 |
| Esc | 收起面板；搜索时先退出搜索 |

搜索和备注编辑时，普通字符键用于输入。右键托盘图标可更换快捷键、切换主题、设置开机启动、清空历史或退出。

## 本地数据

历史、设置、图片与诊断日志保存在 `%APPDATA%\ClipboardTool`。卸载默认保留数据；备份该目录即可保留历史和设置。托盘菜单中的“清空历史”会立即删除全部历史和图片。

## 开发与构建

需要 Windows、PowerShell 7 和 .NET SDK **10.0.401**（版本策略见 `global.json`）。

```powershell
git clone https://github.com/JayWu199751/clipboard-wpf.git
cd clipboard-wpf
dotnet build ClipboardTool.sln -c Release
dotnet test ClipboardTool.sln -c Release
dotnet run --project ClipboardTool.Presentation.Wpf -c Debug
```

Debug 使用当前用户权限，Release 使用管理员清单。

构建安装包还需安装 NSIS，并在 PowerShell 7 中执行：

```powershell
pwsh.exe -NoProfile -File installer/build-installer.ps1
```

产物：`installer/ClipboardTool-Setup.exe`。安装包和构建输出不纳入 Git，通过 GitHub Releases 分发。

## 项目结构

| 目录 | 内容 |
| --- | --- |
| `ClipboardTool.Domain` | 历史、搜索、键位及面板规则 |
| `ClipboardTool.Application` | 业务编排与平台端口 |
| `ClipboardTool.Infrastructure.Windows` | 剪贴板、热键、托盘、存储及 Windows 集成 |
| `ClipboardTool.Presentation.Wpf` | WPF 界面与应用入口 |
| `ClipboardTool.Tests` | 自动化测试 |
| `installer` | NSIS 安装脚本与打包入口 |
| `tools` / `prototype` | 验证工具与历史技术试验 |
| `docs` / `.scratch` | 文档、原始重写资料、工单与验证记录 |

当前 F01–F48 功能与人工验收已完成。用户使用一周反馈占用约 100 MB 并接受当前表现，该观察未指定内存采样指标。[文档导航](docs/README.md)包含验收记录、架构决策和开发资料。
