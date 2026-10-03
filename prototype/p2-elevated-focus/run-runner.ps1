# Elevated side: silently elevate and run the runner (ConsentPromptBehaviorAdmin=0 on
# this machine means no prompt), wait for exit, propagate the exit code.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File run-runner.ps1 -Exe <full path to P2Focus.exe>
param(
  [Parameter(Mandatory = $true)][string]$Exe
)

$p = Start-Process -FilePath $Exe -ArgumentList 'run' -Verb RunAs -PassThru -Wait
exit $p.ExitCode
