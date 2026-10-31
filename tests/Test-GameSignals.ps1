[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# In-game confirmation (sound and a click-through badge), start with
# Windows into the tray, and the summary after a game closes.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Description)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing game signal contract: $Description"
    }
}

$signals = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\GameSignals.cs'))
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
$installer = [IO.File]::ReadAllText((Join-Path $projectRoot 'BoostixInstaller\Program.cs'))
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))

Assert-Contains $build '"$projectRoot\Boostix\GameSignals.cs"' 'signals compiled into Boostix.exe'
# The badge must never take focus from the game or catch its clicks.
Assert-Contains $signals 'WsExTransparent' 'click-through badge'
Assert-Contains $signals 'WsExNoActivate' 'badge never takes focus'
Assert-Contains $signals 'ShowActivated = false' 'badge shown without activation'
Assert-Contains $program '"GameSignals=" + centerSettings.GameSignals' 'signals preference saved'
Assert-Contains $program 'AutostartRegistration.Set(' 'start-with-Windows applied from Settings'
Assert-Contains $program 'AutostartRegistration.TrayArgument' 'start-up launch goes to the tray'
Assert-Contains $program 'SessionSummary.Describe(' 'summary after the game'
# Uninstall removes the start-up entry it may have left behind.
Assert-Contains $installer 'Software\Microsoft\Windows\CurrentVersion\Run' 'uninstall clears the start-up entry'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
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

# Start with Windows: one HKCU Run value that starts Boostix in the tray.
$autostart = $assembly.GetType('Boostix.AutostartRegistration', $true)
$command = [string](Get-Method 'Boostix.AutostartRegistration' 'BuildCommand').Invoke(
    $null, @('C:\Program Files\Boostix\Boostix.exe'))
