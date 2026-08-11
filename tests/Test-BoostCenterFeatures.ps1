[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
$center = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'))
$features = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostFeatures.cs'))
$capture = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\PerformanceCapture.cs'))
$profiles = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\GameTargetProfiles.cs'))
$tokens = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\DesignTokens.cs'))
$optimization = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\OptimizationFlow.cs'))
$installer = [IO.File]::ReadAllText((Join-Path $projectRoot 'BoostixInstaller\Program.cs'))
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))

foreach ($forbiddenLauncherContract in @(
    'AddLauncherCheck',
    'MajesticLauncher',
    'MAJESTIC LAUNCHER'
)) {
    if ($features.Contains($forbiddenLauncherContract)) {
        throw "Preflight is still coupled to the legacy launcher: $forbiddenLauncherContract"
    }
}

foreach ($required in @(
    'AssemblyVersion(ProductBrand.AssemblyVersion)',
    'AssemblyCompany(ProductBrand.CompanyName)',
    'GetApplicationVersion() + "  " + ProductBrand.ReleaseLabel',
    'MakeText(',
    '"by Silas Suspect"',
    'Panel.SetZIndex(watermark, 400)',
    'ProcessPriorityClass.AboveNormal',
    'OriginalPriority = originalPriority',
    'process.StartTime.ToUniversalTime() != item.StartTimeUtc',
    'current != ProcessPriorityClass.AboveNormal',
    'BoostActionOutcome.ExternalOverridePreserved',
    'activeBoostTimer.Interval = TimeSpan.FromSeconds(1)',
    'CheckBeforeBoost=" + centerSettings.CheckBeforeBoost',
    'Interlocked.Increment(ref preflightGeneration)',
    'generation != Interlocked.CompareExchange(ref preflightGeneration, 0, 0)',
    'lastSession.Complete(',
    '"Interrupted"',
    'BoostSessionReportStore.Save',
    'PerformanceCaptureService.CaptureTargetAsync',
    'private const double BaseWindowWidth = 460',
    'private const double CenterWindowWidth = 620',
    'preferenceSection = BuildSessionSummaryPanel();',
    '"Boostix.Target.Select"',
    'gameTargetService.EnumerateCandidates()',
    'gameTargetService.TryResolve(',
    'TryMatchSavedAutoBoostProfile(',
    'gameProfileStore.SetAutoBoost('
)) {
    if (-not $program.Contains($required)) {
        throw "The Boost session contract is missing: $required"
    }
}

function Find-SubMinimumTextLiterals {
    param(
        [string]$Source,
        [string]$SourceName
    )

    $violations = New-Object 'Collections.Generic.List[string]'
    $lines = [regex]::Split($Source, '\r?\n')
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match
            'FontSize\s*=\s*(?<size>\d+(?:\.\d+)?)\s*,') {
            $size = [double]::Parse(
                $Matches.size,
                [Globalization.CultureInfo]::InvariantCulture)
            if ($size -lt 11) {
                $violations.Add(
                    "${SourceName}:$($index + 1) FontSize=$size")
            }
        }

        if ($lines[$index] -notmatch 'MakeText\(') {
            continue
        }
        if ($lines[$index] -match
            'MakeText\([^\r\n]*?,\s*(?<size>\d+(?:\.\d+)?)\s*,') {
            $size = [double]::Parse(
                $Matches.size,
                [Globalization.CultureInfo]::InvariantCulture)
            if ($size -lt 11) {
                $violations.Add(
                    "${SourceName}:$($index + 1) MakeText=$size")
            }
            continue
        }

        $last = [Math]::Min($index + 10, $lines.Length - 1)
        for ($probe = $index + 1; $probe -le $last; $probe++) {
            if ($lines[$probe] -match
                'BoostixDesignTokens\.(?:MetadataTextSize|BodyTextSize)') {
                break
            }
            if ($lines[$probe] -match
                'BoostixDesignTokens\.(?:BodyTextSize|MetadataTextSize|SectionTitleSize)\s*,') {
                break
            }
            if ($lines[$probe] -match
                '^\s*(?<size>\d+(?:\.\d+)?),\s*$') {
                $size = [double]::Parse(
                    $Matches.size,
                    [Globalization.CultureInfo]::InvariantCulture)
                if ($size -lt 11) {
                    $violations.Add(
                        "${SourceName}:$($probe + 1) MakeText=$size")
                }
                break
            }
            if ($lines[$probe] -match '^\s*\);?\s*$') {
                break
            }
        }
    }
    return $violations.ToArray()
}

