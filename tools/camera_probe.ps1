# camera_probe.ps1
#
# Hardware experiment for the G'AIM'E light gun's UVC camera:
# does it stream the LIVE sensor view (what the camera sees of the screen),
# or only a fixed TEST PATTERN?
#
# Method (two-color discrimination):
#   Phase MAGENTA: start ffmpeg + HID digitizer probe, 2 s lead-in (desktop),
#                  12 s fullscreen magenta form, 1 s tail.
#   Gap 4 s (desktop visible).
#   Phase CYAN:    identical, fullscreen cyan form.
#   Then extract 5 frames from the middle of each capture, sample every 8th
#   pixel in x and y, and compare mean RGB between phases.
#
#   LIVE sensor view  -> magenta and cyan phases differ on at least one channel
#                        by > 40, and each phase is stable (channel stdev < 25).
#   TEST pattern      -> both phases are near-identical (all channel deltas < 15).
#   Anything else     -> INCONCLUSIVE; inspect the extracted PNGs by eye.
#
# A HUMAN MUST AIM THE GUN AT THE PRIMARY SCREEN while this runs.
# Run with Windows PowerShell 5.1 (STA), e.g.:
#   powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File \\wsl.localhost\Ubuntu-24.04\home\USER\lightgun\tools\camera_probe.ps1
#
# Outputs: <repo>\logs\camera-probe\capture-{magenta,cyan}.mp4
#          <repo>\logs\camera-probe\digitizer-{magenta,cyan}.txt
#          <repo>\logs\camera-probe\frames\frame-{magenta,cyan}-*.png

