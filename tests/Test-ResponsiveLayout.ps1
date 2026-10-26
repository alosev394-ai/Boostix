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

# The shell is one fixed 880x560 DIP design surface: a 216 DIP navigation
# sidebar and a 664 DIP content column with 28 DIP insets. Work areas that
# cannot hold it at 1:1 receive a uniformly down-scaled window.
$designWidth = 880.0
$designHeight = 560.0
$sidebarWidth = 216.0
$contentInset = 28.0

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
    throw 'Compiled app does not expose the size-aware placement calculation.'
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
    if ($adaptiveViewbox.StretchDirection -ne
        [Windows.Controls.StretchDirection]::DownOnly) {
        throw 'The adaptive Viewbox may upscale the design surface.'
    }

    $adaptiveViewbox.Measure([Windows.Size]::new($designWidth, $designHeight))
    $adaptiveViewbox.Arrange([Windows.Rect]::new(0.0, 0.0, $designWidth, $designHeight))
    $adaptiveViewbox.UpdateLayout()

    $designSurface = $adaptiveViewbox.Child
    if ([Math]::Abs($designSurface.DesiredSize.Width - $designWidth) -gt 0.001 -or
        [Math]::Abs($designSurface.DesiredSize.Height - $designHeight) -gt 0.001) {
        throw "Production design surface measured $($designSurface.DesiredSize); expected 880x560 DIP."
    }
    $adaptiveMatrix = $designSurface.TransformToAncestor($adaptiveViewbox).Value
    if ([Math]::Abs($adaptiveMatrix.M11 - 1.0) -gt 0.001 -or
        [Math]::Abs($adaptiveMatrix.M22 - 1.0) -gt 0.001) {
        throw "Production UI is resampled at $($adaptiveMatrix.M11)x$($adaptiveMatrix.M22) when it fits; expected a crisp 1.0x transform."
    }

    # A smaller host scales the whole surface uniformly, never reflows it.
    $adaptiveViewbox.Measure([Windows.Size]::new(792.0, 504.0))
    $adaptiveViewbox.Arrange([Windows.Rect]::new(0.0, 0.0, 792.0, 504.0))
    $adaptiveViewbox.UpdateLayout()
    $scaledMatrix = $designSurface.TransformToAncestor($adaptiveViewbox).Value
    if ([Math]::Abs($scaledMatrix.M11 - 0.9) -gt 0.001 -or
        [Math]::Abs($scaledMatrix.M22 - 0.9) -gt 0.001) {
        throw "A constrained host scaled the surface to $($scaledMatrix.M11)x$($scaledMatrix.M22); expected a uniform 0.9x."
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

function Assert-Placement {
    param(
        [int[]]$Actual,
        [int[]]$Expected,
        [string]$Description
    )
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Actual[$index] -ne $Expected[$index]) {
            throw "$Description was $($Actual -join ', '); expected $($Expected -join ', ')."
        }
    }
}

