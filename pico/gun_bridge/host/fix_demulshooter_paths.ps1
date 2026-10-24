# Rewrite DemulShooter P1/P2 RawInput paths for the G'AIM'E Pico bridge.
#
# DemulShooter matches RawInput devices by exact device-interface path and only
# enumerates at startup. This script asks Windows for the exact RawInput device
# names (the same strings DemulShooter sees), picks the bridge's two HID
# interfaces (MI_00 = player 1, MI_01 = player 2), and rewrites config.ini.
# Run it while DemulShooter is closed (it reads the file once at startup).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File fix_demulshooter_paths.ps1
#   powershell -ExecutionPolicy Bypass -File fix_demulshooter_paths.ps1 -DryRun
#   powershell -ExecutionPolicy Bypass -File fix_demulshooter_paths.ps1 -ConfigPath D:\ds\config.ini

[CmdletBinding()]
param(
    [string]$ConfigPath = 'C:\games\demulshooter\config.ini',
    [int]$VendorId = 0x1209,
    [int]$ProductId = 0x0A11,
    [int]$WaitSeconds = 5,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "config.ini not found: $ConfigPath"
}

if (Get-Process -Name DemulShooter -ErrorAction SilentlyContinue) {
    Write-Warning 'DemulShooter is running; it will not pick up the change until restarted.'
}

# --- RawInput device-name enumeration --------------------------------------
if (-not ('RawInput' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class RawInput {
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(
        [In, Out] RAWINPUTDEVICELIST[] pRawInputDeviceList,
        ref uint puiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
}
"@
}

$RIDI_DEVICENAME = 0x20000007
$structSize = [Runtime.InteropServices.Marshal]::SizeOf([type][RawInput+RAWINPUTDEVICELIST])
$vidpid = 'VID_{0:X4}&PID_{1:X4}' -f $VendorId, $ProductId
$pattern = [regex]::Escape($vidpid) + '&MI_([0-9A-Fa-f]{2})'

function Get-BridgePaths {
    $count = 0
    if ([RawInput]::GetRawInputDeviceList($null, [ref]$count, $structSize) -ne 0) {
        throw "GetRawInputDeviceList(size) failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    if ($count -eq 0) { return @{} }

    $devices = New-Object 'RawInput+RAWINPUTDEVICELIST[]' $count
    if ([RawInput]::GetRawInputDeviceList($devices, [ref]$count, $structSize) -eq [uint32]::MaxValue) {
        throw "GetRawInputDeviceList(list) failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }

    $found = @{}
    foreach ($device in $devices) {
        # No dwType filter: a HID mouse is reported as RIM_TYPEMOUSE (0), a HID
        # gamepad as RIM_TYPEHID (2). Only the device name matters here.

        $size = 0
        [RawInput]::GetRawInputDeviceInfo($device.hDevice, $RIDI_DEVICENAME, [IntPtr]::Zero, [ref]$size) | Out-Null
        if ($size -le 1) { continue }

        $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal([int]$size * 2)
        try {
            [RawInput]::GetRawInputDeviceInfo($device.hDevice, $RIDI_DEVICENAME, $buffer, [ref]$size) | Out-Null
            $name = [Runtime.InteropServices.Marshal]::PtrToStringUni($buffer)
        } finally {
            [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
        }

        if ($name -match $pattern) {
            $found[[Convert]::ToInt32($Matches[1], 16)] = $name
        }
    }
    return $found
}

# --- wait for the bridge ---------------------------------------------------
$paths = @{}
$deadline = (Get-Date).AddSeconds($WaitSeconds)
do {
    $paths = Get-BridgePaths
    if (-not $paths.ContainsKey(0) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
} while (-not $paths.ContainsKey(0) -and (Get-Date) -lt $deadline)

if (-not $paths.ContainsKey(0)) {
    throw "No $vidpid MI_00 RawInput device found. Is the Pico plugged in and enumerated?"
}

$paths.GetEnumerator() | Sort-Object Key | ForEach-Object {
    Write-Host ("MI_{0:D2} -> {1}" -f $_.Key, $_.Value)
}

# --- rewrite config.ini ----------------------------------------------------
$lines = [System.IO.File]::ReadAllLines($ConfigPath)
$out = New-Object System.Collections.Generic.List[string]
$changed = 0

foreach ($line in $lines) {
    if ($line -match '^P1DeviceName\s*=') {
        $out.Add("P1DeviceName = $($paths[0])"); $changed++; continue
    }
    if ($line -match '^P2DeviceName\s*=') {
        if ($paths.ContainsKey(1)) {
            $out.Add("P2DeviceName = $($paths[1])"); $changed++
        } else {
            Write-Warning 'No MI_01 interface found; leaving P2DeviceName unchanged.'
            $out.Add($line)
        }
        continue
    }
    if ($line -match '^P1Mode\s*=') { $out.Add('P1Mode = RAWINPUT'); continue }
    if ($line -match '^P2Mode\s*=' -and $paths.ContainsKey(1)) {
        $out.Add('P2Mode = RAWINPUT'); continue
    }
    $out.Add($line)
}

if ($changed -eq 0) {
    Write-Warning 'No P1DeviceName/P2DeviceName lines matched; config format changed?'
}

if ($DryRun) {
    Write-Host "`n--- would write ($changed fields) ---"
    $out | Where-Object { $_ -match '^P[12](DeviceName|Mode)\s*=' }
    return
}

$backup = "$ConfigPath.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item -LiteralPath $ConfigPath -Destination $backup
[System.IO.File]::WriteAllLines($ConfigPath, $out, [System.Text.Encoding]::ASCII)

Write-Host "`nBacked up to: $backup"
Write-Host "Updated $changed field(s) in: $ConfigPath"