$windowControlsStart = $program.IndexOf(
    'private Grid BuildWindowControls',
    [StringComparison]::Ordinal)
$titleStart = $program.IndexOf(
    'private StackPanel BuildTitle',
    $windowControlsStart,
    [StringComparison]::Ordinal)
if ($windowControlsStart -lt 0 -or $titleStart -le $windowControlsStart) {
    throw 'The main-window chrome composition could not be located.'
}
$windowControls = $program.Substring(
    $windowControlsStart,
    $titleStart - $windowControlsStart)
foreach ($required in @(
    'Margin = new Thickness(0),',
    'center.HorizontalAlignment = HorizontalAlignment.Left',
    'center.VerticalAlignment = VerticalAlignment.Top',
    'header.Children.Add(center);',
    'controls.HorizontalAlignment = HorizontalAlignment.Right',
    'controls.VerticalAlignment = VerticalAlignment.Top'
)) {
    if (-not $windowControls.Contains($required)) {
        throw "The split left/right window-chrome contract is missing: $required"
    }
}
if ($windowControls.Contains('controls.Children.Add(center)')) {
    throw 'The settings button is still grouped with the right-side caption controls.'
}

$centerButtonStart = $program.IndexOf(
    'private static Button MakeCenterButton',
    [StringComparison]::Ordinal)
$windowButtonStart = $program.IndexOf(
    'private static Button MakeWindowButton',
    $centerButtonStart,
    [StringComparison]::Ordinal)
$transparentButtonTemplateStart = $program.IndexOf(
    'private static ControlTemplate MakeTransparentButtonTemplate',
    $windowButtonStart,
    [StringComparison]::Ordinal)
$chromeButtonTemplateStart = $program.IndexOf(
    'private static ControlTemplate MakeChromeButtonTemplate',
    $transparentButtonTemplateStart,
    [StringComparison]::Ordinal)
$makeTextStart = $program.IndexOf(
    'private static TextBlock MakeText',
    $chromeButtonTemplateStart,
    [StringComparison]::Ordinal)
if ($centerButtonStart -lt 0 -or
    $windowButtonStart -le $centerButtonStart -or
    $transparentButtonTemplateStart -le $windowButtonStart -or
    $chromeButtonTemplateStart -le $transparentButtonTemplateStart -or
    $makeTextStart -le $chromeButtonTemplateStart) {
    throw 'The window chrome geometry sections could not be located.'
}
$centerButton = $program.Substring(
    $centerButtonStart,
    $windowButtonStart - $centerButtonStart)
$windowButton = $program.Substring(
    $windowButtonStart,
    $transparentButtonTemplateStart - $windowButtonStart)
$chromeButtonTemplate = $program.Substring(
    $chromeButtonTemplateStart,
    $makeTextStart - $chromeButtonTemplateStart)
foreach ($required in @(
    'FontSize = 19',
    'HorizontalAlignment = HorizontalAlignment.Center',
    'VerticalAlignment = VerticalAlignment.Center'
)) {
    if (-not $centerButton.Contains($required)) {
        throw "The centered settings glyph contract is missing: $required"
    }
}
foreach ($required in @(
    'glyphCanvas.Width = TitleControlSize',
    'glyphCanvas.Height = TitleControlSize',
    'Geometry.Parse("M 11,11 L 21,21 M 21,11 L 11,21")',
    'minimizeGlyph.Width = 16',
    'Canvas.SetLeft(minimizeGlyph, 8)',
    'Canvas.SetTop(minimizeGlyph, 19)'
)) {
    if (-not $windowButton.Contains($required)) {
        throw "The centered caption glyph contract is missing: $required"
    }
}
if (-not $chromeButtonTemplate.Contains(
        'BorderThicknessProperty, new Thickness(0)')) {
    throw 'The window chrome template still insets its glyph content.'
}