[CmdletBinding()]
param(
    [int]$DurationSec = 12,
    [string]$Size = "640x360",
    [int]$Fps = 30,
    [string]$Device = "Dashine UVC",
    [string]$FfmpegPath = "C:\ProgramData\chocolatey\bin\ffmpeg.exe"
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Fixed experiment timing (per design).
$LeadInSec = 2
$TailSec = 1
$GapSec = 4
$SampleStep = 8

$script:Procs = New-Object System.Collections.ArrayList
$script:ActiveForm = $null
$script:ActiveTimer = $null
$script:FormPhase = ''
$script:FormSeconds = 0
$script:CursorHidden = $false

function Write-Log {
    param([string]$Message)
    $ts = Get-Date -Format 'HH:mm:ss.fff'
    Write-Host ("[{0}] {1}" -f $ts, $Message)
}

function Get-Quoted {
    param([string]$Value)
    return ('"' + $Value + '"')
}

function Restore-Cursor {
    if ($script:CursorHidden) {
        try { [System.Windows.Forms.Cursor]::Show() } catch { }
        $script:CursorHidden = $false
    }
}

function Stop-ProcessSafe {
    param($Proc)
    if ($null -eq $Proc) { return }
    try {
        if (-not $Proc.HasExited) {
            # /T kills any children too (e.g. a python launcher shim).
            & taskkill.exe /PID $Proc.Id /T /F 2>$null | Out-Null
            [void]$Proc.WaitForExit(5000)
            if (-not $Proc.HasExited) {
                Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue
                [void]$Proc.WaitForExit(2000)
            }
        }
    } catch {
        try { Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

function Stop-AllBg {
    if ($null -eq $script:Procs) { return }
    foreach ($p in @($script:Procs)) { Stop-ProcessSafe $p }
    $script:Procs.Clear()
}

function New-FfmpegArgs {
    param(
        [Parameter(Mandatory = $true)][string]$OutFile,
        [Parameter(Mandatory = $true)][string]$CapSize,
        [Parameter(Mandatory = $true)][int]$CapFps,
        [Parameter(Mandatory = $true)][int]$Seconds,
        [string]$LogLevel = 'warning'
    )
    $dev = $Device.Trim()
    if ($dev -match '^\s*video\s*=\s*(.*)$') { $dev = $Matches[1].Trim() }
    $dev = $dev.Trim('"')
    $devArg = Get-Quoted ('video=' + $dev)
    $outArg = Get-Quoted $OutFile

    $parts = @(
        '-hide_banner',
        '-loglevel', $LogLevel,
        '-nostdin',
        '-y',
        '-f', 'dshow',
        '-rtbufsize', '256M',
        '-video_size', $CapSize,
        '-framerate', [string]$CapFps,
        '-pixel_format', 'yuyv422',
        '-i', $devArg,
        '-t', [string]$Seconds,
        '-c:v', 'libx264',
        '-preset', 'ultrafast',
        '-crf', '18',
        '-pix_fmt', 'yuv420p',
        '-an',
        $outArg
    )
    return ($parts -join ' ')
}

function Invoke-Preflight {
    param(
        [Parameter(Mandatory = $true)][string]$TestSize,
        [Parameter(Mandatory = $true)][int]$TestFps,
        [Parameter(Mandatory = $true)][string]$Tag
    )
    $out = Join-Path $OutDir ("_preflight-{0}.mp4" -f $Tag)
    $outLog = Join-Path $OutDir ("_preflight-{0}.out.log" -f $Tag)
    $errLog = Join-Path $OutDir ("_preflight-{0}.err.log" -f $Tag)
    foreach ($f in @($out, $outLog, $errLog)) {
        Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }

    Write-Log ("preflight {0}: trying {1} @ {2} fps for 2 s" -f $Tag, $TestSize, $TestFps)
    $preArgs = New-FfmpegArgs -OutFile $out -CapSize $TestSize -CapFps $TestFps -Seconds 2 -LogLevel 'error'
    $p = $null
    $code = $null
    $ok = $false
    try {
        $p = Start-Process -FilePath $FfmpegPath -ArgumentList $preArgs -WorkingDirectory $WorkDir -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(30000)) {
            Write-Log ("preflight {0}: timed out after 30 s" -f $Tag)
            Stop-ProcessSafe $p
        } else {
            $p.Refresh()
            $code = $p.ExitCode
            $len = 0
            if (Test-Path -LiteralPath $out -PathType Leaf) {
                $len = (Get-Item -LiteralPath $out).Length
            }
            $ok = ($code -eq 0) -and ($len -gt 500)
            Write-Log ("preflight {0}: exit={1} bytes={2}" -f $Tag, $code, $len)
        }
    } finally {
        Stop-ProcessSafe $p
    }

    if (-not $ok -and (Test-Path -LiteralPath $errLog -PathType Leaf)) {
        $errLines = @(Get-Content -LiteralPath $errLog -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_.Trim() })
        foreach ($line in ($errLines | Select-Object -Last 3)) {
            Write-Log ("  ffmpeg: " + $line)
        }
    }
    return [pscustomobject]@{ Ok = $ok; ExitCode = $code; OutFile = $out; ErrLog = $errLog }
}

function Show-ColorForm {
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Color]$Color,
        [Parameter(Mandatory = $true)][int]$Seconds,
        [Parameter(Mandatory = $true)][string]$Phase
    )
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "camera-probe $Phase"
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $form.TopMost = $true
    $form.ShowInTaskbar = $false
    $form.ControlBox = $false
    $form.MinimizeBox = $false
    $form.MaximizeBox = $false
    $form.KeyPreview = $true
    $form.BackColor = $Color

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = [int]([Math]::Max(250, $Seconds * 1000))

    $script:ActiveForm = $form
    $script:ActiveTimer = $timer
    $script:FormPhase = $Phase
    $script:FormSeconds = $Seconds

    $timer.Add_Tick({
        $t = $script:ActiveTimer
        if ($null -ne $t) { try { $t.Stop() } catch { } }
        $f = $script:ActiveForm
        if ($null -ne $f) { try { $f.Close() } catch { } }
    })

    $form.Add_Shown({
        try { [System.Windows.Forms.Cursor]::Hide() } catch { }
        $script:CursorHidden = $true
        $f = $script:ActiveForm
        if ($null -ne $f) { try { $f.Activate() } catch { } }
        $t = $script:ActiveTimer
        if ($null -ne $t) { $t.Start() }
        Write-Log ("PHASE {0}: fullscreen form visible for {1}s" -f $script:FormPhase, $script:FormSeconds)
    })

    $form.Add_KeyDown({
        param($sender, $e)
        if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Escape) {
            try { $sender.Close() } catch { }
        }
    })

    try {
        [System.Windows.Forms.Application]::Run($form)
    } finally {
        try { $timer.Stop() } catch { }
        try { $timer.Dispose() } catch { }
        Restore-Cursor
        if ($script:ActiveForm -eq $form) { $script:ActiveForm = $null }
        $script:ActiveTimer = $null
        $script:FormPhase = ''
        $script:FormSeconds = 0
        try { $form.Dispose() } catch { }
    }
}

