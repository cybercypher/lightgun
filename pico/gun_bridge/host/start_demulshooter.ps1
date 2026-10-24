# Start DemulShooter with the bridge paths rewritten first.
#
# Example:
#   powershell -ExecutionPolicy Bypass -File start_demulshooter.ps1 -Target model2 -Rom vcop2
#
# The path rewrite is idempotent; DS reads config.ini only at startup, which is
# why this launcher rewrites first and then starts DS.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$Rom,
    [string]$DsDir = 'C:\games\demulshooter',
    [string]$ExtraArgs = ''
)

$ErrorActionPreference = 'Stop'

$config = Join-Path $DsDir 'config.ini'
$exe = Join-Path $DsDir 'DemulShooter.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "DemulShooter.exe not found in $DsDir"
}

& (Join-Path $PSScriptRoot 'fix_demulshooter_paths.ps1') -ConfigPath $config

$arguments = @("-target=$Target", "-rom=$Rom")
if ($ExtraArgs) {
    $arguments += $ExtraArgs -split ' '
}

Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $DsDir
Write-Host "Started DemulShooter: -target=$Target -rom=$Rom"