foreach ($required in @(
    'elevated && !IsTrustedInstalledToolPath(tool.Path)',
    'CreateElevatedCapturePath()',
    'Environment.SpecialFolder.CommonApplicationData',
    'ResolveProtectedCaptureDirectory()',
    'ValidateElevatedCaptureFile(elevatedOutputPath);',
    'File.Copy(elevatedOutputPath, csvPath, false)',
    'FileAttributes.ReparsePoint',
    'ExpectedPresentMonSha256'
)) {
    if (-not $capture.Contains($required)) {
        throw "The elevated measurement safety contract is missing: $required"
    }
}

foreach ($required in @(
    'PrepareCaptureDirectoryTransaction',
    'ApplyCaptureDirectoryTransaction',
    'RollbackCaptureDirectoryTransaction',
    'SetAccessRuleProtection(true, false)',
    'WellKnownSidType.LocalSystemSid',
    'WellKnownSidType.BuiltinAdministratorsSid',
    'WellKnownSidType.AuthenticatedUserSid',
    'FileSystemRights.ReadAndExecute',
    'PropagationFlags.InheritOnly'
)) {
    if (-not $installer.Contains($required)) {
        throw "The protected ProgramData capture ACL contract is missing: $required"
    }
}

$maintenanceStart = $program.IndexOf('private void RunActiveBoostMaintenance', [StringComparison]::Ordinal)
$restoreStart = $program.IndexOf('private void RestoreOwnedTargetPriorities', $maintenanceStart, [StringComparison]::Ordinal)
if ($maintenanceStart -lt 0 -or $restoreStart -le $maintenanceStart) {
    throw 'The Active Boost maintenance section could not be located.'
}
$maintenance = $program.Substring($maintenanceStart, $restoreStart - $maintenanceStart)
foreach ($forbidden in @(
    'ProcessPriorityClass.High',
    'ProcessPriorityClass.RealTime',
    '.Kill()',
    'Discord',
    'steamwebhelper',
    'EpicGamesLauncher',
    'NVIDIA Overlay',
    'wallpaper64'
)) {
    if ($maintenance.Contains($forbidden)) {
        throw "Active maintenance contains forbidden repeated behavior: $forbidden"
    }
}

foreach ($required in @(
    'CenterPage.Readiness',
    'CenterPage.Impact',
    'CenterPage.Report',
    'CenterPage.Profiles',
    'CenterPage.Settings',
    'OpenReadiness',
    'OpenImpact',
    'OpenReport',
    'OpenProfiles',
    'OpenSettings',
    'BoostCenterTabButton',
    'BoostCenterTabAutomationPeer',
    'ISelectionItemProvider',
    'AutomationControlType.TabItem',
    'PatternInterface.SelectionItem',
    'AddTab(tabs, CenterPage.Readiness',
    'AddTab(tabs, CenterPage.Impact',
    'AddTab(tabs, CenterPage.Report',
    'AddTab(tabs, CenterPage.Profiles',
    'AddTab(tabs, CenterPage.Settings',
    'AutomationProperties.SetName',
    'KeyboardNavigationMode.Cycle',
    'SystemParameters.ClientAreaAnimation',
    'using Boostix.Branding;',
    'BoostixDesignTokens.Accent',
    'BoostixDesignTokens.AccentText',
    'Color targetColor = selected ? AccentTextColor : MutedColor;',
    'MakeBoostixVerticalScrollBarStyle',
    'ProductBrand.AccentVisualHex',
    'ProductBrand.AccentTextHex',
    'ProductBrand.AccentHex',
    'CanContentScroll = false',
    'PageScrollerPreviewMouseWheel',
    'CalculateSmoothScrollTarget',
    'Thumb.DragStartedEvent',
    'BeginPageTransition(previousPage, page);',
    'FinishPageTransitionImmediately();',
    'generation != pageTransitionGeneration',
    '-direction * 18',
    'direction * 18',
    'TimeSpan.FromMilliseconds(milliseconds)',
    'AnimateTabColor(foreground, targetColor, animate);',
    'AnimateTabIndicator(',
    'FillBehavior = FillBehavior.Stop',
    'MakeKeyboardFocusVisualStyle',
    'FocusVisualStyle = MakeKeyboardFocusVisualStyle(6)',
    '"Boostix.Center"',
    '"Boostix.Center.Tab." + page',
    'Raise(RestoreRequested)'
)) {
    if (-not $center.Contains($required)) {
        throw "The Boost Center UI contract is missing: $required"
    }
}
$addTabStart = $center.IndexOf(
    'private void AddTab(',
    [StringComparison]::Ordinal)
