# Drive the G'AIM'E Pico bridge (pico/gun_bridge) from PowerShell.
#
#   # move the P1 absolute mouse to a screen pixel and hold still
#   powershell -File tools\win\Bridge.ps1 -Px 1920 -Py 1080
#
#   # click there (left button = trigger)
#   powershell -File tools\win\Bridge.ps1 -Px 1920 -Py 1080 -Click
#
#   # press a gun button for 150 ms (e.g. Start = forward)
#   powershell -File tools\win\Bridge.ps1 -Button forward -HoldMs 150
#
#   # repeated moves (aim tracking simulation)
#   powershell -File tools\win\Bridge.ps1 -Px 800 -Py 600 -Repeat 40 -IntervalMs 10
#
#   # raw protocol coords and a status probe
#   powershell -File tools\win\Bridge.ps1 -X 16383 -Y 16383 -Status
#
# The last position is remembered in %TEMP%\gaime_bridge_pos.json so button
# presses do not have to repeat coordinates. Prints JSON: frame hex, cursor
# position before/after, foreground window.

param(
    [string]$Port = "COM5",
    [int]$Player = 0,
    [int]$X = -1,
    [int]$Y = -1,
    [double]$Px = -999999,
    [double]$Py = -999999,
    [double]$Fx = -1,
    [double]$Fy = -1,
    [string[]]$Button = @(),
    [switch]$Click,
    [int]$HoldMs = 0,
    [switch]$Keep,
    [int]$Repeat = 1,
    [int]$IntervalMs = 10,
    [switch]$Status,
    [switch]$ReadCursor
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W32B {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@

function Crc8([byte[]]$data) {
    $crc = 0
    foreach ($b in $data) {
        $crc = $crc -bxor $b
        for ($i = 0; $i -lt 8; $i++) {
            if ($crc -band 0x80) { $crc = (($crc -shl 1) -bxor 0x07) -band 0xFF }
            else { $crc = ($crc -shl 1) -band 0xFF }
        }
    }
    return [byte]$crc
}

$statePath = Join-Path $env:TEMP "gaime_bridge_pos.json"
$state = @{ x = 16383; y = 16383; sx = 1920; sy = 1080; serial = "" }
if (Test-Path $statePath) {
    try {
        $s = Get-Content $statePath -Raw | ConvertFrom-Json
        foreach ($k in @("x", "y", "sx", "sy", "serial")) {
            if ($null -ne $s.$k) { $state[$k] = $s.$k }
        }
    } catch { }
}

if ($Status) {
    $ports = [System.IO.Ports.SerialPort]::GetPortNames()
    $open = $null
    try {
        $sp = New-Object System.IO.Ports.SerialPort $Port, 115200
        $sp.Open()
        $open = "free"
        $sp.Close(); $sp.Dispose()
    } catch { $open = "busy: " + $_.Exception.Message }
    $cur = New-Object W32B+POINT
    [void][W32B]::GetCursorPos([ref]$cur)
    [pscustomobject]@{ port = $Port; ports = $ports; portState = $open; state = $state; cursor = @{ x = $cur.X; y = $cur.Y } } | ConvertTo-Json -Depth 5 -Compress
    return
}

# ---- resolve coordinates -------------------------------------------------
$fx = -1.0; $fy = -1.0
if ($X -ge 0 -and $Y -ge 0) {
    $fx = [math]::Min(1.0, [math]::Max(0.0, $X / 32767.0))
    $fy = [math]::Min(1.0, [math]::Max(0.0, $Y / 32767.0))
    $X = [int][math]::Round($fx * 32767); $Y = [int][math]::Round($fy * 32767)
} elseif ($Fx -ge 0 -and $Fy -ge 0) {
    $fx = $Fx; $fy = $Fy
    $X = [int][math]::Round($Fx * 32767); $Y = [int][math]::Round($Fy * 32767)
} elseif ($Px -ne -999999 -and $Py -ne -999999) {
    # Windows maps this absolute HID device onto the PRIMARY screen only
    # (measured: raw 22937 -> cursor x 2687 of 3840).
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $fx = ($Px - $b.X) / $b.Width
    $fy = ($Py - $b.Y) / $b.Height
    $X = [int][math]::Round($fx * 32767); $Y = [int][math]::Round($fy * 32767)
} else {
    $X = [int]$state.x; $Y = [int]$state.y
}

# ---- buttons -------------------------------------------------------------
$map = @{ left = 0x01; right = 0x02; middle = 0x04; back = 0x08; forward = 0x10 }
$bits = 0
foreach ($b in $Button) {
    foreach ($part in ($b -split ",")) {
        $k = $part.Trim().ToLower()
        if ($k -eq "") { continue }
        if (-not $map.ContainsKey($k)) { throw "unknown button '$k' (left,right,middle,back,forward)" }
        $bits = $bits -bor $map[$k]
    }
}

function Make-Frame([int]$btn, [int]$x, [int]$y, [byte]$seq) {
    $body = [byte[]](0xA5, 0x5A, 0x01, [byte]$Player, [byte]$btn, 0x00,
        [byte]($x -band 0xFF), [byte](($x -shr 8) -band 0xFF),
        [byte]($y -band 0xFF), [byte](($y -shr 8) -band 0xFF), $seq)
    return [byte[]]($body + (Crc8 $body))
}

function Get-ForegroundInfo {
    $h = [W32B]::GetForegroundWindow()
    $sb = New-Object System.Text.StringBuilder 512
    [void][W32B]::GetWindowText($h, $sb, 512)
    $pid_ = 0
    [void][W32B]::GetWindowThreadProcessId($h, [ref]$pid_)
    $name = ""
    try { $name = (Get-Process -Id $pid_ -ErrorAction Stop).ProcessName } catch { }
    return @{ title = $sb.ToString(); process = $name }
}

$before = New-Object W32B+POINT
[void][W32B]::GetCursorPos([ref]$before)

$sp = New-Object System.IO.Ports.SerialPort $Port, 115200, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
$sp.ReadTimeout = 200
$sp.WriteTimeout = 500
$sp.Open()
$sp.DiscardInBuffer()
$sp.DiscardOutBuffer()

$seq = 0
try {
    $framesSent = 0
    # initial position frame
    $f = Make-Frame $bits $X $Y $seq; $seq = ($seq + 1) -band 0xFF
    $sp.Write($f, 0, $f.Length); $framesSent++

    if ($Click) {
        Start-Sleep -Milliseconds 40
        $f = Make-Frame 0x01 $X $Y $seq; $seq = ($seq + 1) -band 0xFF
        $sp.Write($f, 0, $f.Length); $framesSent++
        Start-Sleep -Milliseconds ([math]::Max(60, $HoldMs))
        $f = Make-Frame 0x00 $X $Y $seq; $seq = ($seq + 1) -band 0xFF
        $sp.Write($f, 0, $f.Length); $framesSent++
    } elseif ($bits -ne 0 -and -not $Keep) {
        Start-Sleep -Milliseconds ([math]::Max(60, $HoldMs))
        $f = Make-Frame 0x00 $X $Y $seq; $seq = ($seq + 1) -band 0xFF
        $sp.Write($f, 0, $f.Length); $framesSent++
    } elseif ($Repeat -gt 1) {
        for ($i = 1; $i -lt $Repeat; $i++) {
            Start-Sleep -Milliseconds $IntervalMs
            $f = Make-Frame $bits $X $Y $seq; $seq = ($seq + 1) -band 0xFF
            $sp.Write($f, 0, $f.Length); $framesSent++
        }
    }
    $sp.BaseStream.Flush()
    Start-Sleep -Milliseconds 30
} finally {
    $sp.Close(); $sp.Dispose()
}

Start-Sleep -Milliseconds 30
$after = New-Object W32B+POINT
[void][W32B]::GetCursorPos([ref]$after)

$state.x = $X; $state.y = $Y
if ($Px -ne -999999) { $state.sx = [int]$Px; $state.sy = [int]$Py }
$state | ConvertTo-Json -Compress | Set-Content $statePath -Encoding ascii

$fg = Get-ForegroundInfo
[pscustomobject]@{
    port = $Port; player = $Player; x = $X; y = $Y; buttons = $bits
    frames = $framesSent; cursorBefore = @{ x = $before.X; y = $before.Y }
    cursorAfter = @{ x = $after.X; y = $after.Y }
    foreground = $fg
} | ConvertTo-Json -Depth 4 -Compress
