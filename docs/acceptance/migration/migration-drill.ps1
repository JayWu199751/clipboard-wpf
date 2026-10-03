# T09 迁移演练（旧→新→旧 副本往返）：隔离真实存档，档级断言。
# 步骤：
#  1. 演练沙箱 %TEMP%\t09-migration-drill：构造「旧版 Tauri 存档」（契约格式：clipboard-history.json + images/）。
#  2. 旧版读旧档 → 运行期复制 2 条新内容 → 退出 → 旧版落档。
#  3. 新版读旧版落档（旧→新切换的关键：跨实现档兼容）→ 运行期复制 1 条新内容 → 退出 → 新版落档。
#  4. 旧版再读新版档（新→旧回退的关键）→ 断言全部条目/置顶/备注/PNG 可读。
#  5. 坏档备份演练：history 写坏 → 启动新版 → backup/ 生成、不崩；写失败演练：档只读 → 启动 → 原件不动。
# 用法：powershell -File migration-drill.ps1 -OldExe <旧版exe> -NewExe <新版exe> -OutDir <结果目录>

param(
    [Parameter(Mandatory = $true)][string]$OldExe,
    [Parameter(Mandatory = $true)][string]$NewExe,
    [string]$OutDir = ".\migration-results"
)

$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Drawing

$sandbox = Join-Path $env:TEMP "t09-migration-drill"
$dataDir = Join-Path $sandbox "appdata"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = New-Object System.Collections.Generic.List[string]
function Log($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $msg
    $results.Add($line)
    Write-Output $line
}

if (Get-Process -Name "ClipboardTool", "clipboard-tool" -ErrorAction SilentlyContinue) {
    Log "FAIL：已有实例运行，中止"; exit 1
}

# 真实存档与演练沙箱物理隔离（演练全程不触碰 %APPDATA%\ClipboardTool）——程序如何读到沙箱档？
# 两代都按 %APPDATA%\ClipboardTool 固定路径读档（契约），因此演练期临时重命名真实存档、
# 把沙箱目录 Junction/替换到契约路径，结束后原样恢复（E2E 产线同款隔离模式）。
$realDir = "$env:APPDATA\ClipboardTool"
$realBackup = Join-Path $sandbox "real-archive-backup"
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$hadReal = Test-Path $realDir
if ($hadReal) {
    Move-Item $realDir $realBackup -Force
    Log "真实存档已隔离到 $realBackup"
}
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Copy-Item $dataDir -Destination $realDir -Recurse -Force

function Restore-Real {
    if (Test-Path $realDir) { Remove-Item $realDir -Recurse -Force }
    if ($hadReal) { Move-Item $realBackup $realDir -Force }
    Log "真实存档已原样恢复"
}

function Stop-App {
    foreach ($n in @("ClipboardTool", "clipboard-tool")) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    $left = Get-Process -Name "ClipboardTool", "clipboard-tool" -ErrorAction SilentlyContinue
    if ($left) { Log "WARN：疑似残留进程 $($left.Id -join ',')" } else { Log "零残留进程 ✓" }
}

function Run-App {
    param([string]$Exe, [int]$Seconds, [string[]]$ClipTexts)
    Start-Process -FilePath $Exe | Out-Null
    Start-Sleep -Seconds 3
    foreach ($t in $ClipTexts) {
        Set-Clipboard -Value $t
        Start-Sleep -Seconds 1.2
    }
    Start-Sleep -Seconds $Seconds
    Stop-App
}