$openCenterStart = $center.IndexOf(
    'private void Open(',
    $addTabStart,
    [StringComparison]::Ordinal)
if ($addTabStart -lt 0 -or $openCenterStart -le $addTabStart) {
    throw 'The Boost Center tab construction section could not be isolated.'
}
$addTab = $center.Substring($addTabStart, $openCenterStart - $addTabStart)
if (-not $addTab.Contains('Height = new GridLength(40)') -and
    -not $addTab.Contains(
        'Height = new GridLength(BoostixDesignTokens.MinimumActionHeight)')) {
    throw 'Boost Center tab hit targets are not 40 DIP.'
}
foreach ($forbiddenPublicUi in @(
    '"Majestic',
    'GTA',
    'settings.AutoBoost',
    '#E81C5A',
    'Color.FromRgb(232, 28, 90)',
    'MakeMajesticVerticalScrollBarStyle',
    '"MajesticBoost.Center'
)) {
    if ($center.Contains($forbiddenPublicUi)) {
        throw "The Boostix Center still contains legacy public UI: $forbiddenPublicUi"
    }
}
foreach ($requiredPublicUi in @(
    'BOOSTIX",',
    '"Boostix.Center"',
    '"Boostix.Center.SelectTarget"',
    '"Boostix.Center.ImpactScan"',
    '"Boostix.Center.ProfileAdd"',
    '"Boostix.Center.ProfileAuto."',
    '"Boostix.Center.Setting."',
    'settings.CheckBeforeBoost'
)) {
    if (-not $center.Contains($requiredPublicUi)) {
        throw "The Boostix Center public copy is missing: $requiredPublicUi"
    }
}
if ($center -notmatch '(?s)AnimatePageVisual\(\s*pageScroller,.*?PageTransitionExitMilliseconds,\s*EasingMode\.EaseIn' -or
    $center -notmatch '(?s)AnimatePageVisual\(\s*pageScroller,.*?PageTransitionEnterMilliseconds,\s*EasingMode\.EaseOut' -or
    -not $center.Contains('PageTransitionTotalMilliseconds =')) {
    throw 'The Boost Center page transition timing contract is missing.'
}

foreach ($required in @(
    'ProcessStartTimeUtc',
    'current.StartTimeUtc != identity.ProcessStartTimeUtc',
    'GameExecutablePath.AreEquivalent(',
    'internal bool TryResolve(',
    'internal bool TryMatchSavedAutoBoostProfile(',
    'internal bool SetAutoBoost(string executablePath, bool enabled)',
    'TryGetAutoBoostProfile(',
    'StringComparison.OrdinalIgnoreCase',
    'AtomicWriteUtf8(',
    'TryQuarantineCorruptFile()',
    'File.Replace(',
    'File.Move('
)) {
    if (-not $profiles.Contains($required)) {
        throw "The exact-target/profile safety contract is missing: $required"
    }
}

foreach ($required in @(
    'public const double BodyTextSize = 12',
    'public const double MetadataTextSize = 11',
    'public const double MinimumActionHeight = 40',
    'public const double PreferredActionHeight = 44'
)) {
    if (-not $tokens.Contains($required)) {
        throw "The Boostix 2.0 typography/hit-target token is missing: $required"
    }
}

$mainCompositionStart = $program.IndexOf(
    'private Grid BuildShell()',
    [StringComparison]::Ordinal)
$sessionPanelStart = $program.IndexOf(
    'private Grid BuildSessionSummaryPanel()',
    $mainCompositionStart,
    [StringComparison]::Ordinal)
if ($mainCompositionStart -lt 0 -or $sessionPanelStart -le $mainCompositionStart) {
    throw 'The main Boostix 2.0 composition could not be located.'
}
$mainComposition = $program.Substring(
    $mainCompositionStart,
    $sessionPanelStart - $mainCompositionStart)
