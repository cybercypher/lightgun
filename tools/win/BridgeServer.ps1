# Persistent host-side driver for the G'AIM'E Pico bridge.
#
# Opening/closing the CDC port makes the Pico re-enumerate and re-emit its last
# absolute position, which hijacks the shared Windows cursor. So the port is
# opened once here and commands are appended to a file.
#
#   powershell -File tools\win\BridgeServer.ps1 -Port COM5
#
# Command file (default %TEMP%\gaime_bridge\cmds.txt), one command per line:
#
#   <seq> move <screenX> <screenY>
#   <seq> raw <x0to32767> <y0to32767>
#   <seq> down <buttons> [x y]
#   <seq> up
#   <seq> click <holdMs> [x y]
#   <seq> press <buttons> <holdMs> [x y]
#   <seq> wheel <delta> [x y]
#   <seq> path <x,y;x,y;...> <intervalMs>
#   <seq> quit
#
# <buttons> is a comma list or mask: left,right,middle,back,forward.
# status.txt holds the last executed seq/command plus the cursor position.

param(
    [string]$Port = "COM5",
    [string]$Dir = "",
    [int]$PollMs = 2
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32S {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
}
"@

if (-not $Dir) { $Dir = Join-Path $env:TEMP "gaime_bridge" }
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
$cmdFile = Join-Path $Dir "cmds.txt"
$statusFile = Join-Path $Dir "status.txt"
$logFile = Join-Path $Dir "server.log"

function Log([string]$msg) {
    $line = "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $msg
    Add-Content -Path $logFile -Value $line -Encoding utf8
}

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

$script:player = 0
$script:x = 16384
$script:y = 16384
$script:buttons = 0
$script:seq = 0
$script:sp = $null
$script:primary = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

function Get-Buttons([string]$s) {
    $map = @{ left = 0x01; right = 0x02; middle = 0x04; back = 0x08; forward = 0x10 }
    if ($s -match "^[0-9]+$") { return [int]$s -band 0x1F }
    $bits = 0
    foreach ($part in ($s -split "[,+|]")) {
        $k = $part.Trim().ToLower()
        if ($k -eq "") { continue }
        if (-not $map.ContainsKey($k)) { throw "bad button '$k'" }
        $bits = $bits -bor $map[$k]
    }
    return $bits
}

function Frame([int]$btn, [int]$rawX, [int]$rawY) {
    $body = [byte[]](0xA5, 0x5A, 0x01, [byte]$script:player, [byte]$btn, 0x00,
        [byte]($rawX -band 0xFF), [byte](($rawX -shr 8) -band 0xFF),
        [byte]($rawY -band 0xFF), [byte](($rawY -shr 8) -band 0xFF),
        [byte]($script:seq -band 0xFF))
    $script:seq = ($script:seq + 1) -band 0xFF
    return [byte[]]($body + (Crc8 $body))
}

function Send([int]$btn) {
    if ($null -eq $script:sp) { throw "port not open" }
    $f = Frame $btn $script:x $script:y
    $script:sp.Write($f, 0, $f.Length)
    $script:sp.BaseStream.Flush()
}

function Clamp([int]$v, [int]$lo, [int]$hi) {
    if ($v -lt $lo) { return $lo }
    if ($v -gt $hi) { return $hi }
    return $v
}

function ScreenToRaw([int]$px, [int]$py) {
    $fx = ($px - $script:primary.X) / $script:primary.Width
    $fy = ($py - $script:primary.Y) / $script:primary.Height
    $script:x = Clamp ([int][math]::Round($fx * 32767)) 0 32767
    $script:y = Clamp ([int][math]::Round($fy * 32767)) 0 32767
}

function Set-Position([string[]]$t, [int]$from) {
    if ($t.Count -ge ($from + 2)) {
        $px = [int]$t[$from]; $py = [int]$t[$from + 1]
        ScreenToRaw $px $py
    }
}

function Open-Port {
    try {
        $sp = New-Object System.IO.Ports.SerialPort $Port, 115200, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
        $sp.WriteTimeout = 500
        $sp.ReadTimeout = 200
        $sp.Open()
        $sp.DiscardInBuffer()
        $sp.DiscardOutBuffer()
        $script:sp = $sp
        Log "opened $Port"
        return $true
    } catch {
        $script:sp = $null
        Log "open failed: $($_.Exception.Message)"
        return $false
    }
}

function Write-Status([string]$seq, [string]$cmd, [string]$result) {
    $p = New-Object W32S+POINT
    [void][W32S]::GetCursorPos([ref]$p)
    $obj = [ordered]@{
        seq = $seq; command = $cmd; result = $result
        x = $script:x; y = $script:y; buttons = $script:buttons
        cursor = @{ x = $p.X; y = $p.Y }
        port = $Port; open = ($null -ne $script:sp); time = (Get-Date).ToString("HH:mm:ss.fff")
    }
    $obj | ConvertTo-Json -Compress | Set-Content -Path $statusFile -Encoding ascii
}

Log "server start port=$Port dir=$Dir"
if (-not (Test-Path $cmdFile)) { New-Item -ItemType File -Path $cmdFile | Out-Null }
$processed = 0
Write-Status "0" "init" "ok"
Log "watching $cmdFile"

while ($true) {
    if (-not $script:sp) {
        if (-not (Open-Port)) { Start-Sleep -Milliseconds 500; continue }
        # Park the pointer away from screen edges (the bottom-right corner is
        # "Show Desktop" and would minimise every window).
        $script:x = 16384; $script:y = 16384
        Send 0
        Log "parked at 16384,16384"
    }

    $lines = @()
    try { $lines = @(Get-Content -Path $cmdFile -ErrorAction Stop) } catch { }
    if ($lines.Count -gt $processed) {
        Log "reading $($lines.Count - $processed) new line(s)"
        for ($i = $processed; $i -lt $lines.Count; $i++) {
            $line = "$($lines[$i])".Trim()
            if ($line -eq "") { continue }
            $t = $line -split "\s+"
            $cmdSeq = $t[0]
            $cmd = if ($t.Count -gt 1) { $t[1] } else { "" }
            $result = "ok"
            try {
                switch ($cmd) {
                    "move" { Set-Position $t 2; Send $script:buttons }
                    "raw" {
                        $script:x = Clamp ([int]$t[2]) 0 32767
                        $script:y = Clamp ([int]$t[3]) 0 32767
                        Send $script:buttons
                    }
                    "down" {
                        Set-Position $t 3
                        $b = Get-Buttons $t[2]
                        $script:buttons = $script:buttons -bor $b
                        Send $script:buttons
                    }
                    "up" { $script:buttons = 0; Send 0 }
                    "click" {
                        # click <screenX> <screenY> [holdMs]
                        $ms = if ($t.Count -gt 4) { [int]$t[4] } else { 80 }
                        Set-Position $t 2
                        Send 0x01
                        Start-Sleep -Milliseconds $ms
                        Send 0x00
                    }
                    "tap" {
                        # tap [holdMs]  (left click where the pointer already is)
                        $ms = if ($t.Count -gt 2) { [int]$t[2] } else { 80 }
                        Send 0x01
                        Start-Sleep -Milliseconds $ms
                        Send 0x00
                    }
                    "press" {
                        # press <buttons> [holdMs]  (at the current position)
                        $b = Get-Buttons $t[2]
                        $ms = if ($t.Count -gt 3) { [int]$t[3] } else { 100 }
                        Send $b
                        Start-Sleep -Milliseconds $ms
                        Send 0x00
                    }
                    "wheel" { Set-Position $t 3; Send $script:buttons }
                    "path" {
                        $pts = $t[2] -split ";"
                        $iv = if ($t.Count -gt 3) { [int]$t[3] } else { 8 }
                        foreach ($pt in $pts) {
                            $xy = $pt -split ","
                            if ($xy.Count -ne 2) { continue }
                            ScreenToRaw ([int]$xy[0]) ([int]$xy[1])
                            Send $script:buttons
                            Start-Sleep -Milliseconds $iv
                        }
                    }
                    "pos" { Send $script:buttons }
                    "quit" {
                        Log "quit"
                        try { $script:sp.Close() } catch { }
                        Write-Status $cmdSeq $line "bye"
                        exit 0
                    }
                    default { $result = "unknown $cmd" }
                }
            } catch {
                $result = "error: $($_.Exception.Message)"
                Log "cmd '$line' $result"
            }
            Write-Status $cmdSeq $line $result
        }
        $processed = $lines.Count
    }
    Start-Sleep -Milliseconds $PollMs
}
