[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# A booster must not cost what it is there to save. Once the stage has settled
# nothing on it may animate by itself: in 5.0 one looping dot kept the whole
# window redrawing behind a running game, about a fifth of a processor core.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This UI regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

$settleSeconds = 12
$measureSeconds = 8
# Percent of one core. 5.0 used 35-47 here; a CI runner without a graphics card
# draws in software, so the limit leaves room for the one-second timer.
$limit = 12

# Only the launch runner may loop, and it stops with the window's focus.
$stage = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\GameStage.cs'))
$loops = [regex]::Matches($stage, 'RepeatBehavior\.Forever').Count
if ($loops -gt 1) {
    throw "The stage has $loops endless animations; only the launch runner may have one."
}

function Measure-Load {
    param([string]$Label, [string[]]$Arguments)
    $process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList (@(
        '--skip-setup', '--demo', '--demo-shelf',
        ('--test-instance=' + [Guid]::NewGuid().ToString('N'))) + $Arguments)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
            throw 'Boostix did not open its window.'
        }
        Start-Sleep -Seconds $settleSeconds
        $process.Refresh()
        $before = $process.TotalProcessorTime.TotalMilliseconds
        $started = [DateTime]::UtcNow
        Start-Sleep -Seconds $measureSeconds
        $process.Refresh()
        $used = $process.TotalProcessorTime.TotalMilliseconds - $before
        $percent = $used / ([DateTime]::UtcNow - $started).TotalMilliseconds * 100
        Write-Host ('  {0}: {1:N1}% of one core' -f $Label, $percent)
        if ($percent -gt $limit) {
            throw ('{0} keeps using {1:N1}% of a core after it settled.' -f $Label, $percent)
        }
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Measure-Load 'The stage' @()
Measure-Load 'The live stage' @('--demo-live')

'Stage load test passed: the stage and the live stage stay quiet once settled.'
