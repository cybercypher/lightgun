# Real (non-bridge) mouse + keyboard input for driving Windows apps.
#
#   powershell -File tools\win\WinInput.ps1 -Move 100,200 -Click
#   powershell -File tools\win\WinInput.ps1 -Click -At 640,400        # move then click
#   powershell -File tools\win\WinInput.ps1 -Key enter
#   powershell -File tools\win\WinInput.ps1 -Text "hello"
#   powershell -File tools\win\WinInput.ps1 -Activate TeknoParrotUi
#
# `-Activate` brings a window to the foreground before sending anything, which
# matters for TP: its setup dialog only sees raw input while focused.

param(
    [int[]]$At = @(),
    [int[]]$Move = @(),
    [switch]$Click,
    [switch]$RightClick,
    [switch]$DoubleClick,
    [switch]$Down,
    [switch]$Up,
    [string]$Key = "",
    [string]$Text = "",
    [string]$Activate = "",
    [string]$Minimize = "",
    [string]$MinimizeTitle = "",
    [string]$Title = "",
    [int]$KeyHoldMs = 40,
    [int]$DelayMs = 120
)

$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W32I {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string name);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
}
"@

$MOUSEEVENTF_LEFTDOWN = 0x02
$MOUSEEVENTF_LEFTUP = 0x04
$MOUSEEVENTF_RIGHTDOWN = 0x08
$MOUSEEVENTF_RIGHTUP = 0x10

$result = [ordered]@{}

if ($MinimizeTitle) {
    $script:min = @()
    $cb = [W32I+EnumProc]{
        param($h, $l)
        $sb = New-Object System.Text.StringBuilder 512
        [void][W32I]::GetWindowText($h, $sb, 512)
        $t = $sb.ToString()
        if ($t -like "*$MinimizeTitle*") {
            $cls = New-Object System.Text.StringBuilder 256
            [void][W32I]::GetClassName($h, $cls, 256)
            $pid_ = 0
            [void][W32I]::GetWindowThreadProcessId($h, [ref]$pid_)
            $pn = ""
            try { $pn = (Get-Process -Id $pid_ -ErrorAction Stop).ProcessName } catch { }
            [void][W32I]::ShowWindow($h, 6)   # SW_MINIMIZE
            $script:min += @{ title = $t; class = $cls.ToString(); process = $pn; pid = $pid_ }
        }
        return $true
    }
    [void][W32I]::EnumWindows($cb, [IntPtr]::Zero)
    $result.minimizedByTitle = $script:min
}

if ($Minimize) {
    $done = @()
    foreach ($p in (Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.ProcessName -like "*$Minimize*" })) {
        [void][W32I]::ShowWindow($p.MainWindowHandle, 6)   # SW_MINIMIZE
        $done += @{ process = $p.ProcessName; title = $p.MainWindowTitle; pid = $p.Id }
    }
    $result.minimized = $done
}

if ($Activate -or $Title) {
    $target = $null
    foreach ($p in (Get-Process | Where-Object { $_.MainWindowHandle -ne 0 })) {
        if ($Activate -and $p.ProcessName -notlike "*$Activate*") { continue }
        if ($Title -and $p.MainWindowTitle -notlike "*$Title*") { continue }
        $target = $p; break
    }
    if (-not $target) { throw "no window matching '$Activate $Title'" }
    $h = $target.MainWindowHandle
    [void][W32I]::ShowWindow($h, 9)   # SW_RESTORE
    # Windows refuses SetForegroundWindow to a process that does not own the
    # foreground; a synthetic ALT tap releases that lock.
    [W32I]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [W32I]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][W32I]::SetForegroundWindow($h)
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline -and [W32I]::GetForegroundWindow() -ne $h) {
        try { (New-Object -ComObject WScript.Shell).AppActivate($target.Id) | Out-Null } catch { }
        [void][W32I]::SetForegroundWindow($h)
        Start-Sleep -Milliseconds 150
    }
    $result.activated = @{
        process = $target.ProcessName; title = $target.MainWindowTitle; pid = $target.Id
        foreground = ([W32I]::GetForegroundWindow() -eq $h)
    }
}

if ($At.Count -eq 2) {
    [void][W32I]::SetCursorPos($At[0], $At[1])
    Start-Sleep -Milliseconds 60
}
if ($Move.Count -eq 2) {
    [void][W32I]::SetCursorPos($Move[0], $Move[1])
    Start-Sleep -Milliseconds 60
}

if ($Click -or $Down -or $Up -or $DoubleClick -or $RightClick) {
    $clicks = 1
    if ($DoubleClick) { $clicks = 2 }
    for ($i = 0; $i -lt $clicks; $i++) {
        if ($RightClick) {
            [W32I]::mouse_event($MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds $KeyHoldMs
            [W32I]::mouse_event($MOUSEEVENTF_RIGHTUP, 0, 0, 0, [UIntPtr]::Zero)
        } else {
            [W32I]::mouse_event($MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds $KeyHoldMs
            [W32I]::mouse_event($MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        }
        Start-Sleep -Milliseconds ([math]::Max(40, $DelayMs))
    }
    $result.clicked = $true
}

$vkMap = @{
    enter = 0x0D; esc = 0x1B; escape = 0x1B; space = 0x20; tab = 0x09; back = 0x08
    up = 0x26; down = 0x28; left = 0x25; right = 0x27; delete = 0x2E; home = 0x24; end = 0x23
    pageup = 0x21; pagedown = 0x22; f1 = 0x70; f2 = 0x71; f3 = 0x72; f4 = 0x73; f5 = 0x74
    f6 = 0x75; f7 = 0x76; f8 = 0x77; f9 = 0x78; f10 = 0x79; f11 = 0x7A; f12 = 0x7B
}
if ($Key) {
    $vk = $null
    if ($vkMap.ContainsKey($Key.ToLower())) { $vk = $vkMap[$Key.ToLower()] }
    elseif ($Key.Length -eq 1) { $vk = [int][char]$Key.ToUpper() }
    else { throw "unknown key '$Key'" }
    [W32I]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $KeyHoldMs
    [W32I]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    $result.key = $Key
}

if ($Text) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait($Text)
    Start-Sleep -Milliseconds 80
    $result.text = $Text
}

$p = New-Object W32I+POINT
[void][W32I]::GetCursorPos([ref]$p)
$fg = New-Object System.Text.StringBuilder 512
[void][W32I]::GetWindowText([W32I]::GetForegroundWindow(), $fg, 512)
$result.cursor = @{ x = $p.X; y = $p.Y }
$result.foreground = $fg.ToString()
$result | ConvertTo-Json -Depth 4 -Compress
