# Normal-integrity side: clean leftovers, start probe target A and normal notepad N1,
# and write the N1 handshake file for the elevated runner.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File start-target.ps1 -Exe <full path to P2Focus.exe>
param(
  [Parameter(Mandatory = $true)][string]$Exe
)

$ErrorActionPreference = 'Continue'
$dir = Join-Path $env:TEMP 'p2-elevated-focus'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# Kill stale probe targets; close the notepad we started last round (tracked via handshake pid)
Get-Process -Name 'P2Focus' -ErrorAction SilentlyContinue | Stop-Process -Force
$oldN1 = Join-Path $dir 'target-N1.json'
if (Test-Path $oldN1) {
  try {
    $old = Get-Content $oldN1 -Raw | ConvertFrom-Json
    if ($old.pid) {
      $p = Get-Process -Id $old.pid -ErrorAction SilentlyContinue
      if ($p -and $p.ProcessName -eq 'notepad') { $p.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (!$p.HasExited) { $p.Kill() } }
    }
  } catch { }
  Remove-Item $oldN1 -Force -ErrorAction SilentlyContinue
}
foreach ($f in @('target-A.json', 'target-A-events.log', 'target-N1-events.log')) {
  Remove-Item (Join-Path $dir $f) -Force -ErrorAction SilentlyContinue
}

# Probe target A (normal integrity). Verify it stays alive; retry up to 3 times
# because a freshly built exe can be silently swallowed by AV first-scan.
function Start-A {
  for ($i = 1; $i -le 3; $i++) {
    $p = Start-Process -FilePath $Exe -ArgumentList '--target', '--name', 'A', '--exit-after', '600' -PassThru
    Start-Sleep -Seconds 2
    $p.Refresh()
    if (-not $p.HasExited) { Write-Output "A pid=$($p.Id) alive"; return }
    Write-Output "A attempt $i died (exit unknown), retrying"
  }
  throw 'probe target A failed to start'
}
Start-A

# Probe target B (elevated integrity, silent elevation via UAC policy). The runner
# must NOT own this process: a child launched right before activation froze
# reproducibly in earlier rounds, so B is started here and fully settled instead.
function Start-B {
  for ($i = 1; $i -le 3; $i++) {
    $p = Start-Process -FilePath $Exe -ArgumentList '--target', '--name', 'B', '--exit-after', '600' -Verb RunAs -PassThru
    Start-Sleep -Seconds 2
    $p.Refresh()
    if (-not $p.HasExited) { Write-Output "B pid=$($p.Id) alive (elevated)"; return }
    Write-Output "B attempt $i died, retrying"
  }
  throw 'probe target B failed to start'
}
Start-B

function Start-NotepadTarget {
  param([string]$Name, [object]$Proc)
  $target = $null
  for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 100
    $cand = Get-Process -Name 'Notepad' -ErrorAction SilentlyContinue |
      Where-Object { $script:beforeNp -notcontains $_.Id -and $_.Id -ne $Proc.Id -and $_.MainWindowHandle -ne 0 }
    if ($cand) { $target = $cand | Select-Object -First 1; break }
  }
  if ($target) {
    [pscustomobject]@{
      name      = $Name
      hwnd      = [int64]$target.MainWindowHandle
      editHwnd  = 0
      pid       = [int64]$target.Id
      tid       = 0
      integrity = 'readback-by-runner'
      kind      = 'notepad'
    } | ConvertTo-Json | Set-Content -Path (Join-Path $dir "target-$Name.json") -Encoding UTF8
    Write-Output "$Name notepad hwnd=$($target.MainWindowHandle) pid=$($target.Id)"
  } else {
    Write-Output "$Name notepad window NOT found (launcher exited=$($Proc.HasExited))"
  }
}

# Normal notepad N1: System32\notepad.exe is a single-instance broker (the started
# process owns no window); find the real windowed Notepad process by diffing the
# pre-launch process snapshot, then write the handshake.
$beforeNp = @(Get-Process -Name 'Notepad' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$np = Start-Process -FilePath "$env:SystemRoot\System32\notepad.exe" -PassThru
Start-NotepadTarget -Name 'N1' -Proc $np

# Elevated notepad N2 (silent elevation), same broker handling.
$beforeNp = @(Get-Process -Name 'Notepad' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$np2 = Start-Process -FilePath "$env:SystemRoot\System32\notepad.exe" -Verb RunAs -PassThru
Start-NotepadTarget -Name 'N2' -Proc $np2

Write-Output 'targets launched'
