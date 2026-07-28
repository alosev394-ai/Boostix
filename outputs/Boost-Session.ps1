[CmdletBinding()]
param(
    [switch]$CloseDiscord,
    [switch]$CloseEpic,
    [switch]$CloseSteam,
    [switch]$CloseOneDrive,
    [switch]$CloseTeams,
    [switch]$CloseWallpaper,
    [switch]$CloseNvidiaOverlay,
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
$processesToStop = @($processesToStop | Select-Object -Unique)
$stoppedProcesses = New-Object Collections.Generic.List[string]
$warnings = New-Object Collections.Generic.List[string]

function Stop-BoostBackgroundProcesses {
    foreach ($processName in $processesToStop) {
        $processes = Get-Process -Name $processName -ErrorAction SilentlyContinue
        foreach ($process in $processes) {
            "Requesting $($process.Name) (PID $($process.Id)) to close." |
                Add-Content -LiteralPath $logPath -Encoding UTF8
            try {
                $closed = $false
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                    $closed = $process.CloseMainWindow()
                    if ($closed) {
                        $null = $process.WaitForExit(2500)
                    }
                }
                if (-not $process.HasExited) {
                    "Graceful close timed out for $($process.Name); using the explicit force-close choice." |
                        Add-Content -LiteralPath $logPath -Encoding UTF8
                    $process | Stop-Process -Force -ErrorAction Stop
                }
                $stoppedProcesses.Add("$($process.Name)|$($process.Id)")
            }
            catch {
                $message = "Could not stop $($process.Name) (PID $($process.Id)): $($_.Exception.Message)"
                $warnings.Add($message)
                $message | Add-Content -LiteralPath $logPath -Encoding UTF8
            }
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
