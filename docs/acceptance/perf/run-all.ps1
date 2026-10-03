# T09 性能对比脚本（三）：三轮总编排。
# 纪律（资料包/工单红线）：两代不得同时运行；每轮运行前备份存档、运行后恢复；
# 测完确认零残留进程。同机同数据（make-dataset.ps1 构造的固定数据集）同口径。
# 口径说明（重要，写入报告）：两代均以 asInvoker 完整性运行——旧版 debug 构建（cargo release
# 有 requireAdministrator 清单，自动化无法静默提权）、新版 Release 发布构建以 app.manifest
# （asInvoker）发布变体。提权 release 正式口径列为待用户执行项。
# 用法：powershell -File run-all.ps1 -OldExe <旧版exe> -NewExe <新版exe> -OutDir <结果目录>

param(
    [Parameter(Mandatory = $true)][string]$OldExe,
    [Parameter(Mandatory = $true)][string]$NewExe,
    [string]$OutDir = ".\perf-results"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataDir = "$env:APPDATA\ClipboardTool"
$backupDir = Join-Path $scriptDir "archive-backup"

# —— 存档备份/恢复（每轮前后都恢复基准态，保证三轮同数据） ——
function Backup-Archive {
    if (Test-Path $backupDir) { Remove-Item $backupDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    if (Test-Path $dataDir) {
        Copy-Item $dataDir $backupDir\data -Recurse -Force
    }
}
function Restore-Archive {
    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
    if (Test-Path "$backupDir\data") {
        Copy-Item "$backupDir\data" $dataDir -Recurse -Force
    }
}

# 首轮前备份一次用户真实存档（数据集覆写前）
Backup-Archive

# 用基准数据集覆写存档（构造 200 条 + 8 图）
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir "make-dataset.ps1")
if ($LASTEXITCODE -ne 0) { throw "数据集构造失败" }

try {
    foreach ($round in 1..3) {
        Restore-Archive   # 每轮恢复基准数据集（防程序运行中改写）
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir "sample-round.ps1") `
            -Exe $OldExe -Label "old-tauri-debug" -Round $round -OutDir $OutDir
        Start-Sleep -Seconds 3

        Restore-Archive
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir "sample-round.ps1") `
            -Exe $NewExe -Label "new-wpf-release" -Round $round -OutDir $OutDir
        Start-Sleep -Seconds 3
    }
} finally {
    # 还原用户真实存档
    Restore-Archive
    Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "三轮完成。汇总中位见各 -summary.csv；报告与结论见同目录 04-性能对比报告.md"