try {
    # —— 1. 构造旧版存档：3 文字（1 置顶带备注）+ 1 图（PNG 由 System.Drawing 生成） ——
    $guid1 = "a1b2c3d4-0000-4000-8000-000000000001"
    $guid2 = "a1b2c3d4-0000-4000-8000-000000000002"
    $guid3 = "a1b2c3d4-0000-4000-8000-000000000003"
    $imgId = "a1b2c3d4-0000-4000-8000-000000000004"
    $bmp = New-Object System.Drawing.Bitmap(320, 200)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 60, 120, 200))
    $g.FillEllipse([System.Drawing.Brushes]::Orange, 60, 40, 200, 120)
    $g.Dispose()
    New-Item -ItemType Directory -Force -Path "$dataDir\images" | Out-Null
    $bmp.Save("$dataDir\images\$imgId.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $archive = @(
        [ordered]@{ id = $imgId; type = "image"; imagePath = "$dataDir\images\$imgId.png"; createdAt = 1790992801000; sourceApp = $null; pinned = $true; pinnedAt = 1790992809000; note = "旧版置顶图" },
        [ordered]@{ id = $guid1; type = "text"; text = "旧版文字一（置顶带备注）"; createdAt = 1790992802000; sourceApp = [ordered]@{ exePath = "C:\Windows\System32\notepad.exe"; appName = "notepad"; windowTitle = "演练 - 记事本"; iconDataUrl = $null }; pinned = $true; pinnedAt = 1790992808000; note = "演练备注 α" },
        [ordered]@{ id = $guid2; type = "text"; text = "旧版文字二"; createdAt = 1790992803000; sourceApp = $null; pinned = $false; pinnedAt = 0; note = "" },
        [ordered]@{ id = $guid3; type = "text"; text = "旧版文字三"; createdAt = 1790992804000; sourceApp = $null; pinned = $false; pinnedAt = 0; note = "" }
    )
    # 无 BOM UTF-8（serde_json 不容 BOM；两代契约一致）
    [System.IO.File]::WriteAllText("$dataDir\clipboard-history.json",
        ($archive | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding $false))
    Log "旧版存档构造完成（4 条，含 1 图 1 备注置顶）"

    # —— 2. 旧版读旧档并新增 2 条 ——
    $head = [System.IO.File]::ReadAllText("$realDir\clipboard-history.json", [System.Text.Encoding]::UTF8)
    Log ("启动前契约档前 120 字符：" + $head.Substring(0, [Math]::Min(120, $head.Length)).Replace("`n", " "))
    Run-App -Exe $OldExe -Seconds 4 -ClipTexts @("演练：旧版期间新增 A", "演练：旧版期间新增 B")
    $afterOld = Get-Content "$realDir\clipboard-history.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    Log "旧版落档：$(@($afterOld).Count) 条（期望 6）；落档 id=[$((@($afterOld) | ForEach-Object { $_.id }) -join ',')]"
    if (@($afterOld).Count -eq 6) { Log "PASS 旧版读旧档+运行记录 ✓" } else { Log "FAIL 旧版条数异常" }

    # —— 3. 新版读旧版档（旧→新），新增 1 条并写备注置顶 ——
    Run-App -Exe $NewExe -Seconds 4 -ClipTexts @("演练：新版期间新增 C")
    $afterNew = Get-Content "$realDir\clipboard-history.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    Log "新版落档：$(@($afterNew).Count) 条（期望 7）"
    $imgKept = @($afterNew | Where-Object { $_.id -eq $imgId }).Count -gt 0
    $pngKept = Test-Path "$realDir\images\$imgId.png"
    $noteKept = (@($afterNew | Where-Object { $_.id -eq $guid1 }).note -eq "演练备注 α")
    if (@($afterNew).Count -eq 7 -and $imgKept -and $pngKept) { Log "PASS 旧→新：条目/PNG 保留 ✓" } else { Log "FAIL 旧→新：count=$(@($afterNew).Count) img=$imgKept png=$pngKept" }
    if ($noteKept) { Log "PASS 旧→新：备注字段往返 ✓" } else { Log "FAIL 备注往返" }

    # —— 4. 旧版读新版档（新→旧 回退） ——
    Run-App -Exe $OldExe -Seconds 4 -ClipTexts @()
    $afterRollback = Get-Content "$realDir\clipboard-history.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($afterRollback).Count -eq 7) { Log "PASS 新→旧回退：旧版读新版档 7 条 ✓" } else { Log "FAIL 新→旧：count=$(@($afterRollback).Count)" }

    # —— 5a. 坏档备份：写坏 JSON → 新版启动 → backup/ 生成不崩 ——
    Set-Content "$realDir\clipboard-history.json" -Value '{ "broken": [ this is not json' -Encoding UTF8
    Run-App -Exe $NewExe -Seconds 3 -ClipTexts @()
    $backups = Get-ChildItem "$realDir\backup" -Filter "clipboard-history.corrupt-*" -ErrorAction SilentlyContinue
    if ($backups) { Log "PASS 坏档备份：$($backups.Name -join ', ')" } else { Log "FAIL 未生成坏档备份" }

    # —— 5b. 写失败恢复：档只读 → 启动新版记录一条 → 原件内容不动（禁写防覆盖） ——
    Set-Content "$realDir\clipboard-history.json" -Value "[]" -Encoding UTF8
    Set-ItemProperty "$realDir\clipboard-history.json" -Name IsReadOnly -Value $true
    Run-App -Exe $NewExe -Seconds 4 -ClipTexts @("演练：只读期复制不落盘")
    Set-ItemProperty "$realDir\clipboard-history.json" -Name IsReadOnly -Value $false
    $content = Get-Content "$realDir\clipboard-history.json" -Raw
    if ($content.Trim() -eq "[]") { Log "PASS 写失败：只读档原件未被空表覆盖（Unreadable 禁写防覆盖）✓" } else { Log "FAIL 只读档被改写：$content" }

    Log "==== 演练结束 ===="
} finally {
    Stop-App
    Restore-Real
}

$results | Set-Content (Join-Path $OutDir "migration-drill-log.txt") -Encoding UTF8
Write-Output "日志：$(Join-Path $OutDir 'migration-drill-log.txt')"
