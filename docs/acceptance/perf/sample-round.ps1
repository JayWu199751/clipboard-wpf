# T09 性能对比脚本（二）：单轮采样。
# 启动指定 exe → 进程树（主进程 + 全部子进程）按 500ms 采样 → 场景序列注入 → 强杀进程树并确认零残留。
# 指标：Private Bytes / 工作集 / 句柄 / GDI 对象 / CPU 时间（进程树合计）。
# 用法：powershell -File sample-round.ps1 -Exe <路径> -Label <版本标签> -Round <轮次> -OutDir <CSV 输出目录>

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Label,
    [Parameter(Mandatory = $true)][int]$Round,
    [string]$OutDir = ".\perf-results"
)

Add-Type -AssemblyName System.Windows.Forms

# 注入与 GDI 说明：本机安全策略禁止运行时编译（Add-Type P/Invoke），故
# ① 呼出键注入改用 SendKeys（同样合成键盘输入，触发目标进程的全局热键）；
# ② GDI 对象数无法经 GetGuiResources 采样——报告口径中标注为未采样项。

if (Get-Process -Name "ClipboardTool", "clipboard-tool" -ErrorAction SilentlyContinue) {
    Write-Error "已有实例在运行（两代不得同时运行），中止"
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$csvPath = Join-Path $OutDir "$Label-round$Round.csv"

# —— 呼出键注入（全局热键 Ctrl+Shift+V；SendKeys 合成输入触发目标注册的热键） ——
function Send-SummonKey {
    [System.Windows.Forms.SendKeys]::SendWait("^+v")
}

# —— 进程树枚举（主进程 + 全部后代） ——
function Get-TreePids {
    param([int]$RootPid)
    $all = Get-CimInstance Win32_Process -Filter "Name like '%lipboard%' or Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue
    $pids = New-Object System.Collections.Generic.HashSet[int]
    [void]$pids.Add($RootPid)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($p in $all) {
            if ($pids.Contains([int]$p.ParentProcessId) -and -not $pids.Contains([int]$p.ProcessId)) {
                [void]$pids.Add([int]$p.ProcessId)
                $changed = $true
            }
        }
    }
    return $pids
}

# —— 单次采样：进程树合计 ——
function Sample-Tree {
    param([System.Diagnostics.Process]$Root, [System.Diagnostics.Stopwatch]$Clock, [string]$Phase)
    $treePids = Get-TreePids -RootPid $Root.Id
    $priv = 0; $ws = 0; $handles = 0; $cpuMs = 0.0
    foreach ($procId in $treePids) {
        try {
            $p = Get-Process -Id $procId -ErrorAction Stop
            $priv += $p.PrivateMemorySize64
            $ws += $p.WorkingSet64
            $handles += $p.HandleCount
            $cpuMs += $p.TotalProcessorTime.TotalMilliseconds
        } catch { }
    }
    [pscustomobject]@{
        label = $Label; round = $Round
        elapsedMs = [int]$Clock.Elapsed.TotalMilliseconds; phase = $Phase
        treeCount = $treePids.Count
        privateMB = [math]::Round($priv / 1MB, 1)
        workingSetMB = [math]::Round($ws / 1MB, 1)
        handles = $handles
        cpuMs = [int]$cpuMs
    }
}

# —— 场景时间轴（毫秒）：冷启动 0-35s → 空闲停靠 35-60s → 呼出序列 60-96s ——
$summonTimes = @(60000, 66000, 72000, 78000, 84000, 90000)  # 首次 + 5 次重复（toggle）

$proc = Start-Process -FilePath $Exe -PassThru
if (-not $proc) { Write-Error "启动失败：$Exe"; exit 1 }
Start-Sleep -Milliseconds 800
$proc.Refresh()
if ($proc.HasExited) { Write-Error "进程秒退（exit=$($proc.ExitCode)）"; exit 1 }

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$rows = New-Object System.Collections.Generic.List[object]
$nextSummon = 0

try {
    while ($clock.Elapsed.TotalMilliseconds -lt 98000) {
        $phase = if ($clock.ElapsedMilliseconds -lt 35000) { "cold" }
                 elseif ($clock.ElapsedMilliseconds -lt 60000) { "idle" }
                 else { "summon" }
        $rows.Add((Sample-Tree -Root $proc -Clock $clock -Phase $phase))

        if ($nextSummon -lt $summonTimes.Count -and $clock.ElapsedMilliseconds -ge $summonTimes[$nextSummon]) {
            Send-SummonKey
            $nextSummon++
        }
        Start-Sleep -Milliseconds 500
    }
} finally {
    $clock.Stop()
}

# —— 收尾：整树强杀 + 零残留确认 ——
$treeBeforeKill = (Get-TreePids -RootPid $proc.Id).Count
foreach ($procId in (Get-TreePids -RootPid $proc.Id)) {
    try { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue } catch { }
}
Start-Sleep -Seconds 2
$residual = Get-Process -Name "ClipboardTool", "clipboard-tool", "msedgewebview2" -ErrorAction SilentlyContinue |
    Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-3) }
if ($residual) {
    Write-Warning "检测到疑似残留进程：$($residual.Id -join ',')"
}

$rows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

# —— 轮摘要：各阶段末 5 个样本中位（树合计） ——
function Median([double[]]$v) {
    $s = $v | Sort-Object
    if ($s.Count -eq 0) { return 0 }
    $m = [int][math]::Floor(($s.Count - 1) / 2)
    return $s[$m]
}
$summary = foreach ($phase in @("cold", "idle", "summon")) {
    $tail = @($rows | Where-Object { $_.phase -eq $phase } | Select-Object -Last 10)
    [pscustomobject]@{
        label = $Label; round = $Round; phase = $phase
        medianPrivateMB = Median @($tail | ForEach-Object { $_.privateMB })
        medianWorkingSetMB = Median @($tail | ForEach-Object { $_.workingSetMB })
        medianHandles = Median @($tail | ForEach-Object { $_.handles })
        maxTreeCount = ($tail | Measure-Object treeCount -Maximum).Maximum
        totalCpuMs = ($tail | Measure-Object cpuMs -Maximum).Maximum
    }
}
$summaryPath = Join-Path $OutDir "$Label-round$Round-summary.csv"
$summary | Export-Csv -Path $summaryPath -NoTypeInformation -Encoding UTF8

Write-Output "轮完成：$csvPath（样本 $($rows.Count)，树规模峰值 $treeBeforeKill，残留 $(if ($residual) { '有' } else { '无' })）"
