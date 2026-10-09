# ClipboardTool NSIS 安装包构建（F38）。
# 用法（管理员不必需；构建机需 dotnet SDK 10 与 NSIS makensis）：
#   pwsh.exe -NoProfile -File installer/build-installer.ps1
# 产物：installer/ClipboardTool-Setup.exe

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 1) 自包含发布（ADR-0001：携带 .NET 10 桌面运行时；清单走 app.release.manifest=requireAdministrator）
$publish = Join-Path $PSScriptRoot "publish"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root "ClipboardTool.Presentation.Wpf/ClipboardTool.Presentation.Wpf.csproj") `
    -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# 2) makensis（脚本内 File /r publish\*.* 相对路径基于本目录）
$makensis = Get-ChildItem "C:\Program Files (x86)\NSIS\makensis.exe", "C:\Program Files\NSIS\makensis.exe" `
    -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $makensis) { throw "未找到 makensis（winget install NSIS.NSIS）" }
& $makensis.FullName (Join-Path $PSScriptRoot "ClipboardTool.nsi")
if ($LASTEXITCODE -ne 0) { throw "makensis 失败" }

$setup = Get-Item (Join-Path $PSScriptRoot "ClipboardTool-Setup.exe")
"安装包：$($setup.FullName)  体积：{0:N1} MB" -f ($setup.Length / 1MB) | Write-Host