function Invoke-Phase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][System.Drawing.Color]$Color,
        [Parameter(Mandatory = $true)][string]$CapSize,
        [Parameter(Mandatory = $true)][int]$CapFps
    )
    $total = $LeadInSec + $DurationSec + $TailSec
    $cap = Join-Path $OutDir ("capture-{0}.mp4" -f $Name)
    $dig = Join-Path $OutDir ("digitizer-{0}.txt" -f $Name)
    $digErr = Join-Path $OutDir ("digitizer-{0}.err.txt" -f $Name)
    $ffOut = Join-Path $OutDir ("ffmpeg-{0}.out.log" -f $Name)
    $ffErr = Join-Path $OutDir ("ffmpeg-{0}.err.log" -f $Name)
    foreach ($f in @($cap, $dig, $digErr, $ffOut, $ffErr)) {
        Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }

    $ff = $null
    $py = $null
    $ffCode = $null
    $ffLen = 0
    $probeStarted = $false

    Write-Log ("PHASE {0}: START (screen RGB {1},{2},{3}; {4}s = {5} lead-in + {6} form + {7} tail)" -f $Name, $Color.R, $Color.G, $Color.B, $total, $LeadInSec, $DurationSec, $TailSec)
    try {
        $ffArgs = New-FfmpegArgs -OutFile $cap -CapSize $CapSize -CapFps $CapFps -Seconds $total -LogLevel 'warning'
        $ff = Start-Process -FilePath $FfmpegPath -ArgumentList $ffArgs -WorkingDirectory $WorkDir -RedirectStandardOutput $ffOut -RedirectStandardError $ffErr -PassThru -WindowStyle Hidden
        [void]$script:Procs.Add($ff)
        Write-Log ("capture started (pid {0}) -> {1}" -f $ff.Id, $cap)

        if (Test-Path -LiteralPath $ProbePy -PathType Leaf) {
            $probeSeconds = [int]$total
            $pyArgs = '-u "{0}" log --seconds {1}' -f $ProbePy, $probeSeconds
            try {
                $py = Start-Process -FilePath 'python' -ArgumentList $pyArgs -WorkingDirectory $WorkDir -RedirectStandardOutput $dig -RedirectStandardError $digErr -PassThru -WindowStyle Hidden
                [void]$script:Procs.Add($py)
                $probeStarted = $true
                Write-Log ("digitizer started (pid {0}) -> {1}" -f $py.Id, $dig)
            } catch {
                Write-Log ("WARN: digitizer probe failed to start: {0}" -f $_.Exception.Message)
                Write-Log "WARN: continuing without HID tracking proof"
            }
        } else {
            Write-Log ("WARN: probe not found: {0} (continuing without HID proof)" -f $ProbePy)
        }

        Write-Log ("lead-in {0}s: desktop visible, capture rolling" -f $LeadInSec)
        Start-Sleep -Seconds $LeadInSec

        Show-ColorForm -Color $Color -Seconds $DurationSec -Phase $Name

        Write-Log ("PHASE {0}: form closed; tail {1}s" -f $Name, $TailSec)
        Start-Sleep -Seconds $TailSec

        if ($null -ne $ff) {
            if (-not $ff.WaitForExit(20000)) {
                Write-Log ("WARN: capture did not exit within 20 s; stopping pid {0} (file may be unfinalized)" -f $ff.Id)
                Stop-ProcessSafe $ff
            } else {
                $ff.Refresh()
                $ffCode = $ff.ExitCode
            }
        }

        if (Test-Path -LiteralPath $cap -PathType Leaf) {
            $ffLen = (Get-Item -LiteralPath $cap).Length
        }
        Write-Log ("capture ended: exit={0} bytes={1}" -f $ffCode, $ffLen)
        if ($ffLen -le 0) {
            Write-Log ("WARN: capture file is missing/empty: {0}" -f $cap)
        }
        if (($null -ne $ffCode) -and ($ffCode -ne 0)) {
            Write-Log ("WARN: ffmpeg exit code {0}; see {1}" -f $ffCode, $ffErr)
        }

        if ($null -ne $py) {
            if (-not $py.WaitForExit(2000)) {
                Write-Log ("digitizer still running after capture; stopping pid {0}" -f $py.Id)
                Stop-ProcessSafe $py
            } else {
                $py.Refresh()
                Write-Log ("digitizer exited (code {0})" -f $py.ExitCode)
            }
        }
    } finally {
        if (($null -ne $ff) -and (-not $ff.HasExited)) { Stop-ProcessSafe $ff }
        if (($null -ne $py) -and (-not $py.HasExited)) { Stop-ProcessSafe $py }
        if ($null -ne $script:ActiveForm) {
            try { $script:ActiveForm.Close() } catch { }
            try { $script:ActiveForm.Dispose() } catch { }
            $script:ActiveForm = $null
        }
        Restore-Cursor
        Write-Log ("PHASE {0}: END" -f $Name)
    }

    return [pscustomobject]@{
        Name = $Name
        Capture = $cap
        Digitizer = $dig
        DigitizerErr = $digErr
        FfmpegExit = $ffCode
        FfmpegErr = $ffErr
        Bytes = $ffLen
        ProbeStarted = $probeStarted
    }
}