if (-not $mainComposition.Contains(
        'preferenceSection = BuildSessionSummaryPanel();')) {
    throw 'The main window does not compose the exact-target session summary.'
}
if ($mainComposition.Contains('BuildPreferencePanel()') -or
    $mainComposition.Contains('BuildPreferenceToggle(') -or
    $mainComposition.Contains('Boostix.Keep.')) {
    throw 'The main window still composes the obsolete keep-toggle panel.'
}

$sessionPanelEnd = $program.IndexOf(
    'private TextBlock AddLiveMetric(',
    $sessionPanelStart,
    [StringComparison]::Ordinal)
if ($sessionPanelEnd -le $sessionPanelStart) {
    throw 'The exact-target session summary section could not be isolated.'
}
$sessionPanel = $program.Substring(
    $sessionPanelStart,
    $sessionPanelEnd - $sessionPanelStart)
foreach ($required in @(
    '"Boostix.Target.Select"',
    'BoostixDesignTokens.BodyTextSize',
    'BoostixDesignTokens.MetadataTextSize',
    'KeyboardNavigation.SetTabIndex(targetSelectorButton, 11)'
)) {
    if (-not $sessionPanel.Contains($required)) {
        throw "The exact-target selector layout contract is missing: $required"
    }
}
if (-not $sessionPanel.Contains('Height = 44') -and
    -not $sessionPanel.Contains(
        'Height = BoostixDesignTokens.PreferredActionHeight')) {
    throw 'The exact-target selector is not a 44 DIP preferred action.'
}

$minimumTextViolations = @(
    Find-SubMinimumTextLiterals $mainComposition 'Program.cs/main'
    Find-SubMinimumTextLiterals $center 'BoostCenterOverlay.cs'
)
if ($minimumTextViolations.Count -gt 0) {
    $preview = ($minimumTextViolations | Select-Object -First 12) -join '; '
    throw (
        'Boostix 2.0 exposes text below the 11 DIP minimum: ' + $preview)
}
foreach ($required in @(
    'Width = new GridLength(36 + ToggleSafeGutter)',
    'HorizontalAlignment = HorizontalAlignment.Center',
    'Margin = new Thickness(0)',
    'Margin = new Thickness(3, 0, 0, 0)',
    'new TranslateTransform(isChecked ? 14 : 0, 0)',
    'double targetX = active ? 14 : 0',
    'UseLayoutRounding = true',
    'ClipToBounds = false',
    'Margin = new Thickness(0, 8, ScrollSafeInset, 8)'
)) {
    if (-not $center.Contains($required)) {
        throw "The Boost Center toggle anti-clipping contract is missing: $required"
    }
}
foreach ($required in @(
    'float rightInset = 2F * dpiScale',
    'float trackWidth = 36F * dpiScale',
    'float trackLeft = Width - rightInset - trackWidth'
)) {
    if (-not $installer.Contains($required)) {
        throw "The installer toggle DPI-safe right-edge inset is missing: $required"
    }
}
if ($installer.Contains('float trackLeft = Width - trackWidth;')) {
    throw 'The installer toggle does not preserve its rounded right-edge inset.'
}

foreach ($forbidden in @(
    'IsKeyboardFocusedProperty'
)) {
    if ($program.Contains($forbidden) -or $center.Contains($forbidden)) {
        throw "The interaction-style contract still contains: $forbidden"
    }
}
if ($installer.Contains('DrawFocusRectangle')) {
    throw 'The installer still draws a click/focus rectangle.'
}

foreach ($required in @(
    'internal string GetOptimizationStatus()',
    'internal bool ShowManualRestore()',
    'BeginRestoreAndClose()'
)) {
    if (-not $optimization.Contains($required)) {
        throw "The manual restore contract is missing: $required"
    }
}

foreach ($required in @(
    'WriteAllTextAtomic',
    'MaxReports = 20',
    'BoostPreflightService',
    'AvailableMemoryStartBytes',
    'ExternalOverridePreserved'
)) {
    if (-not $features.Contains($required)) {
        throw "The report/preflight contract is missing: $required"
    }
}

foreach ($required in @(
    'BoostCenterOverlay.cs',
    'PerformanceCapture.cs',
    'Pinned PresentMon 2.5.1',
    'Boostix.PresentMon.exe'
)) {
    if (-not $build.Contains($required)) {
        throw "The release build contract is missing: $required"
    }
}

Write-Host 'Boost Center and safe session regression test passed.' -ForegroundColor Green
