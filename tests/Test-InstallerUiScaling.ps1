[CmdletBinding()]
param(
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'Installer UI scaling tests require Windows.'
}
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne
    [Threading.ApartmentState]::STA) {
    throw 'Installer UI scaling tests require an STA Windows PowerShell process.'
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandPath = Join-Path $projectRoot 'ProductBrand.cs'
$installerSourcePath = Join-Path $projectRoot 'BoostixInstaller\Program.cs'

if (-not (Test-Path -LiteralPath $brandPath -PathType Leaf)) {
    throw "Product metadata source is missing: $brandPath"
}
if (-not (Test-Path -LiteralPath $installerSourcePath -PathType Leaf)) {
    throw "Installer source is missing: $installerSourcePath"
}

$brandSource = [IO.File]::ReadAllText($brandPath)
$versionMatches = [Text.RegularExpressions.Regex]::Matches(
    $brandSource,
    '(?m)^\s*public\s+const\s+string\s+ProductVersion\s*=\s*"' +
        '(?<version>\d+\.\d+\.\d+)"\s*;\s*$')
if ($versionMatches.Count -ne 1) {
    throw 'ProductBrand.cs must declare exactly one semantic ProductVersion.'
}
$productVersion = $versionMatches[0].Groups['version'].Value

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $projectRoot (
        'dist\Boostix-Setup-' + $productVersion + '.exe')
}
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path

$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
if ($metadata.ProductName -cne 'Boostix' -or
    $metadata.FileVersion -cne ($productVersion + '.0')) {
    throw (
        'The installer binary does not match ProductBrand.cs: ProductName=' +
        $metadata.ProductName + '; FileVersion=' + $metadata.FileVersion)
}

$failures = New-Object 'System.Collections.Generic.List[string]'
$instanceFlags = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Instance

function Add-ContractFailure {
    param([Parameter(Mandatory = $true)][string]$Message)

    [void]$failures.Add($Message)
}