function Get-FrameStats {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bmp = $null
    try {
        $bmp = [System.Drawing.Bitmap]::FromFile($Path)
        $sumR = [long]0
        $sumG = [long]0
        $sumB = [long]0
        $n = 0
        for ($y = 0; $y -lt $bmp.Height; $y += $SampleStep) {
            for ($x = 0; $x -lt $bmp.Width; $x += $SampleStep) {
                $c = $bmp.GetPixel($x, $y)
                $sumR += $c.R
                $sumG += $c.G
                $sumB += $c.B
                $n++
            }
        }
        if ($n -eq 0) { throw "no pixels sampled" }
        return [pscustomobject]@{
            Path = $Path
            R = ([double]$sumR / [double]$n)
            G = ([double]$sumG / [double]$n)
            B = ([double]$sumB / [double]$n)
            Samples = $n
        }
    } finally {
        if ($null -ne $bmp) { $bmp.Dispose() }
    }
}

function Get-Stdev {
    param([double[]]$Values, [double]$Mean)
    if (($null -eq $Values) -or ($Values.Count -lt 2)) { return [double]::NaN }
    $sum = 0.0
    foreach ($v in $Values) {
        $d = $v - $Mean
        $sum += ($d * $d)
    }
    return [Math]::Sqrt($sum / $Values.Count)
}

