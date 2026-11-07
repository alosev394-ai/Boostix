[CmdletBinding()]
param(
    [switch]$CloseDiscord,
    [switch]$CloseEpic,
    [switch]$CloseSteam,
    [switch]$CloseOneDrive,
    [switch]$CloseTeams,
    [switch]$CloseWallpaper,
    [switch]$CloseNvidiaOverlay,
    [string]$CloseProcessNames,
    [string]$ReadySignalPath,
    [string]$ResultPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'
$startedUtc = [DateTime]::UtcNow
$logDirectory = Join-Path $env:LOCALAPPDATA 'Boostix'
if (-not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
}
$logPath = Join-Path $logDirectory 'Boost-Session.last.log'
if (-not $ResultPath) {
    $ResultPath = Join-Path $logDirectory 'Boost-Session.last.result.ini'
}
"[$(Get-Date -Format o)] Boostix session preparation started." | Set-Content -LiteralPath $logPath -Encoding UTF8

# Process closure is opt-in. Boostix does not terminate any third-party
# application unless the user explicitly disabled its "keep open" switch.
$processesToStop = @()
if ($CloseDiscord) { $processesToStop += 'Discord' }
if ($CloseEpic) { $processesToStop += @('EpicGamesLauncher', 'EpicWebHelper', 'EpicOnlineServicesUserHelper') }
if ($CloseSteam) { $processesToStop += @('steam', 'steamwebhelper', 'GameOverlayUI') }
if ($CloseOneDrive) { $processesToStop += 'OneDrive' }
if ($CloseTeams) { $processesToStop += @('ms-teams', 'Teams') }
if ($CloseWallpaper) { $processesToStop += @('wallpaper32', 'wallpaper64') }
if ($CloseNvidiaOverlay) { $processesToStop += @('NVIDIA Overlay', 'nvsphelper64') }
if ($CloseProcessNames) {
    foreach ($extraName in ($CloseProcessNames -split ',')) {
        $trimmedName = $extraName.Trim()
        if ($trimmedName -match '^[\w .\-]+$') { $processesToStop += $trimmedName }
    }
}
$processesToStop = @($processesToStop | Select-Object -Unique)
$stoppedProcesses = New-Object Collections.Generic.List[string]
$stillRunning = New-Object Collections.Generic.List[string]
$warnings = New-Object Collections.Generic.List[string]

function Stop-BoostBackgroundProcesses {
    foreach ($processName in $processesToStop) {
        $processes = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) {
            continue
        }
        try {
            # A program leaves as one: its helpers have no window and go with
            # the main one, so the whole group gets one request and one wait.
            $windowed = @($processes | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
            $mainProcess = if ($windowed.Count -gt 0) { $windowed[0] } else { $processes[0] }
            $executablePath = $mainProcess.Path
            foreach ($process in $windowed) {
                "Requesting $($process.Name) (PID $($process.Id)) to close." |
                    Add-Content -LiteralPath $logPath -Encoding UTF8
                $null = $process.CloseMainWindow()
            }
            $deadline = [DateTime]::UtcNow.AddMilliseconds(2500)
            foreach ($process in $processes) {
                $remaining = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
                if ($windowed.Count -gt 0 -and $remaining -gt 0) {
                    $null = $process.WaitForExit($remaining)
                }
            }
            $left = @($processes | Where-Object { -not $_.HasExited })
            if ($left.Count -gt 0) {
                # Boostix 2.0 deliberately remains graceful-only here. A
                # process that ignores WM_CLOSE may contain unsaved user
                # data, so a normal Boost session must never kill it.
                "Graceful close did not finish for $processName; leaving it running." |
                    Add-Content -LiteralPath $logPath -Encoding UTF8
                $stillRunning.Add($processName)
                continue
            }
            $stoppedProcesses.Add("$processName|$($mainProcess.Id)|$executablePath")
        }
        catch {
            $message = "Could not stop ${processName}: $($_.Exception.Message)"
            $warnings.Add($message)
            $message | Add-Content -LiteralPath $logPath -Encoding UTF8
        }
    }
}

Stop-BoostBackgroundProcesses

try {
    $resultDirectory = Split-Path -Parent $ResultPath
    if ($resultDirectory -and -not (Test-Path -LiteralPath $resultDirectory)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    }

    $resultLines = New-Object Collections.Generic.List[string]
    $resultLines.Add('[BoostSession]')
    $resultLines.Add('FormatVersion=1')
    $resultLines.Add("StartedUtc=$($startedUtc.ToString('o'))")
    $resultLines.Add("CompletedUtc=$([DateTime]::UtcNow.ToString('o'))")
    $resultLines.Add('Status=Completed')
    $resultLines.Add("StoppedProcessCount=$($stoppedProcesses.Count)")
    $resultLines.Add("WarningCount=$($warnings.Count)")
    $resultLines.Add('')
    $resultLines.Add('[StoppedProcesses]')
    for ($index = 0; $index -lt $stoppedProcesses.Count; $index++) {
        $resultLines.Add("Process$($index + 1)=$($stoppedProcesses[$index])")
    }
    $resultLines.Add('')
    $resultLines.Add('[StillRunning]')
    for ($index = 0; $index -lt $stillRunning.Count; $index++) {
        $resultLines.Add("Left$($index + 1)=$($stillRunning[$index])")
    }
    $resultLines.Add('')
    $resultLines.Add('[Warnings]')
    for ($index = 0; $index -lt $warnings.Count; $index++) {
        $safeWarning = $warnings[$index] -replace '[\r\n=]', ' '
        $resultLines.Add("Warning$($index + 1)=$safeWarning")
    }

    [IO.File]::WriteAllLines(
        $ResultPath,
        $resultLines.ToArray(),
        (New-Object Text.UTF8Encoding($false))
    )
    "Boost result written to $ResultPath" | Add-Content -LiteralPath $logPath -Encoding UTF8
}
catch {
    "Could not write boost result: $($_.Exception.Message)" | Add-Content -LiteralPath $logPath -Encoding UTF8
}

if ($ReadySignalPath) {
    try {
        $signalDirectory = Split-Path -Parent $ReadySignalPath
        if ($signalDirectory -and -not (Test-Path -LiteralPath $signalDirectory)) {
            New-Item -ItemType Directory -Path $signalDirectory -Force | Out-Null
        }
        [IO.File]::WriteAllText(
            $ReadySignalPath,
            (Get-Date).ToString('o'),
            (New-Object Text.UTF8Encoding($false))
        )
        "Boost readiness signal written to $ReadySignalPath" | Add-Content -LiteralPath $logPath -Encoding UTF8
    }
    catch {
        "Could not write boost readiness signal: $($_.Exception.Message)" | Add-Content -LiteralPath $logPath -Encoding UTF8
    }
}

'One-shot Boostix preparation completed.' | Add-Content -LiteralPath $logPath -Encoding UTF8