function Get-RequiredType {
    param(
        [Parameter(Mandatory = $true)][Reflection.Assembly]$Assembly,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $type = $Assembly.GetType($Name, $false, $false)
    if ($null -eq $type) {
        throw "The compiled installer is missing type '$Name'."
    }
    return $type
}

function New-InstallerFormInstance {
    param(
        [Parameter(Mandatory = $true)][Type]$Type,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [Type[]]$ParameterTypes
    )

    $constructor = $Type.GetConstructor(
        $instanceFlags,
        $null,
        $ParameterTypes,
        $null)
    if ($null -eq $constructor) {
        throw "The compiled installer is missing the expected $($Type.FullName) constructor."
    }
    try {
        return [Windows.Forms.Form]$constructor.Invoke($Arguments)
    }
    catch [Reflection.TargetInvocationException] {
        if ($null -ne $_.Exception.InnerException) {
            throw $_.Exception.InnerException
        }
        throw
    }
}

function Get-RequiredFieldValue {
    param(
        [Parameter(Mandatory = $true)][object]$Instance,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $field = $Instance.GetType().GetField($Name, $instanceFlags)
    if ($null -eq $field) {
        throw "The compiled installer is missing field '$Name'."
    }
    $value = $field.GetValue($Instance)
    if ($null -eq $value) {
        throw "The compiled installer field '$Name' was not initialized."
    }
    return $value
}

function Invoke-RequiredInstanceMethod {
    param(
        [Parameter(Mandatory = $true)][object]$Instance,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [Type[]]$ParameterTypes,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowNull()]
        [object[]]$Arguments
    )

    $method = $Instance.GetType().GetMethod(
        $Name,
        $instanceFlags,
        $null,
        $ParameterTypes,
        $null)
    if ($null -eq $method) {
        throw "The compiled installer is missing method '$Name'."
    }
    try {
        return $method.Invoke($Instance, $Arguments)
    }
    catch [Reflection.TargetInvocationException] {
        if ($null -ne $_.Exception.InnerException) {
            throw $_.Exception.InnerException
        }
        throw
    }
}

function Assert-DpiAutoscalingContract {
    param(
        [Parameter(Mandatory = $true)][Windows.Forms.Form]$Form,
        [Parameter(Mandatory = $true)][string]$Role
    )

    if ($Form.AutoScaleMode -ne [Windows.Forms.AutoScaleMode]::Dpi) {
        Add-ContractFailure (
            "$Role must use AutoScaleMode.Dpi; actual=$($Form.AutoScaleMode).")
    }
    $dimensions = $Form.AutoScaleDimensions
    if ([Math]::Abs($dimensions.Width - 96.0) -gt 0.01 -or
        [Math]::Abs($dimensions.Height - 96.0) -gt 0.01) {
        Add-ContractFailure (
            "$Role must declare 96x96 AutoScaleDimensions; actual=" +
            "$($dimensions.Width)x$($dimensions.Height).")
    }
}

function Assert-InteractiveAccessibility {
    param(
        [Parameter(Mandatory = $true)][Windows.Forms.Control]$Root,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $interactive = New-Object 'System.Collections.Generic.List[System.Windows.Forms.Control]'
    $pending = New-Object 'System.Collections.Generic.Stack[System.Windows.Forms.Control]'
    $pending.Push($Root)
    while ($pending.Count -ne 0) {
        $parent = $pending.Pop()
        foreach ($control in $parent.Controls) {
            $typed = [Windows.Forms.Control]$control
            $pending.Push($typed)
            if ($typed -is [Windows.Forms.Button] -or
                $typed.GetType().Name -eq 'BoostixToggle') {
                [void]$interactive.Add($typed)
            }
        }
    }

    if ($interactive.Count -eq 0) {
        Add-ContractFailure "$Role does not expose any button or toggle controls."
        return
    }
    foreach ($control in $interactive) {
        $identity = "$Role/$($control.GetType().Name)[TabIndex=$($control.TabIndex)]"
        if (-not $control.TabStop) {
            Add-ContractFailure "$identity must participate in keyboard tab navigation."
        }
        if ([string]::IsNullOrWhiteSpace($control.AccessibleName)) {
            Add-ContractFailure "$identity must have a non-empty AccessibleName."
        }
    }
}

function Assert-ControlTreeInsideClientArea {
    param(
        [Parameter(Mandatory = $true)][Windows.Forms.Control]$Root,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $pending = New-Object 'System.Collections.Generic.Stack[System.Windows.Forms.Control]'
    $pending.Push($Root)
    while ($pending.Count -ne 0) {
        $parent = $pending.Pop()
        $client = $parent.ClientRectangle
        foreach ($control in $parent.Controls) {
            $typed = [Windows.Forms.Control]$control
            $pending.Push($typed)
            $bounds = $typed.Bounds
            if ($bounds.Left -lt $client.Left -or
                $bounds.Top -lt $client.Top -or
                $bounds.Right -gt $client.Right -or
                $bounds.Bottom -gt $client.Bottom) {
                Add-ContractFailure (
                    "$Role/$($typed.GetType().Name) leaves its parent's client area: " +
                    "bounds=$bounds; parentClient=$client.")
            }
        }
    }
}

function Assert-InstallerProgressUsesTrackWidth {
    param(
        [Parameter(Mandatory = $true)][Windows.Forms.Form]$Form,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $fill = [Windows.Forms.Panel](Get-RequiredFieldValue $Form 'progressFill')
    $track = [Windows.Forms.Control]$fill.Parent
    if ($null -eq $track -or $track.ClientSize.Width -le 480) {
        Add-ContractFailure (
            "$Role did not scale the progress track beyond its 96-DPI width.")
        return
    }

    if ($Form.GetType().Name -eq 'InstallerForm') {
        [void](Invoke-RequiredInstanceMethod `
            -Instance $Form `
            -Name 'AnimateProgress' `
            -ParameterTypes ([Type[]]@([int])) `
            -Arguments ([object[]]@($track.ClientSize.Width)))
    }
    else {
        $displayedField = $Form.GetType().GetField('displayedProgress', $instanceFlags)
        $targetField = $Form.GetType().GetField('targetProgress', $instanceFlags)
        if ($null -eq $displayedField -or $null -eq $targetField) {
            throw 'UpdateProgressForm is missing its progress state fields.'
        }
        $displayedField.SetValue($Form, 99)
        $targetField.SetValue($Form, 100)
        [void](Invoke-RequiredInstanceMethod `
            -Instance $Form `
            -Name 'ProgressAnimationTick' `
            -ParameterTypes ([Type[]]@([object], [EventArgs])) `
            -Arguments ([object[]]@($null, [EventArgs]::Empty)))
    }

    if ($fill.Width -ne $track.ClientSize.Width) {
        Add-ContractFailure (
            "$Role progress must fill the scaled track width; " +
            "fill=$($fill.Width), track=$($track.ClientSize.Width).")
    }
}

$installerSource = [IO.File]::ReadAllText($installerSourcePath)
$dpiPaintEvidence = [regex]::Matches(
    $installerSource,
    '(?i)\b(DeviceDpi|DpiX|ScaleForDpi|DpiScale)\b').Count
if ($dpiPaintEvidence -lt 4 -or
    $installerSource -cnotmatch '(?i)\b(DeviceDpi|DpiX)\b') {
    Add-ContractFailure (
        'Custom-painted installer controls must derive coordinates, pen widths, ' +
        'corner radii, and glyph sizes from the active DPI.')
}

$focusCueConditions = [regex]::Matches(
    $installerSource,
    '(?i)(Focused\s*&&\s*ShowFocusCues|ShowFocusCues\s*&&\s*Focused)').Count
if ($focusCueConditions -lt 2 -or
    $installerSource -match '(?i)if\s*\(\s*Focused\s*\)') {
    Add-ContractFailure (
        'Custom button and toggle focus visuals must be conditional on both ' +
        'Focused and ShowFocusCues, never on Focused alone.')
}

$reducedMotionEvidence = [regex]::Matches(
    $installerSource,
    '(?i)\b(ClientAreaAnimation|AnimationsEnabled|ShouldReduceMotion|ReducedMotion)\b').Count
if ($installerSource -cnotmatch '(?i)\bClientAreaAnimation\b' -or
    $reducedMotionEvidence -lt 3) {
    Add-ContractFailure (
        'Button, toggle, and progress animations must honor the Windows reduced-motion setting.')
}

function Get-BrandByte {
    param([Parameter(Mandatory = $true)][string]$Name)

    $match = [regex]::Match(
        $brandSource,
        '(?m)^\s*public\s+const\s+byte\s+' +
            [regex]::Escape($Name) + '\s*=\s*(?<value>\d+)\s*;\s*$')
    if (-not $match.Success) {
        throw "ProductBrand.cs does not declare $Name."
    }
    return [byte][int]$match.Groups['value'].Value
}

function Get-RelativeLuminance {
    param(
        [Parameter(Mandatory = $true)][byte]$Red,
        [Parameter(Mandatory = $true)][byte]$Green,
        [Parameter(Mandatory = $true)][byte]$Blue
    )

    $linear = foreach ($component in @($Red, $Green, $Blue)) {
        $value = $component / 255.0
        if ($value -le 0.04045) {
            $value / 12.92
        }
        else {
            [Math]::Pow(($value + 0.055) / 1.055, 2.4)
        }
    }
    return 0.2126 * $linear[0] +
        0.7152 * $linear[1] +
        0.0722 * $linear[2]
}

$accentTextLuminance = Get-RelativeLuminance `
    -Red (Get-BrandByte 'AccentTextRed') `
    -Green (Get-BrandByte 'AccentTextGreen') `
    -Blue (Get-BrandByte 'AccentTextBlue')
$backgroundLuminance = Get-RelativeLuminance -Red 22 -Green 22 -Blue 22
$contrast = ([Math]::Max($accentTextLuminance, $backgroundLuminance) + 0.05) /
    ([Math]::Min($accentTextLuminance, $backgroundLuminance) + 0.05)
if ($contrast -lt 4.5) {
    Add-ContractFailure (
        'Installer accent text does not meet 4.5:1 contrast on #161616; ' +
        "actual=$([Math]::Round($contrast, 2)):1.")
}
$accentTextUses = [regex]::Matches(
    $installerSource,
    '(?s)version\s*=\s*MakeLabel\(.{0,180}?accentText\)|' +
        '(phaseLabel|statusLabel)\.ForeColor\s*=\s*accentText').Count
if ($accentTextUses -lt 4) {
    Add-ContractFailure (
        'Small version and success text must use the accessible accentText color.')
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($InstallerPath))
$installerFormType = Get-RequiredType $assembly 'BoostixSetup.InstallerForm'
$updateFormType = Get-RequiredType $assembly 'BoostixSetup.UpdateProgressForm'

$baselineForms = New-Object 'System.Collections.Generic.List[System.Windows.Forms.Form]'
try {
    $installerBaseline = New-InstallerFormInstance `
        -Type $installerFormType `
        -Arguments ([object[]]@()) `
        -ParameterTypes ([Type[]]@())
    [void]$baselineForms.Add($installerBaseline)
    Assert-DpiAutoscalingContract $installerBaseline 'InstallerForm'
    Assert-InteractiveAccessibility $installerBaseline 'InstallerForm'

    $updateBaseline = New-InstallerFormInstance `
        -Type $updateFormType `
        -Arguments ([object[]]@($false)) `
        -ParameterTypes ([Type[]]@([bool]))
    [void]$baselineForms.Add($updateBaseline)
    Assert-DpiAutoscalingContract $updateBaseline 'UpdateProgressForm'
    Assert-InteractiveAccessibility $updateBaseline 'UpdateProgressForm'
}
finally {
    foreach ($form in $baselineForms) {
        $form.Dispose()
    }
}

# The supported installer acceptance matrix ends at 200% DPI. Scaling a Form
# to 300% on a 1080p test host makes WinForms clamp the top-level window to the
# host work area, which tests the runner's monitor rather than Boostix layout.
foreach ($factor in @(
    [single]1.25,
    [single]1.5,
    [single]1.75,
    [single]2.0
)) {
    $scaledForms = New-Object 'System.Collections.Generic.List[System.Windows.Forms.Form]'
    try {
        $installerScaled = New-InstallerFormInstance `
            -Type $installerFormType `
            -Arguments ([object[]]@()) `
            -ParameterTypes ([Type[]]@())
        [void]$scaledForms.Add($installerScaled)
        $installerScaled.Scale([Drawing.SizeF]::new($factor, $factor))
        Assert-ControlTreeInsideClientArea `
            $installerScaled `
            "InstallerForm at $([int]($factor * 100))%"
        Assert-InstallerProgressUsesTrackWidth `
            $installerScaled `
            "InstallerForm at $([int]($factor * 100))%"

        $updateScaled = New-InstallerFormInstance `
            -Type $updateFormType `
            -Arguments ([object[]]@($false)) `
            -ParameterTypes ([Type[]]@([bool]))
        [void]$scaledForms.Add($updateScaled)
        $updateScaled.Scale([Drawing.SizeF]::new($factor, $factor))
        Assert-ControlTreeInsideClientArea `
            $updateScaled `
            "UpdateProgressForm at $([int]($factor * 100))%"
        Assert-InstallerProgressUsesTrackWidth `
            $updateScaled `
            "UpdateProgressForm at $([int]($factor * 100))%"
    }
    finally {
        foreach ($form in $scaledForms) {
            $form.Dispose()
        }
    }
}

if ($failures.Count -ne 0) {
    throw (
        "Installer UI scaling regression test failed:`r`n - " +
        [string]::Join("`r`n - ", $failures.ToArray()))
}

Write-Host (
    'Installer DPI scaling, accessibility, focus, motion, and progress-width ' +
    "contracts passed for Boostix $productVersion.") -ForegroundColor Green