function Invoke-PhaseAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Capture,
        [Parameter(Mandatory = $true)][int[]]$Offsets
    )
    $result = [pscustomobject]@{
        Name = $Name
        Capture = $Capture
        Frames = @()
        MeanR = [double]::NaN
        MeanG = [double]::NaN
        MeanB = [double]::NaN
        SdR = [double]::NaN
        SdG = [double]::NaN
        SdB = [double]::NaN
    }

    if (-not (Test-Path -LiteralPath $Capture -PathType Leaf)) {
        Write-Log ("WARN: cannot analyze {0}; capture missing" -f $Name)
        return $result
    }

    $stats = New-Object System.Collections.ArrayList
    foreach ($off in $Offsets) {
        $t = $LeadInSec + $off
        $png = Join-Path $FramesDir ("frame-{0}-{1:00}.png" -f $Name, $off)
        $err = Join-Path $OutDir ("frame-{0}-{1:00}.err.log" -f $Name, $off)
        Remove-Item -LiteralPath $png -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $err -Force -ErrorAction SilentlyContinue

        Write-Log ("extract {0} frame at t={1}s -> {2}" -f $Name, $t, (Split-Path -Leaf $png))
        & $FfmpegPath -hide_banner -loglevel error -nostdin -y -i $Capture -ss $t -frames:v 1 $png 2> $err | Out-Null
        $code = $LASTEXITCODE

        $len = 0
        if (Test-Path -LiteralPath $png -PathType Leaf) {
            $len = (Get-Item -LiteralPath $png).Length
        }
        if (($code -ne 0) -or ($len -le 0)) {
            Write-Log ("WARN: frame extraction failed for {0} t={1}s (exit {2})" -f $Name, $t, $code)
            continue
        }

        try {
            $s = Get-FrameStats -Path $png
            [void]$stats.Add($s)
        } catch {
            Write-Log ("WARN: could not sample {0}: {1}" -f $png, $_.Exception.Message)
        }
    }

    $result.Frames = @($stats)
    if ($stats.Count -gt 0) {
        $rVals = [double[]]@($stats | ForEach-Object { $_.R })
        $gVals = [double[]]@($stats | ForEach-Object { $_.G })
        $bVals = [double[]]@($stats | ForEach-Object { $_.B })

        $result.MeanR = ($rVals | Measure-Object -Average).Average
        $result.MeanG = ($gVals | Measure-Object -Average).Average
        $result.MeanB = ($bVals | Measure-Object -Average).Average

        $result.SdR = Get-Stdev -Values $rVals -Mean $result.MeanR
        $result.SdG = Get-Stdev -Values $gVals -Mean $result.MeanG
        $result.SdB = Get-Stdev -Values $bVals -Mean $result.MeanB
    }
    return $result
}

function Show-CaptureInfo {
    param([Parameter(Mandatory = $true)]$Phase)
    Write-Host ("--- capture {0} ---" -f $Phase.Name)
    if (-not (Test-Path -LiteralPath $Phase.Capture -PathType Leaf)) {
        Write-Host ("  MISSING: {0}" -f $Phase.Capture)
        return
    }
    $item = Get-Item -LiteralPath $Phase.Capture
    Write-Host ("  path: {0}" -f $item.FullName)
    Write-Host ("  size: {0:N0} bytes" -f $item.Length)

    if ($null -eq $FfprobePath) {
        Write-Host "  ffprobe not found next to ffmpeg; frame count/duration skipped"
        return
    }

    $err = Join-Path $OutDir ("ffprobe-{0}.err.log" -f $Phase.Name)
    $out = & $FfprobePath -v error -select_streams v:0 -count_frames -show_entries 'stream=width,height,avg_frame_rate,nb_read_frames:format=duration' -of 'default=noprint_wrappers=1' $Phase.Capture 2> $err
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host ("  ffprobe exit {0}; see {1}" -f $code, $err)
    }
    foreach ($line in @($out)) {
        Write-Host ("  " + $line)
    }
}

function Show-DigitizerSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ErrPath,
        [Parameter(Mandatory = $true)][int]$Seconds
    )
    Write-Host ("--- digitizer {0} ---" -f $Name)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Host ("  MISSING: {0}" -f $Path)
        Write-Host "  (python/hidapi probe produced no output; the video verdict stands alone)"
        return
    }

    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8 -ErrorAction SilentlyContinue)
    $count = $lines.Count
    $hz = 0.0
    if ($Seconds -gt 0) { $hz = [double]$count / [double]$Seconds }

    Write-Host ("  lines: {0}  (~{1:N1} reports/s over {2}s; expect ~265 Hz while tracking)" -f $count, $hz, $Seconds)
    if ($count -eq 0) {
        Write-Host "  no digitizer lines; see probe stderr below"
    } else {
        Write-Host "  first 3:"
        foreach ($l in ($lines | Select-Object -First 3)) {
            Write-Host ("    | " + $l)
        }
        Write-Host "  last 3:"
        foreach ($l in ($lines | Select-Object -Last 3)) {
            Write-Host ("    | " + $l)
        }
    }

    if (Test-Path -LiteralPath $ErrPath -PathType Leaf) {
        $errLines = @(Get-Content -LiteralPath $ErrPath -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_.Trim() })
        if ($errLines.Count -gt 0) {
            Write-Host "  probe stderr:"
            foreach ($l in ($errLines | Select-Object -Last 2)) {
                Write-Host ("    ! " + $l)
            }
        }
    }

    if ($count -lt 10) {
        Write-Host ("  WARN: only {0} digitizer lines; tracking proof is weak/absent" -f $count)
    }
}

