[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# Dashboard motion: start-up self-test, live needle, rolling timer digits,
# one sliding navigation marker and a tray icon that shows Boost state.
# Every animation must fall back to an instant change when Windows
# animations are off.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

$gauge = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostGauge.cs'))
$center = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'))
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
$tray = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\TrayIntegration.cs'))

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Description)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing motion contract: $Description"
    }
}

Assert-Contains $gauge 'public void PlaySelfTest()' 'gauge self-test'
Assert-Contains $gauge 'public void SetLiveLoad(double load)' 'live needle'
Assert-Contains $gauge 'class RollingReadout' 'rolling timer readout'
Assert-Contains $gauge 'class SystemLoadSampler' 'processor load sampler'
Assert-Contains $program 'boostGauge.PlaySelfTest()' 'self-test runs at start-up'
Assert-Contains $program 'boostGauge.SetLiveLoad(' 'needle follows the load while Boost runs'
Assert-Contains $center 'MoveSelectionMarker(' 'one sliding navigation marker'
Assert-Contains $tray 'CreateGaugeIcon(' 'tray icon drawn per Boost state'
foreach ($source in @(
        @{ Name = 'BoostGauge.cs'; Text = $gauge },
        @{ Name = 'BoostCenterOverlay.cs'; Text = $center })) {
    if ($source.Text.IndexOf('MotionEnabled', [StringComparison]::Ordinal) -lt 0 -and
        $source.Text.IndexOf('ClientAreaAnimation', [StringComparison]::Ordinal) -lt 0) {
        throw "$($source.Name) animates without checking the Windows animation setting."
    }
}

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName System.Drawing
$assembly = [Reflection.Assembly]::LoadFrom($ApplicationPath)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'

function Get-Method {
    param([string]$TypeName, [string]$Name)
    $type = $assembly.GetType($TypeName, $true)
    $method = $type.GetMethod($Name, $flags)
    if (-not $method) {
        throw "$TypeName.$Name is missing."
    }
    return $method
}

# The live needle stays inside the Boost zone (60..120 degrees) and keeps
# off the zone edges, so it still reads as "Boost on" at idle and at full load.
$liveAngle = Get-Method 'Boostix.BoostGauge' 'LiveAngle'
$idle = [double]$liveAngle.Invoke($null, @([double]0))
$full = [double]$liveAngle.Invoke($null, @([double]1))
$half = [double]$liveAngle.Invoke($null, @([double]0.5))
$over = [double]$liveAngle.Invoke($null, @([double]4))
$nan = [double]$liveAngle.Invoke($null, @([double]::NaN))
if ($idle -le 60 -or $full -ge 120 -or -not ($idle -lt $half -and $half -lt $full) -or $over -ne $full -or $nan -ne $idle) {
    throw "Live needle angles are wrong: idle=$idle half=$half full=$full over=$over nan=$nan"
}

# Scale divisions light up when the needle passes them during the self-test.
$tickDelay = Get-Method 'Boostix.BoostGauge' 'SelfTestTickDelay'
$first = [double]$tickDelay.Invoke($null, @([double]-120, 600))
$middle = [double]$tickDelay.Invoke($null, @([double]0, 600))
$last = [double]$tickDelay.Invoke($null, @([double]120, 600))
if ($first -ne 0 -or [Math]::Abs($last - 600) -gt 0.5 -or -not ($middle -gt 0 -and $middle -lt 600)) {
    throw "Self-test tick timing is wrong: first=$first middle=$middle last=$last"
}
# The needle decelerates, so the second half of the scale takes longer.
if ($middle -ge 300) {
    throw "Self-test ticks do not follow the decelerating needle: middle=$middle"
}

# Only changed clock digits roll; words and length changes replace the text.
$changed = Get-Method 'Boostix.RollingReadout' 'ChangedPositions'
$roll = $changed.Invoke($null, @('00:09', '00:10'))
if (-not $roll -or (@($roll) -join ',') -ne '3,4') {
    throw "Rolling digits for 00:09 -> 00:10 were $(@($roll) -join ',')"
}
$startWord = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/Rg9GB0Lo='))
$stopWord = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0YHRgtC+0L8h'))
foreach ($pair in @(@($startWord, '00:01'), @('09:59', '100:00'), @('00:01', $stopWord))) {
    if ($null -ne $changed.Invoke($null, @($pair[0], $pair[1]))) {
        throw "'$($pair[0])' -> '$($pair[1])' must replace the readout instead of rolling."
    }
}

# Processor load from two GetSystemTimes readings (kernel time includes idle).
$loadBetween = Get-Method 'Boostix.SystemLoadSampler' 'LoadBetween'
$load = [double]$loadBetween.Invoke($null, @([UInt64]100, [UInt64]1000, [UInt64]175, [UInt64]1100))
if ([Math]::Abs($load - 0.25) -gt 0.0001) {
    throw "Processor load should be 0.25, was $load"
}
$noTime = [double]$loadBetween.Invoke($null, @([UInt64]100, [UInt64]1000, [UInt64]100, [UInt64]1000))
if (-not [double]::IsNaN($noTime)) {
    throw 'Processor load without elapsed time must be unknown (NaN).'
}

# The navigation marker lands exactly on each 42 DIP row with its 4 DIP gap.
$offset = Get-Method 'Boostix.BoostCenterOverlay' 'NavigationOffset'
foreach ($row in 0..5) {
    $value = [double]$offset.Invoke($null, @([int]$row))
    if ($value -ne ($row * 46)) {
        throw "Navigation marker offset for row $row was $value"
    }
}

# The tray icon shows the needle in a lit lemon zone only while Boost runs.
$iconFactory = Get-Method 'Boostix.BoostixTrayIcon' 'CreateGaugeBitmap'
function Get-LemonPixels {
    param([bool]$Active)
    $bitmap = [System.Drawing.Bitmap]$iconFactory.Invoke($null, @($Active))
    try {
        $count = 0
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                $c = $bitmap.GetPixel($x, $y)
                if ($c.A -gt 200 -and $c.R -gt 220 -and $c.G -gt 210 -and $c.B -lt 140) { $count++ }
            }
        }
        return $count
    }
    finally { $bitmap.Dispose() }
}
$activeLemon = Get-LemonPixels $true
$idleLemon = Get-LemonPixels $false
if ($activeLemon -lt 20 -or $idleLemon -ge ($activeLemon / 3)) {
    throw "Tray icon does not show Boost state: active lemon pixels=$activeLemon idle=$idleLemon"
}

Write-Host 'Motion design contracts passed: self-test, live needle, rolling digits, sliding marker and tray state.' -ForegroundColor Green
