# Install the G'AIM'E white reference border into TeknoParrot's ELF loader.
#
# ReShade hooks OpenGL through a proxy named opengl32.dll that must sit next to
# the process exe. For ElfLdr2 games (Terminator Salvation and the other Raw
# Thrills / Linux titles) that is BudgieLoader.exe, so this script installs into
# C:\games\teknoparrot\ElfLdr2 and every ElfLdr2 game gets the border.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Install-ReshadeBorder.ps1
#   ... -BorderPx 24        change the thickness (keep ScreenBorder.Pixels equal)
#   ... -Uninstall          remove the proxy and the config, leave nothing behind
#
# The 32-bit ReShade binary is taken from an existing ReShade install by
# default (ReShade's DLL is a universal proxy: the same file exports the D3D11,
# Vulkan and wgl entry points, so a d3d11 proxy copy works as opengl32.dll).
param(
    [string]$InstallDir = "C:\games\teknoparrot\ElfLdr2",
    [string]$ReShadeDll = "",
    [int]$BorderPx = 32,
    [switch]$Uninstall
)
$ErrorActionPreference = "Stop"
$here = Join-Path $PSScriptRoot "reshade"

function Get-PeMachine([string]$path) {
    $b = [System.IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    return [BitConverter]::ToUInt16($b, $pe + 4)
}

$proxy = Join-Path $InstallDir "opengl32.dll"

if ($Uninstall) {
    if ((Test-Path $proxy) -and
        ((Get-Item $proxy).VersionInfo.ProductName -match "ReShade")) {
        Remove-Item -LiteralPath $proxy -Force
        Write-Host "removed $proxy"
    } elseif (Test-Path $proxy) {
        Write-Warning "$proxy is not ReShade - left alone"
    }
    foreach ($f in "ReShade.ini", "ReShadePreset.ini", "ReShade.log") {
        $p = Join-Path $InstallDir $f
        if (Test-Path $p) { Remove-Item -LiteralPath $p -Force; Write-Host "removed $p" }
    }
    $shaders = Join-Path $InstallDir "reshade-shaders"
    if ((Test-Path (Join-Path $shaders "Shaders\GaimeBorder.fx"))) {
        Remove-Item -LiteralPath $shaders -Recurse -Force
        Write-Host "removed $shaders"
    }
    return
}

if (-not (Test-Path (Join-Path $InstallDir "BudgieLoader.exe"))) {
    throw "$InstallDir does not contain BudgieLoader.exe - pass -InstallDir"
}

if ($ReShadeDll -eq "") {
    $candidates = @(
        "D:\Games\X-Men Origins - Wolverine(TM)\Binaries\D3D11.DLL",
        "D:\Games\X-Men Origins - Wolverine(TM)\Binaries\dxgi.dll",
        "C:\games\teknoparrot\ElfLdr2\ReShade32.dll"
    )
    $ReShadeDll = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ReShadeDll -or -not (Test-Path $ReShadeDll)) {
    throw "No ReShade DLL found. Pass -ReShadeDll <path to a 32-bit ReShade proxy DLL>."
}
if ((Get-PeMachine $ReShadeDll) -ne 0x14c) {
    throw "$ReShadeDll is not 32-bit; BudgieLoader.exe is 32-bit. Use ReShade32, not ReShade64."
}
if ((Test-Path $proxy) -and
    -not ((Get-Item $proxy).VersionInfo.ProductName -match "ReShade")) {
    throw "$proxy already exists and is not ReShade - refusing to overwrite"
}

# Older ReShade copies carry a d3d11/dxgi name in their version resource; that
# is cosmetic, the binary is the same universal proxy.
Copy-Item -LiteralPath $ReShadeDll -Destination $proxy -Force
Write-Host ("installed {0} ({1} bytes) from {2}" -f $proxy, (Get-Item $proxy).Length, $ReShadeDll)

$shaderDir = Join-Path $InstallDir "reshade-shaders\Shaders"
New-Item -ItemType Directory -Force -Path $shaderDir | Out-Null
Copy-Item -LiteralPath (Join-Path $here "GaimeBorder.fx") $shaderDir -Force
Copy-Item -LiteralPath (Join-Path $here "ReShade.fxh") $shaderDir -Force
Copy-Item -LiteralPath (Join-Path $here "ReShade.ini") $InstallDir -Force

# The preset carries the thickness; rewrite it so -BorderPx is the one number.
$preset = (Get-Content -Raw (Join-Path $here "ReShadePreset.ini")) -replace
    "(?m)^BORDER_PX=.*$", ("BORDER_PX={0}.000000" -f $BorderPx)
[System.IO.File]::WriteAllText((Join-Path $InstallDir "ReShadePreset.ini"), $preset)
Remove-Item -LiteralPath (Join-Path $InstallDir "ReShade.log") -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Border: $BorderPx px on every edge, applied on launch (no Home key needed)."
Write-Host "Next: start a game from TeknoParrot, then check:"
Write-Host "  $InstallDir\ReShade.log   -> 'Initializing OpenGL' + 'Successfully compiled ... GaimeBorder.fx'"
Write-Host "Revert: tools\Install-ReshadeBorder.ps1 -Uninstall"
