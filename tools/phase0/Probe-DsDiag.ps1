# Phase 0 probe: list the devices DsDiag sees, or probe one of them.
#
# DsDiag.exe is the official DemulShooter RawInput diagnostic and registers the
# same usages as DS itself (Joystick 0x01/0x04, Mouse 0x01/0x02, Gamepad
# 0x01/0x05). A device DsDiag can read is a device DS can read.
#
# DsDiag in a downloaded DemulShooter folder carries a Zone.Identifier and will
# not launch in place; this script runs an unblocked copy from C:\Users\Public\p0.
#
# Selecting some vendor HID collections crashes DsDiag, so probe one index per
# fresh instance rather than sweeping the whole list.
#
# Usage:
#   powershell -File Probe-DsDiag.ps1 -ListOnly
#   powershell -File Probe-DsDiag.ps1 -Index 6
#   powershell -File Probe-DsDiag.ps1 -ListOnly -DsDiagExe 'D:\ds\DsDiag.exe'

param(
  [string]$DsDiagExe = 'C:\games\demulshooter\DsDiag.exe',
  [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
  [switch]$ListOnly,
  [int]$Index = -1,
  [int]$SettleMs = 1200,
  [int]$SampleMs = 800
)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class P0DsDiag {
  public static List<IntPtr> Combos(IntPtr h) {
    var o = new List<IntPtr>();
    EnumChildWindows(h, delegate(IntPtr c, IntPtr l) {
      var sb = new StringBuilder(256); GetClassName(c, sb, 256);
      if (sb.ToString().IndexOf("COMBOBOX") >= 0) o.Add(c);
      return true;
    }, IntPtr.Zero);
    return o;
  }
  public static List<string> Statics(IntPtr h) {
    var o = new List<string>();
    EnumChildWindows(h, delegate(IntPtr c, IntPtr l) {
      var sb = new StringBuilder(256); GetClassName(c, sb, 256);
      if (sb.ToString().IndexOf("STATIC") >= 0) {
        var tx = new StringBuilder(8192); SendMessage(c, 0x000D, (IntPtr)8192, tx);
        if (tx.ToString().Length > 0) o.Add(tx.ToString());
      }
      return true;
    }, IntPtr.Zero);
    return o;
  }
  public static List<string> Items(IntPtr c) {
    var o = new List<string>();
    int n = SendMessage(c, 0x0146, IntPtr.Zero, IntPtr.Zero).ToInt32();
    for (int i = 0; i < n; i++) {
      int len = SendMessage(c, 0x0149, (IntPtr)i, IntPtr.Zero).ToInt32();
      var sb = new StringBuilder(Math.Max(len, 0) + 2);
      SendMessage(c, 0x0148, (IntPtr)i, sb);
      o.Add(sb.ToString());
    }
    return o;
  }
  public static int Select(IntPtr h, IntPtr combo, int idx) {
    int id = GetDlgCtrlID(combo);
    SendMessage(combo, 0x014E, (IntPtr)idx, IntPtr.Zero);            // CB_SETCURSEL
    foreach (int note in new int[] { 1, 9 }) {                       // CBN_SELCHANGE, CBN_SELENDOK
      IntPtr wp = (IntPtr)((note << 16) | (id & 0xFFFF));
      SendMessage(h, 0x0111, wp, combo);
    }
    return id;
  }
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, StringBuilder l);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$log = Join-Path $OutDir 'dsdiag-report.txt'
"# DsDiag report $(Get-Date -Format o) listOnly=$ListOnly index=$Index" | Set-Content -Encoding UTF8 $log
function L($m) { $m | Out-File -Append -Encoding UTF8 $log; Write-Output $m }

# MOTW-safe copy.
$safeDir = Join-Path $env:PUBLIC 'p0'
New-Item -ItemType Directory -Force $safeDir | Out-Null
$exeName = Split-Path $DsDiagExe -Leaf
$safeExe = Join-Path $safeDir $exeName
if (-not (Test-Path $safeExe) -or (Get-Item $DsDiagExe).LastWriteTime -gt (Get-Item $safeExe).LastWriteTime) {
  Copy-Item $DsDiagExe $safeDir -Force
  $core = Join-Path (Split-Path $DsDiagExe -Parent) 'DsCore.dll'
  if (Test-Path $core) { Copy-Item $core $safeDir -Force }
  Get-ChildItem $safeDir -Filter '*.exe' | Unblock-File
}

$p = Start-Process $safeExe -PassThru
for ($i = 0; $i -lt 25; $i++) { Start-Sleep -Milliseconds 400; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
$h = $p.MainWindowHandle
if ($h -eq 0) { throw 'DsDiag main window not found (MOTW still blocking?)' }
Start-Sleep -Seconds 2
L "pid=$($p.Id) hwnd=$h title=[$($p.MainWindowTitle)]"

$combos = [P0DsDiag]::Combos($h)
$devCombo = [IntPtr]::Zero; $best = -1
foreach ($c in $combos) {
  $its = [P0DsDiag]::Items($c)
  if ($its.Count -gt $best) { $best = $its.Count; $devCombo = $c }
}
if ($devCombo -eq [IntPtr]::Zero -or $best -le 0) { $p.Kill(); throw 'device ComboBox not found' }
$items = [P0DsDiag]::Items($devCombo)
L "device combo id=$([P0DsDiag]::GetDlgCtrlID($devCombo)) items=$($items.Count)"
for ($i = 0; $i -lt $items.Count; $i++) { L ("  [{0}] {1}" -f $i, $items[$i]) }

if (-not $ListOnly) {
  if ($Index -lt 0 -or $Index -ge $items.Count) { $p.Kill(); throw "-Index must be 0..$($items.Count - 1)" }
  $before = @([P0DsDiag]::Statics($h))
  [P0DsDiag]::Select($h, $devCombo, $Index) | Out-Null
  Start-Sleep -Milliseconds $SettleMs
  $p.Refresh()
  if ($p.HasExited) {
    L "--- [$Index] $($items[$Index]) ---"
    L 'DsDiag exited while opening this device (vendor collection it cannot parse)'
  } else {
    $after = @([P0DsDiag]::Statics($h))
    Start-Sleep -Milliseconds $SampleMs
    $later = @([P0DsDiag]::Statics($h))
    L "--- [$Index] $($items[$Index]) ---"
    L ("details: " + (($after | Where-Object { $_ -ne '-' }) -join ' | '))
    L ("before-selection: " + (($before | Where-Object { $_ -ne '-' }) -join ' | '))
    if (Compare-Object $after $later) {
      L ("live: CHANGED -> " + (($later | Where-Object { $_ -ne '-' }) -join ' | '))
    } else {
      L 'live: no change (listed but silent to DS)'
    }
  }
}

if (-not $p.HasExited) {
  $p.CloseMainWindow() | Out-Null
  Start-Sleep -Milliseconds 700
  if (-not $p.HasExited) { $p.Kill() }
}
L "report=$log"
