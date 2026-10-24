# UI Automation for Windows apps (TeknoParrot is WPF, so this is reliable).
#
#   powershell -File tools\win\Uia.ps1 -Process TeknoParrotUi -Tree
#   powershell -File tools\win\Uia.ps1 -Process TeknoParrotUi -Invoke "CONTROLLER SETUP"
#   powershell -File tools\win\Uia.ps1 -Process TeknoParrotUi -Find "Trigger"
#   powershell -File tools\win\Uia.ps1 -Process TeknoParrotUi -ClickAt "Start Button P1"
#   powershell -File tools\win\Uia.ps1 -Process TeknoParrotUi -Tree -Max 400 -OutFile tree.txt
#
# -Tree prints one line per element: depth, control type, name, and screen rect.
# -Invoke uses the Invoke pattern (button); -ClickAt moves the real cursor to
# the element centre and clicks (for fields that must receive a raw click).

param(
    [string]$Process = "",
    [string]$Title = "",
    [switch]$Tree,
    [string]$Invoke = "",
    [string]$Find = "",
    [string]$ClickAt = "",
    [string]$FocusRow = "",
    [string]$ExpandRow = "",
    [string]$SelectRow = "",
    [string]$SelectItem = "",
    [string]$OutFile = "",
    [int]$Max = 250,
    [switch]$Activate,
    [int]$Top = 0
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32U {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr extra);
}
"@

function Get-TargetWindow {
    $p = $null
    if ($Process) {
        $p = Get-Process | Where-Object { $_.ProcessName -like "*$Process*" -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    } elseif ($Title) {
        $p = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Title*" -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    if (-not $p) { throw "no window for process='$Process' title='$Title'" }
    return $p
}

$proc = Get-TargetWindow
$h = $proc.MainWindowHandle
# Pick the largest top-level window of the process: WPF apps with custom chrome
# can report a tooltip or 0 as MainWindowHandle.
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$best = $null; $bestArea = 10000
foreach ($w in $wins) {
    $r = $w.Current.BoundingRectangle
    $area = $r.Width * $r.Height
    if ($area -gt $bestArea) { $bestArea = $area; $best = $w }
}
if ($best) { $h = [IntPtr]$best.Current.NativeWindowHandle }
if ($h -eq 0) { throw "no usable window handle for '$Process'" }

if ($Activate) {
    [void][W32U]::ShowWindow($h, 9)
    [W32U]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [W32U]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][W32U]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 300
}

function SafeInt([double]$v) {
    if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { return 0 }
    if ($v -gt 100000) { return 100000 }
    if ($v -lt -100000) { return -100000 }
    return [int][math]::Round($v)
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$rows = @()
$i = 0
foreach ($e in $all) {
    $i++
    $r = $e.Current.BoundingRectangle
    $rows += [pscustomobject]@{
        i = $i
        type = $e.Current.ControlType.ProgrammaticName -replace "ControlType\.", ""
        name = $e.Current.Name
        auto = $e.Current.AutomationId
        x = (SafeInt $r.X); y = (SafeInt $r.Y); w = (SafeInt $r.Width); h = (SafeInt $r.Height)
        enabled = $e.Current.IsEnabled
        offscreen = $e.Current.IsOffscreen
        el = $e
    }
}

if ($Tree -or $Find) {
    $sel = $rows
    if (-not $Tree -and $Find) { $sel = $rows | Where-Object { $_.name -like "*$Find*" -or $_.auto -like "*$Find*" } }
    $lines = @()
    foreach ($r in ($sel | Select-Object -First $Max)) {
        if ($Top -gt 0 -and $r.y -gt ($Top + 5000)) { continue }
        $lines += ("{0,3} {1,-22} {2,-42} [{3},{4} {5}x{6}] {7}{8}" -f $r.i, $r.type, $r.name, $r.x, $r.y, $r.w, $r.h, $r.auto, $(if ($r.enabled) { "" } else { " DISABLED" }))
    }
    $text = $lines -join "`r`n"
    if ($OutFile) { $text | Set-Content -Path $OutFile -Encoding utf8 }
    Write-Output $text
    Write-Output ("-- {0} descendants, window '{1}' pid {2} hwnd {3}" -f $rows.Count, $proc.MainWindowTitle, $proc.Id, $h)
    return
}

if ($Invoke) {
    $hit = $rows | Where-Object { ($_.name -eq $Invoke -or $_.auto -eq $Invoke) -and $_.type -eq "Button" } | Select-Object -First 1
    if (-not $hit) { $hit = $rows | Where-Object { ($_.name -like "*$Invoke*" -or $_.auto -like "*$Invoke*") -and $_.type -eq "Button" } | Select-Object -First 1 }
    if (-not $hit) { throw "no button matching '$Invoke'" }
    $pattern = $null
    if ($hit.el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
        Write-Output ("invoked '{0}'" -f $hit.name)
    } else {
        throw "'$($hit.name)' has no Invoke pattern"
    }
    return
}

function Find-RowControl([string]$labelName, [string]$type) {
    $label = $rows | Where-Object { $_.name -eq $labelName -and $_.type -eq "Text" } | Select-Object -First 1
    if (-not $label) { throw "no row label '$labelName'" }
    $hit = $rows | Where-Object { $_.type -eq $type -and $_.w -gt 80 -and [math]::Abs($_.y - $label.y) -le 12 } | Select-Object -First 1
    if (-not $hit) { throw "no $type next to '$labelName'" }
    return $hit
}

if ($ExpandRow -or $SelectRow) {
    $row = if ($ExpandRow) { $ExpandRow } else { $SelectRow }
    $combo = Find-RowControl $row "ComboBox"
    if (-not $SelectItem) {
        $combo.el.SetFocus()
        Start-Sleep -Milliseconds 100
        $v = $null
        if ($combo.el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$v)) {
            Write-Output ("value: " + $v.Current.Value)
        }
        $ec = $combo.el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ec.Expand()
        Start-Sleep -Milliseconds 400
        $items = $combo.el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)))
        Write-Output ("items: " + $items.Count)
        foreach ($it in $items) { Write-Output ("  - " + $it.Current.Name) }
        $ec.Collapse()
        return
    }
    $ec = $combo.el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand()
    Start-Sleep -Milliseconds 400
    $items = $combo.el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)))
    $want = $items | Where-Object { $_.Current.Name -eq $SelectItem } | Select-Object -First 1
    if (-not $want) { $want = $items | Where-Object { $_.Current.Name -like "*$SelectItem*" } | Select-Object -First 1 }
    if (-not $want) { $ec.Collapse(); throw "item '$SelectItem' not found ($($items.Count) items)" }
    $si = $want.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $si.Select()
    Start-Sleep -Milliseconds 250
    Write-Output ("selected '" + $want.Current.Name + "' in row '$row'")
    return
}

