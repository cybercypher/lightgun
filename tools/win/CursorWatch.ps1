# Sample the Windows cursor position and log every change with a timestamp.
# Used to see who moves the shared cursor: the Pico bridge, the G'AIM'E
# digitizer, or the real mouse.
#
#   powershell -File tools\win\CursorWatch.ps1 -Seconds 10
#   ... -Seconds 10 -Out \\wsl.localhost\...\logs\cursor.log

param(
    [int]$Seconds = 10,
    [string]$Out = "",
    [int]$PollMs = 3
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32CW {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
}
"@

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$last = $null
$lines = @()
$lines += "cursor watch start $(Get-Date -Format HH:mm:ss.fff)"
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $p = New-Object W32CW+POINT
    [void][W32CW]::GetCursorPos([ref]$p)
    $key = "$($p.X),$($p.Y)"
    if ($key -ne $last) {
        $lines += ("{0,8:n3}  {1}" -f $sw.Elapsed.TotalSeconds, $key)
        $last = $key
    }
    Start-Sleep -Milliseconds $PollMs
}
$lines += "cursor watch end"
$text = $lines -join "`r`n"
if ($Out) { $text | Set-Content -Path $Out -Encoding utf8 }
Write-Output $text
