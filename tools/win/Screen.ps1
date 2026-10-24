# Capture the Windows desktop (or one window / rect) to a PNG, and print the
# foreground-window facts an agent needs to click the right pixels.
#
#   powershell -File tools\win\Screen.ps1 -Out shot.png
#   ... -Out shot.png -Rect 0,0,1920,1080 -Scale 0.5
#   ... -Out tp.png -Process TeknoParrotUi
#   ... -Info                      # no capture, just the geometry report
#
# Output is a JSON line on stdout. The PNG path is absolute.

param(
    [string]$Out = "",
    [string]$Process = "",
    [string]$Title = "",
    [string]$Rect = "",
    [double]$Scale = 0,
    [switch]$Info,
    [switch]$Cursor
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W32Cap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
"@

function Window-Info([IntPtr]$h) {
    if ($h -eq [IntPtr]::Zero) { return $null }
    $r = New-Object W32Cap+RECT
    if (-not [W32Cap]::GetWindowRect($h, [ref]$r)) { return $null }
    $pid_ = 0
    [void][W32Cap]::GetWindowThreadProcessId($h, [ref]$pid_)
    $sb = New-Object System.Text.StringBuilder 512
    [void][W32Cap]::GetWindowText($h, $sb, 512)
    $c = New-Object W32Cap+RECT
    [void][W32Cap]::GetClientRect($h, [ref]$c)
    $p = New-Object W32Cap+POINT
    $p.X = 0; $p.Y = 0
    [void][W32Cap]::ClientToScreen($h, [ref]$p)
    $name = ""
    try { $name = (Get-Process -Id $pid_ -ErrorAction Stop).ProcessName } catch { }
    [pscustomobject]@{
        hwnd = $h.ToInt64(); title = $sb.ToString(); process = $name; pid = $pid_
        window = @{ x = $r.Left; y = $r.Top; w = $r.Right - $r.Left; h = $r.Bottom - $r.Top }
        client = @{ x = $p.X; y = $p.Y; w = $c.Right - $c.Left; h = $c.Bottom - $c.Top }
        visible = [W32Cap]::IsWindowVisible($h)
    }
}

$fg = [W32Cap]::GetForegroundWindow()
$cp = New-Object W32Cap+POINT
[void][W32Cap]::GetCursorPos([ref]$cp)

$report = [ordered]@{}
$report.foreground = Window-Info $fg
$report.cursor = @{ x = $cp.X; y = $cp.Y }
$screens = @()
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
    $screens += @{ name = $s.DeviceName; x = $s.Bounds.X; y = $s.Bounds.Y; w = $s.Bounds.Width; h = $s.Bounds.Height; primary = $s.Primary }
}
$report.screens = $screens

if ($Process -or $Title) {
    $cands = @()
    foreach ($p in (Get-Process | Where-Object { $_.MainWindowHandle -ne 0 })) {
        if ($Process -and $p.ProcessName -notlike "*$Process*") { continue }
        if ($Title -and $p.MainWindowTitle -notlike "*$Title*") { continue }
        $cands += (Window-Info $p.MainWindowHandle)
    }
    $report.windows = $cands
    if ($cands.Count -gt 0) {
        $w = $cands[0]
        if (-not $Rect -and -not $Info) { $Rect = "$($w.client.x),$($w.client.y),$($w.client.w),$($w.client.h)" }
    }
}

if ($Info -or $Out -eq "") {
    $report | ConvertTo-Json -Depth 6 -Compress
    return
}

if ($Rect) {
    $rp = $Rect -split "[ ,;]+" | Where-Object { $_ -ne "" }
    if ($rp.Count -ne 4) { throw "Rect needs x,y,w,h" }
    $src = New-Object System.Drawing.Rectangle ([int]$rp[0]), ([int]$rp[1]), ([int]$rp[2]), ([int]$rp[3])
} else {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $src = New-Object System.Drawing.Rectangle $b.X, $b.Y, $b.Width, $b.Height
}

if ($Scale -le 0) { $Scale = 1.0 }
$dstW = [int]($src.Width * $Scale)
$dstH = [int]($src.Height * $Scale)
# CopyFromScreen always blits 1:1; capture full size then scale explicitly.
$raw = New-Object System.Drawing.Bitmap $src.Width, $src.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gr = [System.Drawing.Graphics]::FromImage($raw)
$gr.CopyFromScreen($src.Location, [System.Drawing.Point]::Empty, $src.Size,
    [System.Drawing.CopyPixelOperation]::SourceCopy)
$gr.Dispose()
$bmp = New-Object System.Drawing.Bitmap $dstW, $dstH, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.DrawImage($raw, 0, 0, $dstW, $dstH)
$raw.Dispose()

if ($Cursor) {
    $cx = $cp.X - $src.X; $cy = $cp.Y - $src.Y
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Red), 3
    $g.DrawEllipse($pen, ($cx - 18) * $Scale, ($cy - 18) * $Scale, 36 * $Scale, 36 * $Scale)
    $g.DrawLine($pen, ($cx - 30) * $Scale, $cy * $Scale, ($cx + 30) * $Scale, $cy * $Scale)
    $g.DrawLine($pen, $cx * $Scale, ($cy - 30) * $Scale, $cx * $Scale, ($cy + 30) * $Scale)
    $pen.Dispose()
}
$g.Dispose()

$full = [System.IO.Path]::GetFullPath($Out)
$dir = [System.IO.Path]::GetDirectoryName($full)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$report.capture = @{ path = $full; x = $src.X; y = $src.Y; w = $src.Width; h = $src.Height; scale = $Scale }
$report | ConvertTo-Json -Depth 6 -Compress