if ($command -cne '"C:\Program Files\Boostix\Boostix.exe" --tray') {
    throw "Start-up command was $command"
}
$keyField = $autostart.GetField('RunKeyPath', $flags)
$original = [string]$keyField.GetValue($null)
if ($original -cne 'Software\Microsoft\Windows\CurrentVersion\Run') {
    throw "Start-up key was $original"
}
$testKey = 'Software\Boostix-Test-' + [Guid]::NewGuid().ToString('N')
$keyField.SetValue($null, $testKey)
try {
    $isEnabled = Get-Method 'Boostix.AutostartRegistration' 'IsEnabled'
    $set = Get-Method 'Boostix.AutostartRegistration' 'Set'
    if ([bool]$isEnabled.Invoke($null, @())) {
        throw 'A fresh key must not report start-up as enabled.'
    }
    if (-not [bool]$set.Invoke($null, @($true, 'C:\Apps\Boostix.exe'))) {
        throw 'Enabling start-up failed.'
    }
    $value = (Get-ItemProperty -Path ('HKCU:\' + $testKey) -Name 'Boostix').Boostix
    if ($value -cne '"C:\Apps\Boostix.exe" --tray' -or -not [bool]$isEnabled.Invoke($null, @())) {
        throw "Start-up value was $value"
    }
    if (-not [bool]$set.Invoke($null, @($false, 'C:\Apps\Boostix.exe')) -or [bool]$isEnabled.Invoke($null, @())) {
        throw 'Disabling start-up left the value behind.'
    }
}
finally {
    $keyField.SetValue($null, $original)
    Remove-Item -Path ('HKCU:\' + $testKey) -Recurse -Force -ErrorAction SilentlyContinue
}

# Confirmation sound: short, quiet, rising when Boost turns on and falling
# when it turns off.
$buildWave = Get-Method 'Boostix.ConfirmationSound' 'BuildWave'
function Get-WaveFacts {
    param([bool]$On)
    [byte[]]$wave = $buildWave.Invoke($null, @($On))
    if ([Text.Encoding]::ASCII.GetString($wave, 0, 4) -cne 'RIFF' -or
        [Text.Encoding]::ASCII.GetString($wave, 8, 4) -cne 'WAVE') {
        throw 'Confirmation sound is not a WAV file.'
    }
    $channels = [BitConverter]::ToInt16($wave, 22)
    $rate = [BitConverter]::ToInt32($wave, 24)
    $bits = [BitConverter]::ToInt16($wave, 34)
    $dataBytes = [BitConverter]::ToInt32($wave, 40)
    if ($channels -ne 1 -or $bits -ne 16 -or $rate -ne 44100) {
        throw "Unexpected sound format: $channels ch, $bits bit, $rate Hz"
    }
    $samples = $dataBytes / 2
    $peak = 0
    $crossFirst = 0
    $crossSecond = 0
    $previous = 0
    for ($i = 0; $i -lt $samples; $i++) {
        $s = [BitConverter]::ToInt16($wave, 44 + $i * 2)
        if ([Math]::Abs($s) -gt $peak) { $peak = [Math]::Abs($s) }
        if (($previous -lt 0 -and $s -ge 0)) {
            if ($i -lt $samples / 2) { $crossFirst++ } else { $crossSecond++ }
        }
        $previous = $s
    }
    return @{ Seconds = $samples / $rate; Peak = $peak; First = $crossFirst; Second = $crossSecond }
}
$on = Get-WaveFacts $true
$off = Get-WaveFacts $false
foreach ($facts in @($on, $off)) {
    if ($facts.Seconds -lt 0.12 -or $facts.Seconds -gt 0.45) {
        throw "Confirmation sound length is $($facts.Seconds) s"
    }
    if ($facts.Peak -gt 0.6 * 32767 -or $facts.Peak -lt 0.1 * 32767) {
        throw "Confirmation sound peak is $($facts.Peak)"
    }
}
if (-not ($on.Second -gt $on.First) -or -not ($off.Second -lt $off.First)) {
    throw "Sound pitch direction is wrong: on $($on.First)->$($on.Second), off $($off.First)->$($off.Second)"
}

# The badge sits in the top-right corner of the work area with a margin.
$place = Get-Method 'Boostix.BoostToast' 'PlaceInWorkArea'
$placeArguments = New-Object 'object[]' 2
$placeArguments[0] = [System.Windows.Rect]::new(0, 0, 1920, 1040)
$placeArguments[1] = [System.Windows.Size]::new(280, 64)
$point = [System.Windows.Point]$place.Invoke($null, $placeArguments)
if ($point.X -ne 1616 -or $point.Y -ne 24) {
    throw "Badge position was $point"
}

# Summary after the game.
$duration = Get-Method 'Boostix.SessionSummary' 'FormatDuration'
foreach ($case in @(
        @{ Span = [TimeSpan]::FromSeconds(30); Expected = '0LzQtdC90YzRiNC1INC80LjQvdGD0YLRiw==' },
        @{ Span = [TimeSpan]::new(0, 47, 10); Expected = 'NDcg0LzQuNC9' },
        @{ Span = [TimeSpan]::new(1, 12, 59); Expected = 'MSDRhyAxMiDQvNC40L0=' })) {
    $actual = [string]$duration.Invoke($null, @($case.Span))
    if ($actual -cne (Convert-UiName $case.Expected)) {
        throw "Duration $($case.Span) was formatted as $actual"
    }
}
$describe = Get-Method 'Boostix.SessionSummary' 'Describe'
foreach ($case in @(
        @{ Name = 'Cyberpunk2077.exe'; Span = [TimeSpan]::new(1, 12, 0); Pressure = 'Normal'; Expected = 'Q3liZXJwdW5rMjA3NzogMSDRhyAxMiDQvNC40L0g0YEgQm9vc3QuINCf0LDQvNGP0YLQuCDRhdCy0LDRgtCw0LvQvi4=' },
        @{ Name = ''; Span = [TimeSpan]::new(0, 5, 0); Pressure = 'Critical'; Expected = '0JjQs9GA0LA6IDUg0LzQuNC9INGBIEJvb3N0LiDQn9Cw0LzRj9GC0Lgg0L3QtSDRhdCy0LDRgtCw0LvQviwg0L/QvtC00YDQvtCx0L3QvtGB0YLQuCDQsiDCq9CY0YHRgtC+0YDQuNC4wrsu' },
        @{ Name = $null; Span = [TimeSpan]::FromSeconds(20); Pressure = 'Unavailable'; Expected = '0JjQs9GA0LA6INC80LXQvdGM0YjQtSDQvNC40L3Rg9GC0Ysg0YEgQm9vc3Qu' })) {
    $actual = [string]$describe.Invoke($null, @($case.Name, $case.Span, $case.Pressure))
    if ($actual -cne (Convert-UiName $case.Expected)) {
        throw "Summary was: $actual"
    }
}

Write-Host 'Game signals passed: start-up registration, confirmation sound, badge placement and game summary.' -ForegroundColor Green
