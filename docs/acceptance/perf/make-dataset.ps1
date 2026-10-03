# T09 性能对比脚本（一）：构造两代共用的基准存档数据集。
# 数据集 = 200 条（含 5 条置顶 + 8 张图片，其中 1 张 4000x3000 大图），
# 直接写 %APPDATA%\ClipboardTool（两代同格式契约：clipboard-history.json + images/<id>.png）。
# 用法：powershell -File make-dataset.ps1
# 注意：运行前请确认两代程序均未运行（脚本会检测并拒绝）。

param(
    [string]$DataDir = "$env:APPDATA\ClipboardTool"
)

Add-Type -AssemblyName System.Drawing

# —— 前置：目标进程不得在运行（两代不得同时运行、写档期也不得运行） ——
foreach ($name in @("ClipboardTool", "clipboard-tool")) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        Write-Error "检测到 $name 在运行，请先退出再构造数据集"
        exit 1
    }
}

if (Test-Path "$DataDir\clipboard-history.json") {
    Write-Warning "已存在存档 $DataDir，脚本仅在其 images 与 json 上覆写数据集内容（结构契约一致）"
}

New-Item -ItemType Directory -Force -Path "$DataDir\images" | Out-Null

# —— 生成测试 PNG：1 张 4000x3000 大图（唯一标识大图）+ 7 张 300x200 小图 ——
function New-TestPng {
    param([string]$Path, [int]$Width, [int]$Height, [int]$Seed)
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $rand = New-Object System.Random($Seed)
    # 渐变底 + 随机色块：像素内容有区分度且可压缩，接近真实截图构成
    $rect = New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, [System.Drawing.Color]::FromArgb(255, ($Seed * 37) % 256, 120, 200),
        [System.Drawing.Color]::FromArgb(255, 30, 90, ($Seed * 61) % 256), 45)
    $g.FillRectangle($brush, $rect)
    for ($i = 0; $i -lt 40; $i++) {
        $c = [System.Drawing.Color]::FromArgb(255, $rand.Next(256), $rand.Next(256), $rand.Next(256))
        $b = New-Object System.Drawing.SolidBrush($c)
        $g.FillRectangle($b, $rand.Next($Width), $rand.Next($Height), $rand.Next(80) + 20, $rand.Next(80) + 20)
        $b.Dispose()
    }
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# —— 条目构造：id 与 legacy 同为带连字符 UUID；时间取固定 epoch 毫秒（可复现） ——
$entries = @()
$baseMs = 1790992800000
$imgDefs = @(
    @{ w = 4000; h = 3000 }, @{ w = 300; h = 200 }, @{ w = 300; h = 200 },
    @{ w = 300; h = 200 },  @{ w = 300; h = 200 }, @{ w = 300; h = 200 },
    @{ w = 300; h = 200 },  @{ w = 300; h = 200 }
)

for ($i = 0; $i -lt $imgDefs.Count; $i++) {
    $guid = [guid]::NewGuid().ToString()
    $pngPath = "$DataDir\images\$guid.png"
    New-TestPng -Path $pngPath -Width $imgDefs[$i].w -Height $imgDefs[$i].h -Seed ($i + 1)
    $pinned = ($i -lt 2)
    $entries += [ordered]@{
        id = $guid
        type = "image"
        imagePath = "$DataDir\images\$guid.png"
        createdAt = $baseMs + $i * 1000
        sourceApp = $null
        pinned = $pinned
        pinnedAt = $(if ($pinned) { $baseMs + $i * 1000 + 500 } else { 0 })
        note = ""
    }
}

for ($i = 0; $i -lt 192; $i++) {
    $guid = [guid]::NewGuid().ToString()
    $pinned = ($i -lt 3)
    $entries += [ordered]@{
        id = $guid
        type = "text"
        text = "基准条目 $i：剪贴板重写性能对比固定数据集，含中文与 ASCII mixed content 1234567890"
        createdAt = $baseMs + 10000 + $i * 1000
        sourceApp = [ordered]@{
            exePath = "C:\Windows\System32\notepad.exe"
            appName = "notepad"
            windowTitle = "基准 - 记事本"
            iconDataUrl = $null
        }
        pinned = $pinned
        pinnedAt = $(if ($pinned) { $baseMs + 10000 + $i * 1000 + 500 } else { 0 })
        note = ""
    }
}

# legacy 顺序：置顶块在最前（新→旧），其后普通块
$sorted = @($entries | Where-Object { $_.pinned } | Sort-Object { $_.pinnedAt } -Descending) +
          @($entries | Where-Object { -not $_.pinned } | Sort-Object { $_.createdAt } -Descending)
$sorted | ConvertTo-Json -Depth 6 | Set-Content -Path "$DataDir\clipboard-history.json" -Encoding UTF8

$imgFiles = (Get-ChildItem "$DataDir\images\*.png" | Measure-Object).Count
$imgBytes = (Get-ChildItem "$DataDir\images\*.png" | Measure-Object Length -Sum).Sum
Write-Output "数据集就绪：条目 $($sorted.Count)（置顶 5，图片 8，含 4000x3000 大图 1），PNG $imgFiles 个 / $([math]::Round($imgBytes/1MB, 1)) MB"
