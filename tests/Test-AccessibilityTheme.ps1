[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

$programPath = Join-Path $projectRoot 'Boostix\Program.cs'
$centerPath = Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'
$tokensPath = Join-Path $projectRoot 'Boostix\DesignTokens.cs'
$program = [IO.File]::ReadAllText($programPath)
$center = [IO.File]::ReadAllText($centerPath)
$tokens = [IO.File]::ReadAllText($tokensPath)

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Description
    )
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing accessibility contract: $Description"
    }
}

function Assert-Color {
    param(
        [System.Windows.Media.Color]$Actual,
        [System.Windows.Media.Color]$Expected,
        [string]$Description
    )
    if (-not $Actual.Equals($Expected)) {
        throw "$Description was $Actual; expected $Expected."
    }
}

if ($center -match 'private\s+static\s+readonly\s+Color\s+') {
    throw 'Boost Center still captures theme-sensitive colors in static readonly fields.'
}

Assert-Contains $program `
    'SystemParameters.StaticPropertyChanged +=' `
    'runtime SystemParameters subscription'
Assert-Contains $program `
    'SystemParameters.StaticPropertyChanged -=' `
    'guaranteed SystemParameters unsubscription'
Assert-Contains $program `
    'boostCenterOverlay.RefreshTheme();' `
    'live Center refresh'
Assert-Contains $program `
    'RefreshChromeButtonTheme(closeButton);' `
    'live chrome refresh'
Assert-Contains $program `
    'RestoreBoostCenterFocus();' `
    'focus restoration after Center close'
Assert-Contains $center `
    'SwitchPage((CenterPage)next, true);' `
    'Ctrl+Tab focus preservation'
Assert-Contains $center `
    'FocusSelectedTab(nextPage);' `
    'post-transition selected-tab focus'
Assert-Contains $center `
    'if (object.ReferenceEquals(previous, selected))' `
    'UIA duplicate selection-event guard'
Assert-Contains $center `
    'AutomationEvents.SelectionItemPatternOnElementSelected' `
    'UIA selected-element event'
Assert-Contains $center `
    'AutomationEvents.SelectionPatternOnInvalidated' `
    'UIA container selection invalidation'
Assert-Contains $center `
    'remove.Height = BoostixDesignTokens.MinimumActionHeight;' `
    '40 DIP profile remove action'
Assert-Contains $center `
    '<Setter Property=\"Width\" Value=\"16\"/>' `
    'wide scrollbar hit host'
Assert-Contains $center `
    '<Thumb Width=\"6\" MinHeight=\"40\"' `
    '40 DIP minimum scrollbar thumb'
Assert-Contains $tokens `
    'SystemColors.HighlightTextColor' `
    'Highlight/HighlightText pairing'
Assert-Contains $tokens `
    'SystemColors.WindowTextColor' `
    'Window/WindowText pairing'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
$assembly = [Reflection.Assembly]::LoadFrom($ApplicationPath)
$type = $assembly.GetType('Boostix.BoostixDesignTokens', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$apply = @($type.GetMethods($flags) | Where-Object {
    $_.Name -ceq 'ApplyThemeResources' -and
    $_.GetParameters().Count -eq 2
})
if ($apply.Count -ne 1) {
    throw "Expected one deterministic ApplyThemeResources overload; found $($apply.Count)."
}

function Get-Key {
    param([string]$FieldName)
    $field = $type.GetField($FieldName, $flags)
    if (-not $field) {
        throw "Missing semantic resource key field: $FieldName"
    }
    return [string]$field.GetValue($null)
}

$resources = New-Object System.Windows.ResourceDictionary
$invokeArguments = New-Object 'object[]' 2
$invokeArguments[0] = $resources.PSObject.BaseObject
$invokeArguments[1] = $false
[void]$apply[0].Invoke($null, $invokeArguments)
$normalReferences = @{}
foreach ($fieldName in @(
        'BackgroundBrushKey',
        'SurfaceBrushKey',
        'TextBrushKey',
        'AccentBrushKey',
        'AccentForegroundBrushKey')) {
    $key = Get-Key $fieldName
    $normalReferences[$key] = $resources[$key]
}

$invokeArguments[1] = $true
[void]$apply[0].Invoke($null, $invokeArguments)
$expected = @{
    BackgroundBrushKey = [System.Windows.SystemColors]::WindowColor
    SurfaceBrushKey = [System.Windows.SystemColors]::WindowColor
    SurfaceRaisedBrushKey = [System.Windows.SystemColors]::WindowColor
    TextBrushKey = [System.Windows.SystemColors]::WindowTextColor
    SecondaryTextBrushKey = [System.Windows.SystemColors]::WindowTextColor
    AccentTextBrushKey = [System.Windows.SystemColors]::WindowTextColor
    AccentBrushKey = [System.Windows.SystemColors]::HighlightColor
    HoverBrushKey = [System.Windows.SystemColors]::HighlightColor
    AccentPressedBrushKey = [System.Windows.SystemColors]::HighlightColor
    FocusBrushKey = [System.Windows.SystemColors]::HighlightColor
    DestructiveBrushKey = [System.Windows.SystemColors]::HighlightColor
    AccentForegroundBrushKey = [System.Windows.SystemColors]::HighlightTextColor
    ToggleKnobOnBrushKey = [System.Windows.SystemColors]::HighlightTextColor
    ToggleKnobOffBrushKey = [System.Windows.SystemColors]::WindowTextColor
}
foreach ($entry in $expected.GetEnumerator()) {
    $key = Get-Key $entry.Key
    $brush = $resources[$key] -as [System.Windows.Media.SolidColorBrush]
    if (-not $brush) {
        throw "Semantic resource $($entry.Key) is not a SolidColorBrush."
    }
    Assert-Color $brush.Color $entry.Value $entry.Key
}

foreach ($entry in $normalReferences.GetEnumerator()) {
    if ([object]::ReferenceEquals($entry.Value, $resources[$entry.Key])) {
        throw "Theme refresh mutated/reused $($entry.Key) instead of replacing the resource."
    }
}

Write-Host 'Runtime High Contrast and focus accessibility contracts passed.' `
    -ForegroundColor Green
