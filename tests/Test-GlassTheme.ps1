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

$backdropPath = Join-Path $projectRoot 'Boostix\WindowBackdrop.cs'
$programPath = Join-Path $projectRoot 'Boostix\Program.cs'
$tokensPath = Join-Path $projectRoot 'Boostix\DesignTokens.cs'
$buildPath = Join-Path $projectRoot 'build.ps1'
if (-not (Test-Path -LiteralPath $backdropPath -PathType Leaf)) {
    throw 'The Windows 11 system backdrop source is missing.'
}
$backdrop = [IO.File]::ReadAllText($backdropPath)
$program = [IO.File]::ReadAllText($programPath)
$tokens = [IO.File]::ReadAllText($tokensPath)
$build = [IO.File]::ReadAllText($buildPath)

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Description)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing glass theme contract: $Description"
    }
}

# The backdrop comes from DWM itself: dark acrylic behind a dark window,
# only on builds that support DWMWA_SYSTEMBACKDROP_TYPE, never in High Contrast.
Assert-Contains $backdrop 'DwmSystemBackdropType = 38' 'DWMWA_SYSTEMBACKDROP_TYPE attribute'
Assert-Contains $backdrop 'DwmUseImmersiveDarkMode = 20' 'dark system material'
Assert-Contains $backdrop 'SystemParameters.HighContrast' 'High Contrast opt-out'
Assert-Contains $backdrop '"EnableTransparency"' 'Windows transparency effects setting honoured'
Assert-Contains $program 'WindowBackdrop.TryApply(' 'backdrop applied to the main window'
Assert-Contains $program 'WindowBackdrop.Remove(' 'backdrop removed when High Contrast turns on'
Assert-Contains $program 'WmSettingChange' 'glass follows the transparency switch while Boostix runs'
# DWM draws no backdrop for a frameless popup window: the glass needs a
# framed window whose frame WindowChrome hides, without the accent caption.
Assert-Contains $program 'WindowBackdrop.BuildSupportsGlass' 'framed window on builds with glass'
Assert-Contains $program 'WindowStyle.SingleBorderWindow' 'framed window style for the system backdrop'
Assert-Contains $backdrop 'DwmCaptionColor = 35' 'accent caption colour suppressed'
# On a framed window WindowChrome raises Loaded from inside SourceInitialized;
# the handle must exist by then or Ctrl+Alt+B is never registered.
Assert-Contains $program 'protected override void OnSourceInitialized' 'window handle stored before Loaded'
Assert-Contains $program 'GlassFrameThickness = new Thickness(-1)' 'frame extended under the client area'
Assert-Contains $build '"$projectRoot\Boostix\WindowBackdrop.cs"' 'backdrop source compiled into Boostix.exe'
Assert-Contains $tokens 'WindowTintBrushKey' 'translucent window tint resource'
# Themes: the saved theme is applied before the first colour is used, can be
# picked in Settings, and dialogs read colours when they open.
Assert-Contains $program 'ReadThemePreference(' 'saved theme read at start-up'
Assert-Contains $program 'BoostixDesignTokens.SetPalette(' 'theme applied from Settings'
$centerSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'))
Assert-Contains $centerSource '"Boostix.Center.Theme."' 'theme choice in Settings'
foreach ($dialog in @('OptimizationFlow.cs', 'UpdateFlow.cs')) {
    $dialogSource = [IO.File]::ReadAllText((Join-Path $projectRoot ('Boostix\' + $dialog)))
    if ($dialogSource -match 'static\s+readonly\s+Color\s') {
        throw "$dialog captures theme colours once; a theme switch would not reach it."
    }
}

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
$assembly = [Reflection.Assembly]::LoadFrom($ApplicationPath)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'

$backdropType = $assembly.GetType('Boostix.WindowBackdrop', $true)
$supports = $backdropType.GetMethod('IsSupportedBuild', $flags)
if (-not $supports) {
    throw 'WindowBackdrop.IsSupportedBuild is missing.'
}
foreach ($case in @(
        @{ Build = 19045; Expected = $false },
        @{ Build = 22000; Expected = $false },
        @{ Build = 22620; Expected = $false },
        @{ Build = 22621; Expected = $true },
        @{ Build = 26200; Expected = $true })) {
    $actual = [bool]$supports.Invoke($null, @([int]$case.Build))
    if ($actual -ne $case.Expected) {
        throw "Backdrop support for build $($case.Build) was $actual; expected $($case.Expected)."
    }
}

# Glass only when Windows itself shows glass: transparency effects on and no
# High Contrast. Otherwise DWM paints a grey fallback that would wash out
# the petrol tint, so Boostix stays opaque.
$shouldUse = $backdropType.GetMethod('ShouldUseGlass', $flags)
if (-not $shouldUse) {
    throw 'WindowBackdrop.ShouldUseGlass is missing.'
}
foreach ($case in @(
        @{ Build = 26200; HighContrast = $false; Transparency = $true; Expected = $true },
        @{ Build = 26200; HighContrast = $false; Transparency = $false; Expected = $false },
        @{ Build = 26200; HighContrast = $true; Transparency = $true; Expected = $false },
        @{ Build = 22000; HighContrast = $false; Transparency = $true; Expected = $false })) {
    $actual = [bool]$shouldUse.Invoke($null, @([int]$case.Build, [bool]$case.HighContrast, [bool]$case.Transparency))
    if ($actual -ne $case.Expected) {
        throw "Glass for build $($case.Build), High Contrast $($case.HighContrast), transparency $($case.Transparency) was $actual; expected $($case.Expected)."
    }
}

$tokenType = $assembly.GetType('Boostix.BoostixDesignTokens', $true)
function Get-Token {
    param([string]$Name)
    $property = $tokenType.GetProperty($Name, $flags)
    if (-not $property) {
        throw "Missing design token: $Name"
    }
    return [System.Windows.Media.Color]$property.GetValue($null, $null)
}

function Get-Channel {
    param([double]$Value)
    $s = $Value / 255.0
    if ($s -le 0.03928) { return $s / 12.92 }
    return [Math]::Pow(($s + 0.055) / 1.055, 2.4)
}

function Get-Luminance {
    param([System.Windows.Media.Color]$Color)
    return 0.2126 * (Get-Channel $Color.R) +
        0.7152 * (Get-Channel $Color.G) +
        0.0722 * (Get-Channel $Color.B)
}

function Get-Contrast {
    param([System.Windows.Media.Color]$A, [System.Windows.Media.Color]$B)
    $la = Get-Luminance $A
    $lb = Get-Luminance $B
    return ([Math]::Max($la, $lb) + 0.05) / ([Math]::Min($la, $lb) + 0.05)
}

function Get-Composite {
    param([System.Windows.Media.Color]$Over, [System.Windows.Media.Color]$Behind)
    $a = $Over.A / 255.0
    return [System.Windows.Media.Color]::FromRgb(
        [byte][Math]::Round($a * $Over.R + (1 - $a) * $Behind.R),
        [byte][Math]::Round($a * $Over.G + (1 - $a) * $Behind.G),
        [byte][Math]::Round($a * $Over.B + (1 - $a) * $Behind.B))
}

function Assert-Contrast {
    param(
        [System.Windows.Media.Color]$Foreground,
        [System.Windows.Media.Color]$Background,
        [double]$Minimum,
        [string]$Description
    )
    $ratio = Get-Contrast $Foreground $Background
    if ($ratio -lt $Minimum) {
        throw ('{0} contrast is {1:N2}:1; expected at least {2}:1.' -f $Description, $ratio, $Minimum)
    }
}

function Get-Hue {
    param([System.Windows.Media.Color]$Color)
    return [System.Drawing.Color]::FromArgb($Color.R, $Color.G, $Color.B).GetHue()
}

Add-Type -AssemblyName System.Drawing

# Petrol and lemon: the pair the author picked for the glass redesign.
$lemon = [System.Windows.Media.Color]::FromRgb(0xFF, 0xF4, 0x4F)
$petrolNight = [System.Windows.Media.Color]::FromRgb(0x0A, 0x22, 0x29)
if (-not (Get-Token 'Accent').Equals($lemon)) {
    throw "The Boost accent is $(Get-Token 'Accent'); expected lemon $lemon."
}
if (-not (Get-Token 'Background').Equals($petrolNight)) {
    throw "The window background is $(Get-Token 'Background'); expected petrol $petrolNight."
}
# Every theme in Settings must pass the same checks as the default one.
$palettesField = $tokenType.GetField('Palettes', $flags)
$setPalette = $tokenType.GetMethod('SetPalette', $flags)
if (-not $palettesField -or -not $setPalette) {
    throw 'Themes are missing: BoostixDesignTokens.Palettes and SetPalette are required.'
}
$paletteIds = @($palettesField.GetValue($null) | ForEach-Object { $_.Id })
if (($paletteIds -join ',') -cne 'petrol,pine,ink,amber') {
    throw "Themes were: $($paletteIds -join ',')"
}
if ([bool]$setPalette.Invoke($null, @('no-such-theme'))) {
    throw 'An unknown theme id must be refused.'
}
$glass = $tokenType.GetProperty('GlassBackdrop', $flags)
if (-not $glass -or -not $glass.CanWrite) {
    throw 'BoostixDesignTokens.GlassBackdrop must be a settable flag.'
}
try {
foreach ($paletteId in $paletteIds) {
    if (-not [bool]$setPalette.Invoke($null, @($paletteId))) {
        throw "Theme $paletteId could not be selected."
    }
    $hueGap = [Math]::Abs((Get-Hue (Get-Token 'Warning')) - (Get-Hue (Get-Token 'Accent')))
    if ($hueGap -lt 15) {
        throw "Warning colour is too close to the accent in the $paletteId theme."
    }

    foreach ($backgroundName in @('Background', 'Sidebar', 'Surface', 'SurfaceRaised')) {
        $background = Get-Token $backgroundName
        foreach ($foregroundName in @('Text', 'SecondaryText', 'MutedText', 'AccentText', 'Success', 'Warning', 'Error')) {
            Assert-Contrast (Get-Token $foregroundName) $background 4.5 "$foregroundName on $backgroundName ($paletteId)"
        }
        Assert-Contrast (Get-Token 'Accent') $background 3 "Accent on $backgroundName"
    }
    foreach ($foregroundName in @('Text', 'SecondaryText')) {
        Assert-Contrast (Get-Token $foregroundName) (Get-Token 'Hover') 4.5 "$foregroundName on Hover"
    }
    Assert-Contrast (Get-Token 'AccentForeground') (Get-Token 'Accent') 7 "Text on the accent ($paletteId)"
    Assert-Contrast (Get-Token 'AccentForeground') (Get-Token 'AccentText') 7 'Text on the hovered accent'
    Assert-Contrast (Get-Token 'AccentForeground') (Get-Token 'Destructive') 4.5 'Text on the destructive action'


    $white = [System.Windows.Media.Color]::FromRgb(255, 255, 255)
    # Dark acrylic never lets more than this much light through behind the tint.
    $brightAcrylic = [System.Windows.Media.Color]::FromRgb(0x60, 0x60, 0x60)
    try {
        $glass.SetValue($null, $false, $null)
        foreach ($pair in @(@('WindowTint', 'Background'), @('SidebarTint', 'Sidebar'))) {
            if (-not (Get-Token $pair[0]).Equals((Get-Token $pair[1]))) {
                throw "$($pair[0]) must equal the opaque $($pair[1]) without the system backdrop."
            }
        }

        $glass.SetValue($null, $true, $null)
        foreach ($pair in @(@('WindowTint', 'Background'), @('SidebarTint', 'Sidebar'))) {
            $tint = Get-Token $pair[0]
            $base = Get-Token $pair[1]
            if ($tint.A -lt 0xB0 -or $tint.A -gt 0xF0) {
                throw "$($pair[0]) alpha is $($tint.A); the glass tint must stay translucent but readable."
            }
            if ($tint.R -ne $base.R -or $tint.G -ne $base.G -or $tint.B -ne $base.B) {
                throw "$($pair[0]) must tint with the $($pair[1]) colour."
            }
            Assert-Contrast (Get-Token 'Text') (Get-Composite $tint $white) 4.5 "Text on $($pair[0]) over a white window"
            foreach ($foregroundName in @('Text', 'SecondaryText', 'MutedText', 'AccentText')) {
                Assert-Contrast (Get-Token $foregroundName) (Get-Composite $tint $brightAcrylic) 4.5 "$foregroundName on $($pair[0]) over acrylic"
            }
        }
    }
    finally {
        $glass.SetValue($null, $false, $null)
    }
}
}
finally {
    [void]$setPalette.Invoke($null, @('petrol'))
}

$apply = @($tokenType.GetMethods($flags) | Where-Object {
    $_.Name -ceq 'ApplyThemeResources' -and $_.GetParameters().Count -eq 2
})
$keyField = $tokenType.GetField('WindowTintBrushKey', $flags)
if ($apply.Count -ne 1 -or -not $keyField) {
    throw 'The window tint is not published as a theme resource.'
}
$resources = New-Object System.Windows.ResourceDictionary
$arguments = New-Object 'object[]' 2
$arguments[0] = $resources.PSObject.BaseObject
$arguments[1] = $true
[void]$apply[0].Invoke($null, $arguments)
$tintBrush = $resources[[string]$keyField.GetValue($null)] -as [System.Windows.Media.SolidColorBrush]
if (-not $tintBrush -or -not $tintBrush.Color.Equals([System.Windows.SystemColors]::WindowColor)) {
    throw 'High Contrast must replace the glass tint with the opaque Window colour.'
}

# Windows shows the glass only on the active window and a flat fallback on an
# inactive one, so the tint turns opaque petrol while Boostix is inactive.
$sidebarKeyField = $tokenType.GetField('SidebarTintBrushKey', $flags)
$applyTint = $tokenType.GetMethod('ApplyWindowTint', $flags)
if (-not $sidebarKeyField -or -not $applyTint) {
    throw 'The window and sidebar tints cannot follow window activation.'
}
Assert-Contains $program 'ApplyWindowTint(' 'tint follows window activation'
# Activation changes fade like the DWM backdrop instead of jumping.
Assert-Contains $program 'FadeWindowTint(' 'tint fades on activation'
$center = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'))
Assert-Contains $center 'BoostixDesignTokens.SidebarTintBrushKey' 'sidebar uses the shared tint resource'
try {
    $glass.SetValue($null, $true, $null)
    $tintResources = New-Object System.Windows.ResourceDictionary
    foreach ($case in @(
            @{ Active = $true; Translucent = $true },
            @{ Active = $false; Translucent = $false })) {
        [void]$applyTint.Invoke($null, @($tintResources.PSObject.BaseObject, [bool]$case.Active))
        foreach ($keyName in @('WindowTintBrushKey', 'SidebarTintBrushKey')) {
            $brush = $tintResources[[string]$tokenType.GetField($keyName, $flags).GetValue($null)] -as [System.Windows.Media.SolidColorBrush]
            if (-not $brush) {
                throw "$keyName is not published by ApplyWindowTint."
            }
            $translucent = $brush.Color.A -lt 255
            if ($translucent -ne $case.Translucent) {
                throw "$keyName translucent=$translucent with the window active=$($case.Active)."
            }
        }
    }
}
finally {
    $glass.SetValue($null, $false, $null)
}

Write-Host 'Glass backdrop and petrol/lemon contrast contracts passed.' -ForegroundColor Green