function Show-PhaseColor {
    param([Parameter(Mandatory = $true)]$A)
    Write-Host ("--- color {0} ---" -f $A.Name)
    if ($A.Frames.Count -eq 0) {
        Write-Host "  no frames analyzed"
        return
    }
    foreach ($f in $A.Frames) {
        Write-Host ("  {0}: R={1,7:N2} G={2,7:N2} B={3,7:N2}  samples={4}" -f (Split-Path -Leaf $f.Path), $f.R, $f.G, $f.B, $f.Samples)
    }
    Write-Host ("  mean-of-means: R={0,7:N2} G={1,7:N2} B={2,7:N2}" -f $A.MeanR, $A.MeanG, $A.MeanB)
    Write-Host ("  stdev:         R={0,7:N2} G={1,7:N2} B={2,7:N2}" -f $A.SdR, $A.SdG, $A.SdB)
}

# ---------------------------------------------------------------------------
# Validate parameters and resolve paths (all absolute; CWD is irrelevant).
# ---------------------------------------------------------------------------

if ($DurationSec -lt 1) { throw "-DurationSec must be >= 1" }
if ($Fps -lt 1) { throw "-Fps must be >= 1" }
if ($Size -notmatch '^\d+x\d+$') { throw "-Size must look like 640x360" }
if ([string]::IsNullOrWhiteSpace($Device)) { throw "-Device must not be empty" }

if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne [System.Threading.ApartmentState]::STA) {
    throw "This script needs an STA PowerShell host. Use: powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File <path>"
}

if (Test-Path -LiteralPath $FfmpegPath -PathType Leaf) {
    $FfmpegPath = (Resolve-Path -LiteralPath $FfmpegPath).ProviderPath
} else {
    $ffCmd = Get-Command -Name $FfmpegPath -ErrorAction SilentlyContinue
    if ($null -eq $ffCmd) { throw "ffmpeg not found: $FfmpegPath" }
    $FfmpegPath = $ffCmd.Source
}

if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    throw "PSScriptRoot is empty; save this as a .ps1 file and run it with -File"
}
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$OutDir = Join-Path $RepoRoot 'logs\camera-probe'
$FramesDir = Join-Path $OutDir 'frames'
$ProbePy = Join-Path $RepoRoot 'tools\gun_probe_win.py'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $FramesDir | Out-Null

$WorkDir = $env:TEMP
if ([string]::IsNullOrWhiteSpace($WorkDir) -or -not (Test-Path -LiteralPath $WorkDir -PathType Container)) {
    $WorkDir = $env:SystemRoot
}

$FfprobePath = Join-Path (Split-Path -Parent $FfmpegPath) 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $FfprobePath -PathType Leaf)) {
    $FfprobePath = $null
}