# 1920x1080 desktops: crisp 880x560 DIP up to 175%; 200% scales uniformly.
foreach ($desktopCase in @(
    @{ Dpi = [uint32]96; Expected = @(520, 260, 880, 560, 0) },
    @{ Dpi = [uint32]120; Expected = @(410, 190, 1100, 700, 0) },
    @{ Dpi = [uint32]144; Expected = @(300, 120, 1320, 840, 0) },
    @{ Dpi = [uint32]168; Expected = @(190, 50, 1540, 980, 0) },
    @{ Dpi = [uint32]192; Expected = @(137, 16, 1646, 1048, 1) }
)) {
    $placement = Invoke-MonitorPlacement `
        -WorkLeft 0 -WorkTop 0 -WorkRight 1920 -WorkBottom 1080 `
        -DpiX $desktopCase.Dpi -DpiY $desktopCase.Dpi `
        -CurrentLeft 0 -CurrentTop 0 -Center $true
    Assert-Placement $placement $desktopCase.Expected "1920x1080 placement at $($desktopCase.Dpi) DPI"
    if ($placement[4] -eq 0) {
        $widthDip = $placement[2] * 96.0 / $desktopCase.Dpi
        $heightDip = $placement[3] * 96.0 / $desktopCase.Dpi
        if ([Math]::Abs($widthDip - $designWidth) -gt 0.001 -or
            [Math]::Abs($heightDip - $designHeight) -gt 0.001) {
            throw "1920x1080 placement introduced fractional DIP scaling at $($desktopCase.Dpi) DPI."
        }
    }
    else {
        $aspect = $placement[2] / [double]$placement[3]
        if ([Math]::Abs($aspect - ($designWidth / $designHeight)) -gt 0.01) {
            throw "Scaled placement at $($desktopCase.Dpi) DPI is not uniform: $($placement -join ', ')."
        }
    }
}

$forSize = [int[]]$placementForSizeMethod.Invoke(
    $null,
    [object[]]@(0, 0, 1920, 1080, [uint32]144, [uint32]144, 0, 0, $true, $designWidth, $designHeight))
Assert-Placement $forSize @(300, 120, 1320, 840, 0) 'Size-aware placement of the fixed shell'

foreach ($workCase in @(
    @{ Bottom = 1040; Expected = @(168, 16, 1584, 1008, 1) },
    @{ Bottom = 1032; Expected = @(174, 16, 1571, 1000, 1) }
)) {
    $placement = Invoke-MonitorPlacement `
        -WorkLeft 0 -WorkTop 0 -WorkRight 1920 -WorkBottom $workCase.Bottom `
        -DpiX 192 -DpiY 192 -CurrentLeft 0 -CurrentTop 0 -Center $true
    Assert-Placement $placement $workCase.Expected "1920x$($workCase.Bottom) work area at 200%"
    if (($placement[1] + $placement[3]) -gt ($workCase.Bottom - 16)) {
        throw "1920x$($workCase.Bottom) placement left the 8 DIP work-area safety inset."
    }
}

Assert-Placement (Invoke-MonitorPlacement `
    -WorkLeft -2560 -WorkTop 0 -WorkRight 0 -WorkBottom 1440 `
    -DpiX 144 -DpiY 144 -CurrentLeft 0 -CurrentTop 0 -Center $true) `
    @(-1940, 300, 1320, 840, 0) '150% negative-coordinate monitor placement'
Assert-Placement (Invoke-MonitorPlacement `
    -WorkLeft 0 -WorkTop 0 -WorkRight 2560 -WorkBottom 1440 `
    -DpiX 192 -DpiY 192 -CurrentLeft 0 -CurrentTop 0 -Center $true) `
    @(400, 160, 1760, 1120, 0) '200% QHD monitor placement'
Assert-Placement (Invoke-MonitorPlacement `
    -WorkLeft 0 -WorkTop 0 -WorkRight 1366 -WorkBottom 728 `
    -DpiX 120 -DpiY 120 -CurrentLeft 0 -CurrentTop 0 -Center $true) `
    @(133, 14, 1100, 700, 0) '1366x768 laptop at 125%'
Assert-Placement (Invoke-MonitorPlacement `
    -WorkLeft 1920 -WorkTop 0 -WorkRight 2520 -WorkBottom 700 `
    -DpiX 144 -DpiY 144 -CurrentLeft 5000 -CurrentTop -100 -Center $false) `
    @(1932, 12, 576, 366, 1) 'Constrained 150% monitor placement'
Assert-Placement (Invoke-MonitorPlacement `
    -WorkLeft -300 -WorkTop 100 -WorkRight 0 -WorkBottom 300 `
    -DpiX 192 -DpiY 192 -CurrentLeft 9999 -CurrentTop -999 -Center $false) `
    @(-280, 116, 264, 168, 1) 'Tiny 200% work-area clamping'
Assert-Placement (Invoke-MonitorPlacement `
    -WorkLeft 0 -WorkTop 0 -WorkRight 1920 -WorkBottom 1080 `
    -DpiX 0 -DpiY 900 -CurrentLeft 0 -CurrentTop 0 -Center $true) `
    @(520, 260, 880, 560, 0) 'Invalid monitor DPI fallback to 96 DPI'

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
        throw "Obsolete element is still exposed: $AutomationId"
    }
}

function Get-NormalizedBounds {
    param(
        [Windows.Automation.AutomationElement]$Element,
        [Windows.Automation.AutomationElement]$Window
    )

    # The whole surface scales uniformly, so window width maps pixels to DIP.
    $windowBounds = $Window.Current.BoundingRectangle
    $bounds = $Element.Current.BoundingRectangle
    $factor = $designWidth / $windowBounds.Width
    return [pscustomobject]@{
        Left = ($bounds.Left - $windowBounds.Left) * $factor
        Top = ($bounds.Top - $windowBounds.Top) * $factor
        Width = $bounds.Width * $factor
        Height = $bounds.Height * $factor
        Right = ($bounds.Right - $windowBounds.Left) * $factor
        Bottom = ($bounds.Bottom - $windowBounds.Top) * $factor
        RightInset = ($windowBounds.Right - $bounds.Right) * $factor
        BottomInset = ($windowBounds.Bottom - $bounds.Bottom) * $factor
    }
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

function Assert-InsideWindow {
    param(
        [Windows.Automation.AutomationElement]$Element,
        [Windows.Automation.AutomationElement]$Window,
        [string]$Description
    )
    $bounds = Get-NormalizedBounds $Element $Window
    if ($bounds.Left -lt -0.5 -or $bounds.Top -lt -0.5 -or
        $bounds.RightInset -lt -0.5 -or $bounds.BottomInset -lt -0.5) {
        throw "$Description is clipped by the window."
    }
}

function Assert-NoOverlap {
    param(
        [Windows.Automation.AutomationElement]$First,
        [Windows.Automation.AutomationElement]$Second,
        [string]$Description
    )
    # Overlap is coordinate-system invariant; compare raw UIA rectangles.
    $a = $First.Current.BoundingRectangle
    $b = $Second.Current.BoundingRectangle
    $horizontal = $a.Left -lt ($b.Right - 0.5) -and $a.Right -gt ($b.Left + 0.5)
    $vertical = $a.Top -lt ($b.Bottom - 0.5) -and $a.Bottom -gt ($b.Top + 0.5)
    if ($horizontal -and $vertical) {
        throw "$Description overlap."
    }
}

function Select-Tab {
    param([Windows.Automation.AutomationElement]$Element)

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        throw "Navigation tab lacks SelectionItemPattern: $($Element.Current.AutomationId)"
    }
    ([Windows.Automation.SelectionItemPattern]$pattern).Select()
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
    $owner = if ($actual) {
        [string](Get-Process -Id $actual.Current.ProcessId -ErrorAction SilentlyContinue).ProcessName
    }
    else {
        'none'
    }
    throw "Keyboard focus was '$actualId' (process $owner), expected '$AutomationId'."
}

$tabIds = @(
    'Boostix.Center.Tab.Home',
    'Boostix.Center.Tab.Readiness',
    'Boostix.Center.Tab.Impact',
    'Boostix.Center.Tab.Report',
    'Boostix.Center.Tab.Profiles',
    'Boostix.Center.Tab.Settings')

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
        Assert-Between `
            ($windowBounds.Height / $windowBounds.Width) `
            0.62 `
            0.65 `
            "Window aspect at $scale scale ($($windowBounds.Width)x$($windowBounds.Height))"

        $version = Find-ById $window 'Boostix.Version' 15000 -AllowDisabled
        $minimize = Find-ById $window 'Boostix.Minimize'
        $close = Find-ById $window 'Boostix.Close'
        $boost = Find-ById $window 'Boostix.Activate' 20000
        $target = Find-ById $window 'Boostix.Target.Select'
        $openSystem = Find-ById $window 'Boostix.OpenSystem'
        $status = Find-ById $window 'Boostix.Sidebar.Status' 10000 -AllowDisabled
        $watermark = Find-ById $window 'Boostix.Watermark' 10000 -AllowDisabled

        foreach ($obsoleteId in @(
            'Boostix.OpenCenter',
            'Boostix.Keep.DISCORD',
            'Boostix.Keep.EPICGAMES',
            'Boostix.Keep.STEAM')) {
            Assert-ElementAbsent $window $obsoleteId
        }

        foreach ($pair in @(
            @($boost, 'Boost button'),
            @($target, 'Exact game target selector'),
            @($openSystem, 'System details action'),
            @($minimize, 'Minimize button'),
            @($close, 'Close button'))) {
            Assert-KeyboardFocusable $pair[0] $pair[1]
            Assert-InsideWindow $pair[0] $window $pair[1]
        }

        foreach ($chrome in @($minimize, $close)) {
            $chromeBounds = Get-NormalizedBounds $chrome $window
            Assert-Between $chromeBounds.Width 36.5 39.5 'Caption control width'
            Assert-Between $chromeBounds.Height 30.5 33.5 'Caption control height'
            Assert-Between $chromeBounds.Top 12.5 15.5 'Caption control top inset'
        }
        $closeBounds = Get-NormalizedBounds $close $window
        Assert-Between $closeBounds.RightInset 12.5 15.5 'Close button right inset'

        foreach ($tabId in $tabIds) {
            $tab = Find-ById $window $tabId
            Assert-KeyboardFocusable $tab "Navigation tab $tabId"
            if ($tab.Current.ControlType -ne
                [Windows.Automation.ControlType]::TabItem) {
                throw "Navigation entry is not exposed as UIA TabItem: $tabId"
            }
            $tabBounds = Get-NormalizedBounds $tab $window
            Assert-Between $tabBounds.Height 40 44.5 "Navigation tab height at scale $scale"
            if ($tabBounds.Left -lt 8 -or $tabBounds.Right -gt ($sidebarWidth - 8)) {
                throw "Navigation tab $tabId left the sidebar at scale $scale."
            }
        }

        foreach ($sidebarItem in @(
            @($version, 'Version label'),
            @($status, 'Sidebar session status'),
            @($watermark, 'Watermark'))) {
            $itemBounds = Get-NormalizedBounds $sidebarItem[0] $window
            if ($itemBounds.Left -lt 8 -or $itemBounds.Right -gt $sidebarWidth) {
                throw "$($sidebarItem[1]) is outside the sidebar at scale $scale."
            }
        }
        $watermarkBounds = Get-NormalizedBounds $watermark $window
        if ($watermarkBounds.BottomInset -lt 12) {
            throw "Watermark left its footer safe area at scale $scale."
        }
        Assert-NoOverlap $status $watermark 'Sidebar status and watermark'

        $targetBounds = Get-NormalizedBounds $target $window
        if ($targetBounds.Height -lt 72) {
            throw "Exact target selector is $($targetBounds.Height) DIP tall; expected at least 72."
        }
        if ($targetBounds.Left -lt ($sidebarWidth + $contentInset - 1) -or
            $targetBounds.RightInset -lt ($contentInset - 1)) {
            throw (
                "Exact target selector left the content insets at scale $scale " +
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
        Assert-NoOverlap $target $boost 'Target selector and Boost control'
        Assert-NoOverlap $close $target 'Caption controls and target selector'

        if ([Math]::Abs($scale - 1.0) -lt 0.001) {
            if ($windowBounds.Left -lt $workArea.Left -or
                $windowBounds.Top -lt $workArea.Top -or
                $windowBounds.Right -gt $workArea.Right -or
                $windowBounds.Bottom -gt $workArea.Bottom) {
                throw 'The production-size window is not contained in its target monitor work area.'
            }
            # Keyboard order: sidebar navigation, then the game, then Boost.
            $settingsTab = Find-ById $window 'Boostix.Center.Tab.Settings'
            $settingsTab.SetFocus()
            Wait-ForFocusedId 'Boostix.Center.Tab.Settings' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'Boostix.Target.Select' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'Boostix.Activate' 1000
        }

        Select-Tab (Find-ById $window 'Boostix.Center.Tab.Readiness')
        [void](Find-ById $window 'Boostix.Center.SelectTarget')
        Start-Sleep -Milliseconds ($transitionMilliseconds + 40)
        # Fresh preflight data re-renders the page; query the live element.
        $centerTarget = Find-ById $window 'Boostix.Center.SelectTarget'
        $centerTargetBounds = Get-NormalizedBounds $centerTarget $window
        Assert-Between $centerTargetBounds.Height 38 42 "Section action height at scale $scale"
        if ($centerTargetBounds.RightInset -lt ($contentInset - 1) -or
            $centerTargetBounds.BottomInset -lt 12) {
            throw "Section footer action is clipped at scale $scale."
        }
        # Footer actions sit in their own bar below the scrolling content.
        $scroller = Find-ById $window 'Boostix.Center.PageScroller' 10000 -AllowDisabled
        Assert-NoOverlap $centerTarget $scroller 'Footer action and scrolling content'

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        Select-Tab (Find-ById $window 'Boostix.Center.Tab.Report')
        if ([System.Windows.SystemParameters]::ClientAreaAnimation) {
            Wait-ForFocusedId 'Boostix.Center.ReportBenchmark' 1500
            $stopwatch.Stop()
            Assert-Between `
                $stopwatch.Elapsed.TotalMilliseconds `
                180 `
                900 `
                'Directional report transition'
        }

        Select-Tab (Find-ById $window 'Boostix.Center.Tab.Settings')
        [void](Find-ById $window 'Boostix.Center.Setting.0')
        Start-Sleep -Milliseconds ($transitionMilliseconds + 40)
        $setting = Find-ById $window 'Boostix.Center.Setting.0'
        $settingBounds = Get-NormalizedBounds $setting $window
        if ($settingBounds.RightInset -lt ($contentInset - 1)) {
            throw "A settings switch entered the scroll/content gutter at scale $scale."
        }
        Assert-KeyboardFocusable $setting 'Settings switch'
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
        Assert-InsideWindow $restore $window 'Restore action'
        $scroller = Find-ById $window 'Boostix.Center.PageScroller' 10000 -AllowDisabled
        Assert-NoOverlap $restore $scroller 'Restore action and scrolling settings'

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

# Small work areas: the window shrinks uniformly and nothing is clipped.
foreach ($constrainedCase in @(
    @{ Argument = '--demo-compact'; Height = 492.0 },
    @{ Argument = '--demo-ultra-compact'; Height = 360.0 }
)) {
    $constrainedProcess = $null
    try {
        $constrainedProcess = Start-Process `
            -FilePath $ApplicationPath `
            -ArgumentList '--skip-setup', '--demo', $constrainedCase.Argument, $testInstanceArgument `
            -PassThru
        $constrainedWindow = Wait-ForWindow `
            -Process $constrainedProcess `
            -TimeoutMilliseconds 15000
        $bounds = $constrainedWindow.Current.BoundingRectangle
        Assert-Between `
            ($bounds.Height / $bounds.Width) `
            0.62 `
            0.65 `
            "Constrained window aspect for $($constrainedCase.Argument)"
        foreach ($automationId in @(
            'Boostix.Center.Tab.Home',
            'Boostix.Center.Tab.Settings',
            'Boostix.Activate',
            'Boostix.Target.Select',
            'Boostix.Minimize',
            'Boostix.Close')) {
            $element = Find-ById $constrainedWindow $automationId 15000
            Assert-InsideWindow $element $constrainedWindow "$automationId in $($constrainedCase.Argument)"
        }
    }
    finally {
        if ($constrainedProcess -and -not $constrainedProcess.HasExited) {
            [void]$constrainedProcess.CloseMainWindow()
            if (-not $constrainedProcess.WaitForExit(3000)) {
                Stop-Process `
                    -Id $constrainedProcess.Id `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
        }
    }
}