if ($FocusRow) {
    $label = $rows | Where-Object { $_.name -eq $FocusRow -and $_.type -eq "Text" } | Select-Object -First 1
    if (-not $label) { throw "no row label '$FocusRow'" }
    $edit = $rows | Where-Object { $_.type -eq "Edit" -and $_.w -gt 100 -and [math]::Abs($_.y - $label.y) -le 8 } | Select-Object -First 1
    if (-not $edit) { throw "no edit control next to '$FocusRow'" }
    try { $edit.el.SetFocus() } catch { Write-Output ("setfocus failed: " + $_.Exception.Message) }
    Start-Sleep -Milliseconds 150
    $hasFocus = $edit.el.Current.HasKeyboardFocus
    $focused = $null
    try { $focused = [System.Windows.Automation.AutomationElement]::FocusedElement } catch { }
    Write-Output ("focus row '{0}': edit [{1},{2}] hasFocus={3} focused={4}" -f $FocusRow, $edit.x, $edit.y, $hasFocus, $(if ($focused) { $focused.Current.ControlType.ProgrammaticName + " " + $focused.Current.Name } else { "?" }))
    return
}

if ($ClickAt) {
    $hit = $rows | Where-Object { ($_.name -like "*$ClickAt*" -or $_.auto -like "*$ClickAt*") -and $_.w -gt 0 -and $_.h -gt 0 } | Select-Object -First 1
    if (-not $hit) { throw "no element matching '$ClickAt'" }
    $cx = $hit.x + [int]($hit.w / 2)
    $cy = $hit.y + [int]($hit.h / 2)
    [void][W32U]::ShowWindow($h, 9)
    [W32U]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [W32U]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][W32U]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 250
    [void][W32U]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 60
    [W32U]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [W32U]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
    Write-Output ("clicked '{0}' at {1},{2}" -f $hit.name, $cx, $cy)
    return
}