# Re-run safety: remove only this script's previous outputs.
$stale = @(
    'capture-magenta.mp4', 'capture-cyan.mp4',
    'digitizer-magenta.txt', 'digitizer-magenta.err.txt',
    'digitizer-cyan.txt', 'digitizer-cyan.err.txt',
    'ffmpeg-magenta.out.log', 'ffmpeg-magenta.err.log',
    'ffmpeg-cyan.out.log', 'ffmpeg-cyan.err.log',
    'ffprobe-magenta.err.log', 'ffprobe-cyan.err.log'
)
foreach ($name in $stale) {
    Remove-Item -LiteralPath (Join-Path $OutDir $name) -Force -ErrorAction SilentlyContinue
}
foreach ($pattern in @('frame-*.err.log', '_preflight-*')) {
    Get-ChildItem -LiteralPath $OutDir -Filter $pattern -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $FramesDir -Filter 'frame-*.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# Experiment.
# ---------------------------------------------------------------------------

try {
    Write-Host "============================================================"
    Write-Host "camera_probe.ps1 - live sensor view vs fixed test pattern"
    Write-Host "============================================================"
    Write-Log ("repo root:  {0}" -f $RepoRoot)
    Write-Log ("output dir: {0}" -f $OutDir)
    Write-Log ("ffmpeg:     {0}" -f $FfmpegPath)
    Write-Log ("device:     '{0}'  requested {1} @ {2} fps" -f $Device, $Size, $Fps)
    if ($null -eq $FfprobePath) {
        Write-Log "WARN: ffprobe.exe not found next to ffmpeg; frame-count/duration will be skipped"
    }
    Write-Log "AIM THE GUN AT THE PRIMARY SCREEN and keep it aimed during BOTH color phases."
    Write-Log "Move the gun slowly during each phase so the HID digitizer stream shows real coordinates."

    # Preflight: confirm the camera opens and writes a real file before spending
    # 30 s on the experiment. Fall back to 320x240 @ 30 per the verified formats.
    $capSize = $Size
    $capFps = $Fps
    $pf = Invoke-Preflight -TestSize $capSize -TestFps $capFps -Tag 'primary'
    if (-not $pf.Ok) {
        Write-Log ("WARN: preflight failed at {0} @ {1} fps" -f $capSize, $capFps)
        if (-not (($capSize -eq '320x240') -and ($capFps -eq 30))) {
            $capSize = '320x240'
            $capFps = 30
            $pf = Invoke-Preflight -TestSize $capSize -TestFps $capFps -Tag 'fallback'
        }
    }
    if (-not $pf.Ok) {
        throw ("Camera preflight failed at both requested and fallback settings. Close any application using '{0}' and retry. See {1}" -f $Device, $pf.ErrLog)
    }
    Write-Log ("preflight OK: using {0} @ {1} fps" -f $capSize, $capFps)
    Get-ChildItem -LiteralPath $OutDir -Filter '_preflight-*.mp4' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800

    # Phase 1: MAGENTA.
    $magenta = Invoke-Phase -Name 'magenta' -Color ([System.Drawing.Color]::Magenta) -CapSize $capSize -CapFps $capFps

    Write-Log ("gap {0}s: desktop visible again" -f $GapSec)
    Start-Sleep -Seconds $GapSec

    # Phase 2: CYAN.
    $cyan = Invoke-Phase -Name 'cyan' -Color ([System.Drawing.Color]::Cyan) -CapSize $capSize -CapFps $capFps

    # -----------------------------------------------------------------------
    # Analysis (after both phases).
    # -----------------------------------------------------------------------
    $totalSec = $LeadInSec + $DurationSec + $TailSec
    $offsets = @(@(3, 4, 5, 6, 7) | Where-Object { $_ -le [Math]::Max(1, $DurationSec - 1) })
    if ($offsets.Count -eq 0) {
        $offsets = @([Math]::Max(1, [int]($DurationSec / 2)))
    }

    Write-Host ""
    Write-Log ("ANALYSIS: extracting {0} frames per phase at lead-in + {1}s" -f $offsets.Count, (($offsets | ForEach-Object { $_ }) -join ', '))
    $aMagenta = Invoke-PhaseAnalysis -Name 'magenta' -Capture $magenta.Capture -Offsets $offsets
    $aCyan = Invoke-PhaseAnalysis -Name 'cyan' -Capture $cyan.Capture -Offsets $offsets

    Write-Host ""
    Write-Log "CAPTURE PROBE (ffprobe)"
    Show-CaptureInfo -Phase $magenta
    Show-CaptureInfo -Phase $cyan

    Write-Host ""
    Write-Log "DIGITIZER PROOF (HID stream during capture)"
    Show-DigitizerSummary -Name 'magenta' -Path $magenta.Digitizer -ErrPath $magenta.DigitizerErr -Seconds $totalSec
    Show-DigitizerSummary -Name 'cyan' -Path $cyan.Digitizer -ErrPath $cyan.DigitizerErr -Seconds $totalSec

    Write-Host ""
    Write-Log ("COLOR ANALYSIS (mean of every 8th pixel in x and y, {0} px step)" -f $SampleStep)
    Show-PhaseColor -A $aMagenta
    Show-PhaseColor -A $aCyan

    $dR = [Math]::Abs($aMagenta.MeanR - $aCyan.MeanR)
    $dG = [Math]::Abs($aMagenta.MeanG - $aCyan.MeanG)
    $dB = [Math]::Abs($aMagenta.MeanB - $aCyan.MeanB)
    $maxDelta = [Math]::Max($dR, [Math]::Max($dG, $dB))

    $maxStdev = [Math]::Max(
        [Math]::Max($aMagenta.SdR, $aMagenta.SdG),
        [Math]::Max($aMagenta.SdB, [Math]::Max($aCyan.SdR, [Math]::Max($aCyan.SdG, $aCyan.SdB)))
    )

    Write-Host ""
    Write-Log ("channel deltas |magenta-cyan|: R={0:N2} G={1:N2} B={2:N2}" -f $dR, $dG, $dB)

    $enoughFrames = ($aMagenta.Frames.Count -ge 2) -and ($aCyan.Frames.Count -ge 2)
    $live = $enoughFrames -and ($maxDelta -gt 40) -and ($maxStdev -lt 25)
    $same = ($dR -lt 15) -and ($dG -lt 15) -and ($dB -lt 15)
    $em = [char]0x2014

    if (-not $enoughFrames) {
        $verdict = "INCONCLUSIVE $em inspect frames manually"
        $verdictColor = 'Red'
    } elseif ($live) {
        $verdict = "LIVE SENSOR VIEW $em camera follows screen color"
        $verdictColor = 'Green'
    } elseif ($same) {
        $verdict = "TEST PATTERN $em camera ignores screen color"
        $verdictColor = 'Yellow'
    } else {
        $verdict = "INCONCLUSIVE $em inspect frames manually"
        $verdictColor = 'Red'
    }

    Write-Host ""
    Write-Host "============================================================"
    Write-Host ("VERDICT: " + $verdict) -ForegroundColor $verdictColor
    Write-Host "============================================================"
    Write-Host ("rule: LIVE if max|delta| > 40 and max stdev < 25; TEST if all |delta| < 15; else INCONCLUSIVE")
    Write-Host ("observed: max|delta|={0:N2}  max stdev={1:N2}  frames={2}/{3}" -f $maxDelta, $maxStdev, $aMagenta.Frames.Count, $aCyan.Frames.Count)
    if (-not $enoughFrames) {
        Write-Host "note: fewer than 2 usable frames in a phase, so the statistical verdict is forced to INCONCLUSIVE."
    }

    Write-Host ""
    Write-Host "Frames for manual inspection:"
    $framePaths = @()
    foreach ($a in @($aMagenta, $aCyan)) {
        foreach ($f in $a.Frames) { $framePaths += $f.Path }
    }
    if ($framePaths.Count -eq 0) {
        Write-Host "  (none)"
    } else {
        foreach ($p in $framePaths) { Write-Host ("  " + $p) }
    }

    Write-Host ""
    Write-Log ("done; outputs in {0}" -f $OutDir)
} catch {
    Write-Log ("FATAL: {0}" -f $_.Exception.Message)
    throw
} finally {
    if ($null -ne $script:ActiveForm) {
        try { $script:ActiveForm.Close() } catch { }
        try { $script:ActiveForm.Dispose() } catch { }
        $script:ActiveForm = $null
    }
    if ($null -ne $script:ActiveTimer) {
        try { $script:ActiveTimer.Stop() } catch { }
        try { $script:ActiveTimer.Dispose() } catch { }
        $script:ActiveTimer = $null
    }
    Stop-AllBg
    Restore-Cursor
    Write-Log "cleanup complete (no forms or background processes left)"
}
