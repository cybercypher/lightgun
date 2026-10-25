<#
.SYNOPSIS
    Upload a local file to the G'AIM'E gun over its J3 serial console, with
    md5 verification on the gun.

.DESCRIPTION
    The gun console (J3, 115200 8N1) mangles very long single lines, so files
    are sent as base64 in small chunks: one `printf ... >> /tmp/x.b64` per line,
    then `base64 -d` on the gun. The result is md5-compared against the local
    file before the target is replaced, so a corrupt transfer is caught.

    Requires a root shell already listening on the console (the gun's boot
    shell is a root prompt; no login). Wiring: GND->GND, adapter TXD->board RX,
    adapter RXD->board TX, V3.3 unconnected. See tools/gun/README.md.

    Default behaviour (no -NoReplace): write <RemotePath>.new, verify it, then
    `mv` it over <RemotePath> and verify the final file as well.

.EXAMPLE
    # Deploy the corrected logs watchdog (see tools/gun/README.md)
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\Upload-GunFile.ps1 `
      -LocalPath logs\gunref\logs_watchdog.sh `
      -RemotePath /app/shell/logs_watchdog.sh

.EXAMPLE
    # Upload without replacing the target, just verify the staged .new file
    .\Upload-GunFile.ps1 -LocalPath .\foo.sh -RemotePath /tmp/foo.sh -NoReplace
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LocalPath,
    [Parameter(Mandatory = $true)][string]$RemotePath,
    [string]$Port = "COM4",
    [int]$Baud = 115200,
    [int]$ChunkSize = 64,
    [int]$ChunkGapMs = 120,
    [switch]$NoReplace,
    [switch]$KeepTemp
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $LocalPath -PathType Leaf)) {
    throw "local file not found: $LocalPath"
}
$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $LocalPath).ProviderPath)
$localMd5 = (Get-FileHash -LiteralPath $LocalPath -Algorithm MD5).Hash.ToLowerInvariant()
$b64 = [Convert]::ToBase64String($bytes)

Write-Host ("local:  {0}  ({1} bytes, md5 {2})" -f $LocalPath, $bytes.Length, $localMd5)
Write-Host ("remote: {0}  via {1} @ {2}" -f $RemotePath, $Port, $Baud)

$sp = New-Object System.IO.Ports.SerialPort($Port, $Baud, 'None', 8, 'One')
$sp.ReadTimeout = 4000
$sp.DtrEnable = $false
$sp.RtsEnable = $false
$sp.Open()

function Read-For([double]$secs) {
    $t = [DateTime]::UtcNow.AddSeconds($secs).Ticks
    $sb = New-Object System.Text.StringBuilder
    while ([DateTime]::UtcNow.Ticks -lt $t) {
        try {
            $n = $sp.BytesToRead
            if ($n -gt 0) {
                $buf = New-Object byte[] $n
                [void]$sp.Read($buf, 0, $n)
                [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf))
            } else { Start-Sleep -Milliseconds 20 }
        } catch { break }
    }
    $sb.ToString()
}

function Send-Line([string]$cmd) {
    $sp.Write($cmd + "`r`n")
}

try {
    # 1. require a live shell before touching anything
    $sp.DiscardInBuffer(); $sp.DiscardOutBuffer()
    Send-Line ""
    $hello = Read-For 2
    if (-not ($hello -match '#')) {
        throw "no shell prompt on $Port. Wiring/power? (send a newline by hand with j3_console.ps1 probe)"
    }
    Write-Host "[shell present]"

    # 2. stage the base64 payload on the gun (tmpfs)
    Send-Line "rm -f /tmp/gunfile.b64"
    Start-Sleep -Milliseconds 200
    $chunks = [Math]::Ceiling($b64.Length / $ChunkSize)
    $i = 0
    for ($off = 0; $off -lt $b64.Length; $off += $ChunkSize) {
        $piece = $b64.Substring($off, [Math]::Min($ChunkSize, $b64.Length - $off))
        Send-Line ("printf '%s' '{0}' >> /tmp/gunfile.b64" -f $piece)
        Start-Sleep -Milliseconds $ChunkGapMs
        $i++
        if (($i % 20) -eq 0) { Write-Host ("  sent {0}/{1} chunks" -f $i, $chunks) }
    }
    Write-Host ("[sent {0} chunks, {1} base64 chars]" -f $chunks, $b64.Length)

    # 3. decode to a staging file next to the target
    $stage = "$RemotePath.new"
    Send-Line ("base64 -d /tmp/gunfile.b64 > {0}; chmod +x {0}; md5sum {0}" -f $stage)
    $reply = Read-For 4
    Write-Host $reply
    if (-not ($reply -match [regex]::Escape($localMd5))) {
        throw "staged file md5 does not match local file - transfer corrupt, target NOT replaced"
    }
    Write-Host "[staged copy verified]"

    # 4. replace (optional) and verify the final target
    if (-not $NoReplace) {
        Send-Line ("mv -f {0} {1}; chmod +x {1}; md5sum {1}" -f $stage, $RemotePath)
        $reply2 = Read-For 4
        Write-Host $reply2
        if (-not ($reply2 -match [regex]::Escape($localMd5))) {
            throw "final file md5 does not match after replace"
        }
        Write-Host "[replaced and verified]"
    }

    if (-not $KeepTemp) { Send-Line "rm -f /tmp/gunfile.b64"; Start-Sleep -Milliseconds 200; [void](Read-For 0.5) }

    Write-Host ""
    Write-Host "OK  $RemotePath  md5 $localMd5"
}
finally {
    try { $sp.Close() } catch { }
}