$overlaySource = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs')
if ($overlaySource -notmatch 'SystemParameters\.ClientAreaAnimation') {
    throw 'Section transitions no longer respect the Windows animation preference.'
}
$programSource = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'Boostix\Program.cs')
foreach ($requiredLayout in @(
    'BuildKeyboardFocusVisualStyle',
    'SystemParameters.FocusVisualStyleKey',
    'MonitorFromWindow(',
    'GetMonitorInfo(',
    'GetDpiForWindow(',
    'WmDpiChanged',
    'LocationChanged +=',
    'TransformFromDevice',
    'CalculateMonitorPlacement(',
    'monitorAdaptiveViewbox',
    'StretchDirection.DownOnly'
)) {
    if (-not $programSource.Contains($requiredLayout)) {
        throw "Per-monitor DPI/work-area layout or the keyboard focus visual is missing: $requiredLayout"
    }
}
foreach ($retiredLayout in @(
    'SetCompactMainLayout',
    'SetCenterWindowMode',
    'CenterWindowWidth',
    'CompactWindowHeight'
)) {
    if ($programSource.Contains($retiredLayout)) {
        throw "The retired two-size window layout returned: $retiredLayout"
    }
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
        Join-Path $projectRoot 'Boostix\DesignTokens.cs')),
    (Get-Content -Raw -Encoding UTF8 (
        Join-Path $projectRoot 'Boostix\OptimizationFlow.cs')),
    (Get-Content -Raw -Encoding UTF8 (
        Join-Path $projectRoot 'Boostix\UpdateFlow.cs')))) {
    if ($sourceText -match 'FocusVisualStyle\s*=\s*null') {
        throw 'A keyboard-focusable application control still suppresses its focus visual.'
    }
}

$tokensSource = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'Boostix\DesignTokens.cs')
if ([regex]::Matches($tokensSource, 'SystemParameters\.ClientAreaAnimation').Count -lt 3) {
    throw 'Shared button and color motion no longer respects reduced motion.'
}
foreach ($motionSourcePath in @(
    (Join-Path $projectRoot 'Boostix\OptimizationFlow.cs'),
    (Join-Path $projectRoot 'Boostix\UpdateFlow.cs')
)) {
    $motionSource = Get-Content -Raw -Encoding UTF8 $motionSourcePath
    if ($motionSource -notmatch 'SystemParameters\.ClientAreaAnimation') {
        throw "Reduced-motion handling is missing in $motionSourcePath."
    }
    if ($motionSource -notmatch 'BoostixUi\.ActionButton\(') {
        throw "Dialog actions in $motionSourcePath do not use the shared Boostix buttons."
    }
}

"Responsive layout test passed: $($results -join ', '); fixed 880x560 DIP shell stays crisp when it fits and scales uniformly otherwise, with sidebar UIA tabs, keyboard order, separated footer actions, unclipped compact work areas, reduced motion and $transitionMilliseconds ms transitions verified."
