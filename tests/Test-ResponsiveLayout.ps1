[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This UI regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$testInstanceArgument = '--test-instance=' + [Guid]::NewGuid().ToString('N')

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$compiledAssembly = [Reflection.Assembly]::LoadFile($ApplicationPath)
$overlayType = $compiledAssembly.GetType(
    'Boostix.BoostCenterOverlay',
    $true)
$bindingFlags = [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$transitionField = $overlayType.GetField(
    'PageTransitionTotalMilliseconds',
    $bindingFlags)
if (-not $transitionField) {
    throw 'Compiled Boost Center does not expose its transition duration.'
}
$transitionMilliseconds = [int]$transitionField.GetRawConstantValue()
if ($transitionMilliseconds -lt 220 -or $transitionMilliseconds -gt 260) {
    throw "Compiled page transition is $transitionMilliseconds ms; expected 220..260 ms."
}

$windowType = $compiledAssembly.GetType('Boostix.BoostWindow', $true)
$placementMethod = $windowType.GetMethod(
    'CalculateMonitorPlacement',
    [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static)
if (-not $placementMethod) {
    throw 'Compiled app does not expose the pure per-monitor placement calculation.'
}
$placementForSizeMethod = $windowType.GetMethod(
    'CalculateMonitorPlacementForSize',
    [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static)
if (-not $placementForSizeMethod) {
    throw 'Compiled app does not expose size-aware Center placement calculation.'
}

$productionWindow = $null
try {
    $constructor = $windowType.GetConstructor(
        [Reflection.BindingFlags]::Public -bor
            [Reflection.BindingFlags]::NonPublic -bor
            [Reflection.BindingFlags]::Instance,
        $null,
        [Type[]]@([string[]]),
        $null)
    if (-not $constructor) {
        throw 'Compiled app does not expose the production window constructor.'
    }

    $constructorArguments = New-Object 'object[]' 1
    $constructorArguments[0] = [string[]]@('--skip-setup')
    $productionWindow = $constructor.Invoke($constructorArguments)
    $adaptiveViewbox = $productionWindow.Content
    if (-not ($adaptiveViewbox -is [Windows.Controls.Viewbox])) {
        throw 'Production window is missing its adaptive Viewbox.'
    }

    $adaptiveViewbox.Measure([Windows.Size]::new(460.0, 552.0))
    $adaptiveViewbox.Arrange([Windows.Rect]::new(
        0.0,
        0.0,
        460.0,
        552.0))
    $adaptiveViewbox.UpdateLayout()

    $designSurface = $adaptiveViewbox.Child
    if ([Math]::Abs($designSurface.DesiredSize.Width - 460.0) -gt 0.001 -or
        [Math]::Abs($designSurface.DesiredSize.Height - 552.0) -gt 0.001) {
        throw "Production design surface measured $($designSurface.DesiredSize); expected 460x552 DIP."
    }

    $adaptiveMatrix = $designSurface.
        TransformToAncestor($adaptiveViewbox).Value
    if ([Math]::Abs($adaptiveMatrix.M11 - 1.0) -gt 0.001 -or
        [Math]::Abs($adaptiveMatrix.M22 - 1.0) -gt 0.001) {
        throw "Production UI is fractionally resampled at $($adaptiveMatrix.M11)x$($adaptiveMatrix.M22); expected a crisp 1.0x transform."
    }

    $compactLayoutMethod = $windowType.GetMethod(
        'SetCompactMainLayout',
        [Reflection.BindingFlags]::NonPublic -bor
            [Reflection.BindingFlags]::Instance)
    if (-not $compactLayoutMethod) {
        throw 'Production window does not expose its high-DPI compact reflow.'
    }
    [void]$compactLayoutMethod.Invoke($productionWindow, @($true))
    $adaptiveViewbox.Measure([Windows.Size]::new(460.0, 492.0))
    $adaptiveViewbox.Arrange([Windows.Rect]::new(
        0.0,
        0.0,
        460.0,
        492.0))
    $adaptiveViewbox.UpdateLayout()
    if ([Math]::Abs($designSurface.Height - 492.0) -gt 0.001) {
        throw "High-DPI compact design surface height was $($designSurface.Height); expected 492 DIP."
    }
    $compactMatrix = $designSurface.
        TransformToAncestor($adaptiveViewbox).Value
    if ([Math]::Abs($compactMatrix.M11 - 1.0) -gt 0.001 -or
        [Math]::Abs($compactMatrix.M22 - 1.0) -gt 0.001) {
        throw "High-DPI compact UI is fractionally resampled at $($compactMatrix.M11)x$($compactMatrix.M22)."
    }
    $boostSurfaceField = $windowType.GetField(
        'boostSurface',
        [Reflection.BindingFlags]::NonPublic -bor
            [Reflection.BindingFlags]::Instance)
    $boostSurface = $boostSurfaceField.GetValue($productionWindow)
    $boostSurfaceMatrix = $boostSurface.
        TransformToAncestor($designSurface).Value
    if ([Math]::Abs($boostSurfaceMatrix.M11 - 1.0) -gt 0.001 -or
        [Math]::Abs($boostSurfaceMatrix.M22 - 1.0) -gt 0.001) {
        throw (
            'High-DPI compact Boost control is internally resampled at ' +
            "$($boostSurfaceMatrix.M11)x$($boostSurfaceMatrix.M22).")
    }
}
finally {
    if ($productionWindow) {
        $productionWindow.Close()
    }
}

function Invoke-MonitorPlacement {
    param(
        [int]$WorkLeft,
        [int]$WorkTop,
        [int]$WorkRight,
        [int]$WorkBottom,
        [uint32]$DpiX,
        [uint32]$DpiY,
        [int]$CurrentLeft,
        [int]$CurrentTop,
        [bool]$Center
    )
    return [int[]]$placementMethod.Invoke(
        $null,
        [object[]]@(
            $WorkLeft,
            $WorkTop,
            $WorkRight,
            $WorkBottom,
            $DpiX,
            $DpiY,
            $CurrentLeft,
            $CurrentTop,
            $Center))
}

function Invoke-MonitorPlacementForSize {
    param(
        [int]$WorkLeft,
        [int]$WorkTop,
        [int]$WorkRight,
        [int]$WorkBottom,
        [uint32]$DpiX,
        [uint32]$DpiY,
        [int]$CurrentLeft,
        [int]$CurrentTop,
        [bool]$Center,
        [double]$LayoutWidthDip,
        [double]$LayoutHeightDip
    )
    return [int[]]$placementForSizeMethod.Invoke(
        $null,
        [object[]]@(
            $WorkLeft,
            $WorkTop,
            $WorkRight,
            $WorkBottom,
            $DpiX,
            $DpiY,
            $CurrentLeft,
            $CurrentTop,
            $Center,
            $LayoutWidthDip,
            $LayoutHeightDip))
}

foreach ($desktopCase in @(
    @{ Dpi = [uint32]96; Width = 460; Height = 552; Compact = 0 },
    @{ Dpi = [uint32]120; Width = 575; Height = 690; Compact = 0 },
    @{ Dpi = [uint32]144; Width = 690; Height = 828; Compact = 0 },
    @{ Dpi = [uint32]168; Width = 805; Height = 966; Compact = 0 },
    @{ Dpi = [uint32]192; Width = 920; Height = 984; Compact = 1 }
)) {
    $placement = Invoke-MonitorPlacement `
        -WorkLeft 0 `
        -WorkTop 0 `
        -WorkRight 1920 `
        -WorkBottom 1080 `
        -DpiX $desktopCase.Dpi `
        -DpiY $desktopCase.Dpi `
        -CurrentLeft 0 `
        -CurrentTop 0 `
        -Center $true
    if ($placement[2] -ne $desktopCase.Width -or
        $placement[3] -ne $desktopCase.Height -or
        $placement[4] -ne $desktopCase.Compact) {
        throw (
            "1920x1080 placement at $($desktopCase.Dpi) DPI was " +
            "$($placement -join ', ').")
    }
    $widthDip = $placement[2] * 96.0 / $desktopCase.Dpi
    $heightDip = $placement[3] * 96.0 / $desktopCase.Dpi
    if ([Math]::Abs($widthDip - 460.0) -gt 0.001 -or
        [Math]::Abs(
            $heightDip - $(if ($desktopCase.Compact) { 492.0 } else { 552.0 })) -gt 0.001) {
        throw "1920x1080 placement introduced fractional DIP scaling at $($desktopCase.Dpi) DPI."
    }
}

foreach ($centerCase in @(
    @{ Dpi = [uint32]96; Width = 620; Height = 552; Compact = 0 },
    @{ Dpi = [uint32]120; Width = 775; Height = 690; Compact = 0 },
    @{ Dpi = [uint32]144; Width = 930; Height = 828; Compact = 0 },
    @{ Dpi = [uint32]168; Width = 1085; Height = 966; Compact = 0 },
    @{ Dpi = [uint32]192; Width = 1240; Height = 984; Compact = 1 }
)) {
    $placement = Invoke-MonitorPlacementForSize `
        -WorkLeft 0 `
        -WorkTop 0 `
        -WorkRight 1920 `
        -WorkBottom 1080 `
        -DpiX $centerCase.Dpi `
        -DpiY $centerCase.Dpi `
        -CurrentLeft 0 `
        -CurrentTop 0 `
        -Center $true `
        -LayoutWidthDip 620 `
        -LayoutHeightDip 552
    if ($placement[2] -ne $centerCase.Width -or
        $placement[3] -ne $centerCase.Height -or
        $placement[4] -ne $centerCase.Compact) {
        throw (
            "620 DIP Center placement at $($centerCase.Dpi) DPI was " +
            "$($placement -join ', ').")
    }
    $widthDip = $placement[2] * 96.0 / $centerCase.Dpi
    $heightDip = $placement[3] * 96.0 / $centerCase.Dpi
    $expectedHeightDip = if ($centerCase.Compact) { 492.0 } else { 552.0 }
    if ([Math]::Abs($widthDip - 620.0) -gt 0.001 -or
        [Math]::Abs($heightDip - $expectedHeightDip) -gt 0.001) {
        throw "Boost Center placement introduced fractional DIP scaling at $($centerCase.Dpi) DPI."
    }
}

# A genuinely constrained monitor must use all safe horizontal room for the
# Center, remain inside the work area and still expand beyond the 460 DIP main
# surface. This deterministic calculation covers production placement without
# depending on the CI runner's maximum-track-size policy.
$constrainedCenter = Invoke-MonitorPlacementForSize `
    -WorkLeft 0 `
    -WorkTop 0 `
    -WorkRight 580 `
    -WorkBottom 800 `
    -DpiX 96 `
    -DpiY 96 `
    -CurrentLeft 900 `
    -CurrentTop -50 `
    -Center $true `
    -LayoutWidthDip 620 `
    -LayoutHeightDip 552
if ($constrainedCenter[0] -ne 8 -or
    $constrainedCenter[1] -ne 176 -or
    $constrainedCenter[2] -ne 564 -or
    $constrainedCenter[3] -ne 447 -or
    $constrainedCenter[4] -ne 1) {
    throw (
        'Constrained Center did not use the maximal 8 DIP safe work-area ' +
        "width: $($constrainedCenter -join ', ').")
}
if ($constrainedCenter[2] -le 460 -or
    $constrainedCenter[0] -lt 0 -or
    ($constrainedCenter[0] + $constrainedCenter[2]) -gt 580) {
    throw 'Constrained Center stayed at main width or escaped its work area.'
}

foreach ($workBottom in @(1040, 1032)) {
    $placement = Invoke-MonitorPlacement `
        -WorkLeft 0 `
        -WorkTop 0 `
        -WorkRight 1920 `
        -WorkBottom $workBottom `
        -DpiX 192 `
        -DpiY 192 `
        -CurrentLeft 0 `
        -CurrentTop 0 `
        -Center $true
    if ($placement[2] -ne 920 -or
        $placement[3] -ne 984 -or
        $placement[4] -ne 1) {
        throw (
            "1920x$workBottom work area at 200% did not preserve the " +
            "460x492 DIP compact surface: $($placement -join ', ').")
    }
    if ([Math]::Abs(($placement[2] * 0.5) - 460.0) -gt 0.001 -or
        [Math]::Abs(($placement[3] * 0.5) - 492.0) -gt 0.001) {
        throw "1920x$workBottom work area introduced fractional compact scaling."
    }
}

$mixedDpi = Invoke-MonitorPlacement `
    -WorkLeft -2560 `
    -WorkTop 0 `
    -WorkRight 0 `
    -WorkBottom 1440 `
    -DpiX 144 `
    -DpiY 144 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($mixedDpi[0] -ne -1625 -or
    $mixedDpi[1] -ne 306 -or
    $mixedDpi[2] -ne 690 -or
    $mixedDpi[3] -ne 828) {
    throw "150% negative-coordinate monitor placement was incorrect: $($mixedDpi -join ', ')."
}

$standardDpi = Invoke-MonitorPlacement `
    -WorkLeft 0 `
    -WorkTop 0 `
    -WorkRight 1920 `
    -WorkBottom 1080 `
    -DpiX 96 `
    -DpiY 96 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($standardDpi[0] -ne 730 -or
    $standardDpi[1] -ne 264 -or
    $standardDpi[2] -ne 460 -or
    $standardDpi[3] -ne 552) {
    throw "96 DPI monitor placement was incorrect: $($standardDpi -join ', ')."
}

$highDpi = Invoke-MonitorPlacement `
    -WorkLeft 0 `
    -WorkTop 0 `
    -WorkRight 2560 `
    -WorkBottom 1440 `
    -DpiX 192 `
    -DpiY 192 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($highDpi[0] -ne 820 -or
    $highDpi[1] -ne 168 -or
    $highDpi[2] -ne 920 -or
    $highDpi[3] -ne 1104) {
    throw "200% monitor placement was incorrect: $($highDpi -join ', ')."
}

$constrainedDpi = Invoke-MonitorPlacement `
    -WorkLeft 1920 `
    -WorkTop 0 `
    -WorkRight 2520 `
    -WorkBottom 700 `
    -DpiX 144 `
    -DpiY 144 `
    -CurrentLeft 5000 `
    -CurrentTop -100 `
    -Center $false
if ($constrainedDpi[0] -ne 1932 -or
    $constrainedDpi[1] -ne 12 -or
    $constrainedDpi[2] -ne 576 -or
    $constrainedDpi[3] -ne 616 -or
    $constrainedDpi[4] -ne 1) {
    throw "Constrained 150% monitor placement was incorrect: $($constrainedDpi -join ', ')."
}

$tinyWorkArea = Invoke-MonitorPlacement `
    -WorkLeft -300 `
    -WorkTop 100 `
    -WorkRight 0 `
    -WorkBottom 300 `
    -DpiX 192 `
    -DpiY 192 `
    -CurrentLeft 9999 `
    -CurrentTop -999 `
    -Center $false
if ($tinyWorkArea[0] -ne -173 -or
    $tinyWorkArea[1] -ne 116 -or
    $tinyWorkArea[2] -ne 157 -or
    $tinyWorkArea[3] -ne 168 -or
    $tinyWorkArea[4] -ne 1) {
    throw "Tiny 200% work-area clamping was incorrect: $($tinyWorkArea -join ', ')."
}

$fallbackDpi = Invoke-MonitorPlacement `
    -WorkLeft 0 `
    -WorkTop 0 `
    -WorkRight 1920 `
    -WorkBottom 1080 `
    -DpiX 0 `
    -DpiY 900 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($fallbackDpi[2] -ne 460 -or
    $fallbackDpi[3] -ne 552) {
    throw 'Invalid monitor DPI did not safely fall back to 96 DPI.'
}

function Wait-ForWindow {
    param(
        [Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Boostix exited unexpectedly with code $($Process.ExitCode)."
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [Windows.Automation.AutomationElement]::FromHandle(
                $Process.MainWindowHandle)
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'Boostix did not expose a top-level window.'
}

function Find-ById {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutMilliseconds = 10000,
        [switch]$AllowDisabled
    )

    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst(
            [Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($element -and
            ($AllowDisabled -or $element.Current.IsEnabled) -and
            -not $element.Current.IsOffscreen) {
            return $element
        }
        Start-Sleep -Milliseconds 25
    }
    throw "UI element did not appear: $AutomationId"
}

function Assert-ElementAbsent {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $element = $Root.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($element -and -not $element.Current.IsOffscreen) {
        throw "Obsolete main-window element is still exposed: $AutomationId"
    }
}

function Invoke-Element {
    param([Windows.Automation.AutomationElement]$Element)

    $pattern = $Element.GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern)
    ([Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Get-NormalizedBounds {
    param(
        [Windows.Automation.AutomationElement]$Element,
        [Windows.Automation.AutomationElement]$Window,
        [double]$DesignWidth = 460.0
    )

    $windowBounds = $Window.Current.BoundingRectangle
    $bounds = $Element.Current.BoundingRectangle
    $factor = $DesignWidth / $windowBounds.Width
    return [pscustomobject]@{
        Left = ($bounds.Left - $windowBounds.Left) * $factor
        Top = ($bounds.Top - $windowBounds.Top) * $factor
        Width = $bounds.Width * $factor
        Height = $bounds.Height * $factor
        RightInset = ($windowBounds.Right - $bounds.Right) * $factor
        BottomInset = ($windowBounds.Bottom - $bounds.Bottom) * $factor
    }
}

function Assert-CenterTabContract {
    param([Windows.Automation.AutomationElement]$Element)

    Assert-KeyboardFocusable $Element 'Boost Center tab'
    if ($Element.Current.ControlType -ne
        [Windows.Automation.ControlType]::TabItem) {
        throw "Boost Center tab is not exposed as UIA TabItem: $($Element.Current.AutomationId)"
    }
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        throw "Boost Center tab lacks SelectionItemPattern: $($Element.Current.AutomationId)"
    }
}

function Select-CenterTab {
    param([Windows.Automation.AutomationElement]$Element)

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        throw "Boost Center tab lacks SelectionItemPattern: $($Element.Current.AutomationId)"
    }
    ([Windows.Automation.SelectionItemPattern]$pattern).Select()
}

function Assert-Between {
    param(
        [double]$Actual,
        [double]$Minimum,
        [double]$Maximum,
        [string]$Description
    )

    if ($Actual -lt $Minimum -or $Actual -gt $Maximum) {
        throw "$Description was $Actual; expected $Minimum..$Maximum DIP."
    }
}

function Assert-KeyboardFocusable {
    param(
        [Windows.Automation.AutomationElement]$Element,
        [string]$Description
    )

    if (-not $Element.Current.IsKeyboardFocusable) {
        throw "$Description is missing keyboard focus support."
    }
    if ([string]::IsNullOrWhiteSpace($Element.Current.Name)) {
        throw "$Description is missing an accessible name."
    }
}

function Get-ExpandedCenterWidthContract {
    param(
        [double]$MainWidthPixels,
        [double]$WorkAreaWidthPixels
    )

    if ($MainWidthPixels -le 0 -or $WorkAreaWidthPixels -le 0) {
        throw 'Center width contract requires positive main/work-area widths.'
    }
    $pixelsPerDip = $MainWidthPixels / 460.0
    $workAreaWidthDip = $WorkAreaWidthPixels / $pixelsPerDip
    $safeWorkAreaWidthDip = $workAreaWidthDip - 16.0
    if ($safeWorkAreaWidthDip -ge 620.0) {
        return [pscustomobject]@{
            Exact = $true
            Minimum = 617.0
            Maximum = 623.0
            WorkAreaWidthDip = $workAreaWidthDip
        }
    }

    return [pscustomobject]@{
        Exact = $false
        Minimum = [Math]::Max(
            461.0,
            [Math]::Min(617.0, $workAreaWidthDip - 16.0))
        # Maximum-track bounds may include about 12 DIP of invisible
        # non-client width beyond the visible work area.
        Maximum = [Math]::Min(623.0, $workAreaWidthDip + 12.0)
        WorkAreaWidthDip = $workAreaWidthDip
    }
}

# Exercise the dynamic CI branch deterministically even on a wide developer
# desktop. These are the 1024 px runner constraints at 175% and 200%.
foreach ($constrainedContractCase in @(
    @{ Scale = 1.75; Main = 805.0; Center = 1044.0; Work = 1024.0 },
    @{ Scale = 2.0; Main = 920.0; Center = 1044.0; Work = 1024.0 }
)) {
    $contract = Get-ExpandedCenterWidthContract `
        -MainWidthPixels $constrainedContractCase.Main `
        -WorkAreaWidthPixels $constrainedContractCase.Work
    $actualDip = $constrainedContractCase.Center /
        ($constrainedContractCase.Main / 460.0)
    if ($contract.Exact -or
        $actualDip -lt $contract.Minimum -or
        $actualDip -gt $contract.Maximum -or
        460.0 -ge $contract.Minimum) {
        throw (
            "Deterministic constrained Center contract at scale " +
            "$($constrainedContractCase.Scale) rejected $actualDip DIP or " +
            'would accept an unchanged 460 DIP window.')
    }
}
$wideContract = Get-ExpandedCenterWidthContract `
    -MainWidthPixels 920.0 `
    -WorkAreaWidthPixels 1920.0
if (-not $wideContract.Exact -or
    $wideContract.Minimum -ne 617.0 -or
    $wideContract.Maximum -ne 623.0) {
    throw 'A wide 200% work area no longer requires an exact 620 DIP Center.'
}

function Wait-ForFocusedId {
    param(
        [string]$AutomationId,
        [int]$TimeoutMilliseconds
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $focused = [Windows.Automation.AutomationElement]::FocusedElement
        if ($focused -and
            $focused.Current.AutomationId -ceq $AutomationId) {
            return
        }
        Start-Sleep -Milliseconds 10
    }
    $actual = [Windows.Automation.AutomationElement]::FocusedElement
    $actualId = if ($actual) { $actual.Current.AutomationId } else { '<none>' }
    throw "Keyboard focus was '$actualId', expected '$AutomationId'."
}

$scales = @(1.0, 1.25, 1.5, 1.75, 2.0)
$results = @()
foreach ($scale in $scales) {
    $process = $null
    try {
        $scaleArgument = '--demo-ui-scale=' +
            $scale.ToString([Globalization.CultureInfo]::InvariantCulture)
        $process = Start-Process `
            -FilePath $ApplicationPath `
            -ArgumentList '--skip-setup', '--demo', $scaleArgument, $testInstanceArgument `
            -PassThru
        $window = Wait-ForWindow -Process $process -TimeoutMilliseconds 15000

        $windowBounds = $window.Current.BoundingRectangle
        $centerPoint = New-Object Drawing.Point(
            [int](($windowBounds.Left + $windowBounds.Right) / 2),
            [int](($windowBounds.Top + $windowBounds.Bottom) / 2))
        $workArea = [Windows.Forms.Screen]::FromPoint(
            $centerPoint).WorkingArea
        $aspect = $windowBounds.Height / $windowBounds.Width
        Assert-Between `
            $aspect `
            1.18 `
            1.22 `
            "Window aspect at $scale scale ($($windowBounds.Width)x$($windowBounds.Height))"

        $gear = Find-ById $window 'Boostix.OpenCenter' 15000
        $version = Find-ById $window 'Boostix.Version'
        $minimize = Find-ById $window 'Boostix.Minimize'
        $close = Find-ById $window 'Boostix.Close'
        $boost = Find-ById $window 'Boostix.Activate' 20000
        $target = Find-ById $window 'Boostix.Target.Select'
        $watermark = Find-ById $window 'Boostix.Watermark' 10000 -AllowDisabled

        foreach ($obsoleteId in @(
            'Boostix.Keep.DISCORD',
            'Boostix.Keep.EPICGAMES',
            'Boostix.Keep.STEAM')) {
            Assert-ElementAbsent $window $obsoleteId
        }

        foreach ($pair in @(
            @($gear, 'Settings button'),
            @($boost, 'Boost button'),
            @($target, 'Exact game target selector'),
            @($minimize, 'Minimize button'),
            @($close, 'Close button'))) {
            Assert-KeyboardFocusable $pair[0] $pair[1]
        }

        foreach ($chrome in @($gear, $minimize, $close)) {
            $chromeBounds = Get-NormalizedBounds $chrome $window
            Assert-Between $chromeBounds.Width 30.5 33.5 'Title control width'
            Assert-Between $chromeBounds.Height 30.5 33.5 'Title control height'
            Assert-Between $chromeBounds.Top 8.5 13.5 'Title control top inset'
        }

        $gearBounds = Get-NormalizedBounds $gear $window
        $closeBounds = Get-NormalizedBounds $close $window
        Assert-Between $gearBounds.Left 8.5 13.5 'Settings button left inset'
        Assert-Between $closeBounds.RightInset 8.5 13.5 'Close button right inset'

        $versionBounds = Get-NormalizedBounds $version $window
        $minimizeBounds = Get-NormalizedBounds $minimize $window
        if ($versionBounds.Left + $versionBounds.Width -gt
            $minimizeBounds.Left + 0.5) {
            throw 'Version text overlaps the minimize button.'
        }

        $targetBounds = Get-NormalizedBounds $target $window
        Assert-Between $targetBounds.Height 42.5 45.5 'Exact target selector height'
        if ($targetBounds.Left -lt 52 -or $targetBounds.RightInset -lt 52) {
            throw (
                "Exact target selector left its centered safe area at scale $scale " +
                "(left $($targetBounds.Left), right $($targetBounds.RightInset) DIP).")
        }
        if ($target.Current.ControlType -ne
            [Windows.Automation.ControlType]::Button) {
            throw 'Exact game target selector is not exposed as a UIA button.'
        }
        if ([string]::IsNullOrWhiteSpace($target.Current.HelpText) -or
            $target.Current.HelpText -notmatch 'EXE') {
            throw 'Exact game target selector does not explain its executable binding.'
        }
        $watermarkBounds = Get-NormalizedBounds $watermark $window
        if ($watermarkBounds.RightInset -lt 22.5 -or
            $watermarkBounds.BottomInset -lt 22.5) {
            throw (
                "Watermark left its 24 DIP footer safe area at scale $scale " +
                "(right $($watermarkBounds.RightInset), bottom $($watermarkBounds.BottomInset) DIP).")
        }

        # Verify the explicit tab order once; scaling does not change focus order.
        if ([Math]::Abs($scale - 1.0) -lt 0.001) {
            if ($windowBounds.Left -lt $workArea.Left -or
                $windowBounds.Top -lt $workArea.Top -or
                $windowBounds.Right -gt $workArea.Right -or
                $windowBounds.Bottom -gt $workArea.Bottom) {
                throw 'The production-size window is not contained in its target monitor work area.'
            }
            $gear.SetFocus()
            Wait-ForFocusedId 'Boostix.OpenCenter' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'Boostix.Activate' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'Boostix.Target.Select' 1000
        }

        $mainPixelsPerDip = $windowBounds.Width / 460.0
        Invoke-Element $gear
        $readiness = Find-ById $window 'Boostix.Center.Tab.Readiness'
        $impact = Find-ById $window 'Boostix.Center.Tab.Impact'
        $report = Find-ById $window 'Boostix.Center.Tab.Report'
        $profiles = Find-ById $window 'Boostix.Center.Tab.Profiles'
        $settings = Find-ById $window 'Boostix.Center.Tab.Settings'
        foreach ($tab in @($readiness, $impact, $report, $profiles, $settings)) {
            Assert-CenterTabContract $tab
            $tabBounds = Get-NormalizedBounds $tab $window 620.0
            Assert-Between $tabBounds.Height 38 45.5 'Boost Center tab height'
        }
        $centerWindowBounds = $window.Current.BoundingRectangle
        $centerWidthDip = $centerWindowBounds.Width / $mainPixelsPerDip
        $centerWidthContract = Get-ExpandedCenterWidthContract `
            -MainWidthPixels $windowBounds.Width `
            -WorkAreaWidthPixels $workArea.Width
        if ($centerWidthContract.Exact) {
            if ($centerWidthDip -lt 617.0 -or $centerWidthDip -gt 623.0) {
                throw (
                    "Expanded Boost Center width at scale $scale was " +
                    "$centerWidthDip DIP; work area " +
                    "$($centerWidthContract.WorkAreaWidthDip) DIP " +
                    'fits the desired 620 DIP width plus safe insets.')
            }
        }
        else {
            # Windows can expose an outer maximum-track rectangle roughly
            # 12 DIP wider than the visible work area. Accept that non-client
            # envelope, but require near-maximal safe expansion and explicitly
            # reject an unchanged 460 DIP main window.
            if ($centerWidthDip -lt $centerWidthContract.Minimum -or
                $centerWidthDip -gt $centerWidthContract.Maximum -or
                $centerWindowBounds.Width -le ($windowBounds.Width + 1.0)) {
                throw (
                    "Constrained Boost Center width at scale $scale was " +
                    "$centerWidthDip DIP; expected maximal safe expansion " +
                    "$($centerWidthContract.Minimum).." +
                    "$($centerWidthContract.Maximum) DIP for work area " +
                    "$($centerWidthContract.WorkAreaWidthDip) DIP, and greater than " +
                    'the 460 DIP main width.')
            }
        }

        $centerTarget = Find-ById $window 'Boostix.Center.SelectTarget'
        $centerTargetBounds = Get-NormalizedBounds $centerTarget $window 620.0
        Assert-Between $centerTargetBounds.Height 38 42 'Boost Center target action height'
        if ($centerTargetBounds.Left -lt 24 -or $centerTargetBounds.RightInset -lt 24) {
            throw 'Boost Center exact-target action is clipped.'
        }

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        Select-CenterTab $report
        if ([System.Windows.SystemParameters]::ClientAreaAnimation) {
            Wait-ForFocusedId 'Boostix.Center.ReportBenchmark' 1500
            $stopwatch.Stop()
            Assert-Between `
                $stopwatch.Elapsed.TotalMilliseconds `
                180 `
                900 `
                'Directional report transition'
        }

        Select-CenterTab $settings
        $setting = Find-ById $window 'Boostix.Center.Setting.0'
        # The new page enters from the right by design. Measure the permanent
        # scroll/toggle gutter only after the directional transition settles.
        Start-Sleep -Milliseconds ($transitionMilliseconds + 40)
        $settingBounds = Get-NormalizedBounds $setting $window 620.0
        if ($settingBounds.RightInset -lt 36) {
            throw "Boost Center switch entered the scroll/toggle safe gutter."
        }
        Assert-KeyboardFocusable $setting 'Boost Center setting switch'
        $oldReportCondition = New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::AutomationIdProperty,
            'Boostix.Center.ReportBenchmark')
        $oldReportButton = $window.FindFirst(
            [Windows.Automation.TreeScope]::Descendants,
            $oldReportCondition)
        if ($oldReportButton -and -not $oldReportButton.Current.IsOffscreen) {
            throw 'Report and settings content overlap after the directional transition.'
        }

        $restore = Find-ById $window 'Boostix.Center.Restore'
        $restoreBounds = Get-NormalizedBounds $restore $window 620.0
        $watermarkBounds = Get-NormalizedBounds $watermark $window 620.0
        $horizontalOverlap =
            $restoreBounds.Left -lt ($watermarkBounds.Left + $watermarkBounds.Width) -and
            ($restoreBounds.Left + $restoreBounds.Width) -gt $watermarkBounds.Left
        $verticalOverlap =
            $restoreBounds.Top -lt ($watermarkBounds.Top + $watermarkBounds.Height) -and
            ($restoreBounds.Top + $restoreBounds.Height) -gt $watermarkBounds.Top
        if ($horizontalOverlap -and $verticalOverlap) {
            throw 'Boost Center footer action overlaps the permanent watermark.'
        }

        $results += '{0}%={1:N0}x{2:N0}' -f `
            [int]($scale * 100),
            $windowBounds.Width,
            $windowBounds.Height
    }
    finally {
        if ($process -and -not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

$compactProcess = $null
try {
    $compactProcess = Start-Process `
        -FilePath $ApplicationPath `
        -ArgumentList '--skip-setup', '--demo', '--demo-compact', $testInstanceArgument `
        -PassThru
    $compactWindow = Wait-ForWindow `
        -Process $compactProcess `
        -TimeoutMilliseconds 15000
    $compactBounds = $compactWindow.Current.BoundingRectangle
    $compactHeightDip = $compactBounds.Height * (460.0 / $compactBounds.Width)
    if ($compactHeightDip -gt 500) {
        throw "Compact layout height was $compactHeightDip; expected at most 500 DIP."
    }
    $compactTarget = Find-ById `
        $compactWindow `
        'Boostix.Target.Select' `
        15000
    $compactTargetBounds = Get-NormalizedBounds `
        $compactTarget `
        $compactWindow
    if ($compactTargetBounds.BottomInset -lt 62) {
        throw 'Compact layout clipped the exact game target selector or live metrics.'
    }
    $compactWatermark = Find-ById `
        $compactWindow `
        'Boostix.Watermark' `
        10000 `
        -AllowDisabled
    $compactWatermarkBounds = Get-NormalizedBounds `
        $compactWatermark `
        $compactWindow
    if ($compactWatermarkBounds.RightInset -lt 22.5 -or
        $compactWatermarkBounds.BottomInset -lt 22.5) {
        throw 'Compact layout moved the watermark outside its 24 DIP footer safe area.'
    }
    $compactHorizontalOverlap =
        $compactTargetBounds.Left -lt
            ($compactWatermarkBounds.Left + $compactWatermarkBounds.Width) -and
        ($compactTargetBounds.Left + $compactTargetBounds.Width) -gt
            $compactWatermarkBounds.Left
    $compactVerticalOverlap =
        $compactTargetBounds.Top -lt
            ($compactWatermarkBounds.Top + $compactWatermarkBounds.Height) -and
        ($compactTargetBounds.Top + $compactTargetBounds.Height) -gt
            $compactWatermarkBounds.Top
    if ($compactHorizontalOverlap -and $compactVerticalOverlap) {
        throw 'Compact layout overlaps the exact game target selector and watermark.'
    }
}
finally {
    if ($compactProcess -and -not $compactProcess.HasExited) {
        [void]$compactProcess.CloseMainWindow()
        if (-not $compactProcess.WaitForExit(3000)) {
            Stop-Process `
                -Id $compactProcess.Id `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

$ultraProcess = $null
try {
    $ultraProcess = Start-Process `
        -FilePath $ApplicationPath `
        -ArgumentList '--skip-setup', '--demo', '--demo-ultra-compact', $testInstanceArgument `
        -PassThru
    $ultraWindow = Wait-ForWindow `
        -Process $ultraProcess `
        -TimeoutMilliseconds 15000
    $ultraBounds = $ultraWindow.Current.BoundingRectangle
    $ultraHeightDip = $ultraBounds.Height * (460.0 / $ultraBounds.Width)
    if ($ultraHeightDip -gt 368) {
        throw "Ultra-compact layout height was $ultraHeightDip; expected at most 368 DIP."
    }
    foreach ($automationId in @(
        'Boostix.OpenCenter',
        'Boostix.Activate',
        'Boostix.Target.Select',
        'Boostix.Close')) {
        $element = Find-ById $ultraWindow $automationId 15000
        $bounds = $element.Current.BoundingRectangle
        if ($bounds.Left -lt $ultraBounds.Left -or
            $bounds.Top -lt $ultraBounds.Top -or
            $bounds.Right -gt $ultraBounds.Right -or
            $bounds.Bottom -gt $ultraBounds.Bottom) {
            throw "Ultra-compact layout clipped $automationId."
        }
    }
}
finally {
    if ($ultraProcess -and -not $ultraProcess.HasExited) {
        [void]$ultraProcess.CloseMainWindow()
        if (-not $ultraProcess.WaitForExit(3000)) {
            Stop-Process `
                -Id $ultraProcess.Id `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

$overlaySource = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs')
if ($overlaySource -notmatch 'SystemParameters\.ClientAreaAnimation') {
    throw 'Boost Center transition no longer respects the Windows animation preference.'
}
$programSource = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'Boostix\Program.cs')
if ($programSource -notmatch 'BuildKeyboardFocusVisualStyle' -or
    $programSource -notmatch 'SystemParameters\.FocusVisualStyleKey' -or
    $programSource -notmatch 'MonitorFromWindow\(' -or
    $programSource -notmatch 'GetMonitorInfo\(' -or
    $programSource -notmatch 'GetDpiForWindow\(' -or
    $programSource -notmatch 'WmDpiChanged' -or
    $programSource -notmatch 'LocationChanged \+=' -or
    $programSource -notmatch 'TransformFromDevice' -or
    $programSource -notmatch 'CalculateMonitorPlacement\(' -or
    $programSource -notmatch 'compactMainLayout' -or
    $programSource -notmatch 'SetCompactMainLayout' -or
    $programSource -notmatch 'CompactWindowHeight' -or
    $programSource -notmatch 'scaleMainLayoutToWorkArea') {
    throw 'Per-monitor DPI/work-area layout or the Boostix keyboard focus visual is missing.'
}
if ($programSource -match 'SystemParameters\.WorkArea') {
    throw 'Main-window placement regressed to the primary-monitor SystemParameters.WorkArea.'
}
$manifest = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'Boostix\app.manifest')
if ($manifest -notmatch '<dpiAwareness[^>]*>PerMonitorV2</dpiAwareness>') {
    throw 'The app manifest no longer declares PerMonitorV2 awareness.'
}
foreach ($sourceText in @(
    $programSource,
    $overlaySource,
    (Get-Content -Raw -Encoding UTF8 (
        Join-Path $projectRoot 'Boostix\OptimizationFlow.cs')),
    (Get-Content -Raw -Encoding UTF8 (
        Join-Path $projectRoot 'Boostix\UpdateFlow.cs')))) {
    if ($sourceText -match 'FocusVisualStyle\s*=\s*null') {
        throw 'A keyboard-focusable application control still suppresses its focus visual.'
    }
}

foreach ($motionSourcePath in @(
    (Join-Path $projectRoot 'Boostix\OptimizationFlow.cs'),
    (Join-Path $projectRoot 'Boostix\UpdateFlow.cs')
)) {
    $motionSource = Get-Content -Raw -Encoding UTF8 $motionSourcePath
    $motionChecks = [regex]::Matches(
        $motionSource,
        'SystemParameters\.ClientAreaAnimation').Count
    if ($motionChecks -lt 3) {
        throw "Reduced-motion handling is incomplete in $motionSourcePath."
    }
}

"Responsive layout test passed: $($results -join ', '); 460 DIP main and exact 620 DIP Center when the work area fits (maximal safe expansion otherwise) remain unclipped at 1920x1080 100/125/150/175/200%, with exact-target focus order, UIA tabs, mixed-DPI compact reflow, reduced motion, gutters and $transitionMilliseconds ms motion verified."
