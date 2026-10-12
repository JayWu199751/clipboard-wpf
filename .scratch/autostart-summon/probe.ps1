param([int]$TargetPid = 0)
$ErrorActionPreference = 'Stop'
if ($TargetPid -eq 0) { $TargetPid = (Get-Process -Name ClipboardTool | Select-Object -First 1).Id }
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SummonProbe {
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point p, uint f);
 public delegate bool EnumProc(IntPtr h, IntPtr p);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int n);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
 [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int n);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
 [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
}
'@
[void][SummonProbe]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', "ClipboardTool-$sid-summon", [System.IO.Pipes.PipeDirection]::Out)
try {
    $pipe.Connect(3000)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("CLIPBOARDTOOL summon v2`n")
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
} finally { $pipe.Dispose() }
Start-Sleep -Milliseconds 700
$script:panelFound = $false
$script:panelDisplayed = $false
[SummonProbe]::EnumWindows({ param($h, $p)
    [uint32]$windowPid = 0
    [void][SummonProbe]::GetWindowThreadProcessId($h, [ref]$windowPid)
    if ($windowPid -ne $TargetPid) { return $true }
    $title = [System.Text.StringBuilder]::new(256)
    [void][SummonProbe]::GetWindowTextW($h, $title, 256)
    if ($title.ToString() -ne 'ClipboardTool') { return $true }
    $r = [SummonProbe+Rect]::new()
    [void][SummonProbe]::GetWindowRect($h, [ref]$r)
    [int]$cloaked = 0
    $dwmResult = [SummonProbe]::DwmGetWindowAttribute($h, 14, [ref]$cloaked, 4)
    $point = [SummonProbe+Point]::new()
    $point.X = ($r.Left + $r.Right) / 2
    $point.Y = ($r.Top + $r.Bottom) / 2
    $hit = [SummonProbe]::GetAncestor([SummonProbe]::WindowFromPoint($point), 2)
    [pscustomobject]@{ Hwnd=('0x{0:X}' -f $h.ToInt64()); Visible=[SummonProbe]::IsWindowVisible($h); Rect="$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)"; Cloaked=$cloaked; DwmResult=$dwmResult; ExStyle=('0x{0:X}' -f [SummonProbe]::GetWindowLongPtrW($h,-20).ToInt64()); CenterHit=('0x{0:X}' -f $hit.ToInt64()) } | ConvertTo-Json -Compress | Write-Host
    if ($title.ToString() -eq 'ClipboardTool') {
        $script:panelFound = $true
        $script:panelDisplayed = [SummonProbe]::IsWindowVisible($h) -and $dwmResult -eq 0 -and $cloaked -eq 0 -and $hit -eq $h -and [SummonProbe]::MonitorFromPoint($point,0) -ne [IntPtr]::Zero
    }
    return $true
}, [IntPtr]::Zero) | Out-Null
if (!$script:panelFound -or !$script:panelDisplayed) { Write-Host 'FAIL 呼出后面板没有出现在屏幕中心命中窗口'; exit 1 }
Write-Host 'PASS 呼出后面板实际出现在屏幕中心命中窗口'
