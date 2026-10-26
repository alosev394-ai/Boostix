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
    'MakeText(',
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
    'private const double BaseWindowWidth = BoostixDesignTokens.WindowWidth',
    'private const double BaseWindowHeight = BoostixDesignTokens.WindowHeight',
    'FrameworkElement home = BuildHomePage();',
    'boostCenterOverlay.SetHomeContent(home);',
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

# The sidebar is the navigation; the caption row keeps only minimize/close at
# the top right of the content column. The retired gear opener is gone.
$windowControlsStart = $program.IndexOf(
    'private StackPanel BuildWindowControls()',
    [StringComparison]::Ordinal)
$windowControlsEnd = $program.IndexOf(
    'private Grid BuildBoostButton()',
    $windowControlsStart,
    [StringComparison]::Ordinal)
if ($windowControlsStart -lt 0 -or $windowControlsEnd -le $windowControlsStart) {
    throw 'The main-window chrome composition could not be located.'
}
$windowControls = $program.Substring(
    $windowControlsStart,
    $windowControlsEnd - $windowControlsStart)
foreach ($required in @(
    'HorizontalAlignment = HorizontalAlignment.Right',
    'VerticalAlignment = VerticalAlignment.Top',
    '"Boostix.TitleBar"',
    '"Boostix.Minimize"',
    '"Boostix.Close"'
)) {
    if (-not $windowControls.Contains($required)) {
        throw "The window-chrome contract is missing: $required"
    }
}
if ($program.Contains('private static Button MakeCenterButton') -or
    $program.Contains('"Boostix.OpenCenter"')) {
    throw 'The retired gear opener returned beside the sidebar navigation.'
}

$windowButtonStart = $program.IndexOf(
    'private static Button MakeWindowButton',
    [StringComparison]::Ordinal)
$chromeButtonTemplateStart = $program.IndexOf(
    'private static ControlTemplate MakeChromeButtonTemplate',
    [StringComparison]::Ordinal)
$makeTextStart = $program.IndexOf(
    'private static TextBlock MakeText',
    $chromeButtonTemplateStart,
    [StringComparison]::Ordinal)
if ($windowButtonStart -lt 0 -or
    $chromeButtonTemplateStart -lt 0 -or
    $makeTextStart -le $chromeButtonTemplateStart) {
    throw 'The window chrome geometry sections could not be located.'
}
$windowButton = $program.Substring(
    $windowButtonStart,
    $program.IndexOf('}', $program.IndexOf('return button;', $windowButtonStart)) - $windowButtonStart)
foreach ($required in @(
    'button.Height = TitleControlSize',
    'BoostixIcons.Close',
    'BoostixIcons.Minimize',
    'BoostixDesignTokens.Destructive',
    'FocusVisualStyle = BoostixUi.FocusVisual('
)) {
    if (-not $windowButton.Contains($required)) {
        throw "The caption button contract is missing: $required"
    }
}
$chromeButtonTemplate = $program.Substring(
    $chromeButtonTemplateStart,
    $makeTextStart - $chromeButtonTemplateStart)
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
    'CenterPage.Home',
    'AddTab(tabs, CenterPage.Home',
    'SetHomeContent(',
    'BuildSidebar()',
    '"Boostix.Sidebar"',
    '"Boostix.Watermark"',
    '"by Silas Suspect"',
    'GetApplicationVersion() + "  " + ProductBrand.ReleaseLabel',
    'Raise(PageOpening);',
    'Raise(preserveTabFocus ? HomeNavigated : CloseRequested);',
    'footerBar.Visibility',
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
    'MakeBoostixVerticalScrollBarStyle',
    'ProductBrand.AccentVisualHex',
    'ProductBrand.AccentHex',
    'CanContentScroll = false',
    'PageScrollerPreviewMouseWheel',
    'CalculateSmoothScrollTarget',
    'Thumb.DragStartedEvent',
    'BeginPageTransition(previousPage, page, preserveTabFocus);',
    'FinishPageTransitionImmediately();',
    'generation != pageTransitionGeneration',
    '-direction * 18',
    'direction * 18',
    'TimeSpan.FromMilliseconds(milliseconds)',
    'AnimateTabColor(visuals.Background, background, animate);',
    'AnimateTabIndicator(',
    'FillBehavior = FillBehavior.Stop',
    'MakeKeyboardFocusVisualStyle',
    'FocusVisualStyle = MakeKeyboardFocusVisualStyle(BoostixDesignTokens.RadiusSmall)',
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
if (-not $center.Contains('private const double NavigationItemHeight = 42;') -or
    -not $addTab.Contains('Height = NavigationItemHeight')) {
    throw 'Sidebar navigation hit targets are not 42 DIP.'
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
if ($center -notmatch '(?s)AnimatePageVisual\(\s*pageArea,.*?PageTransitionExitMilliseconds,\s*EasingMode\.EaseIn' -or
    $center -notmatch '(?s)AnimatePageVisual\(\s*pageArea,.*?PageTransitionEnterMilliseconds,\s*EasingMode\.EaseOut' -or
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
    'public const double BodyTextSize = 13',
    'public const double MetadataTextSize = 11.5',
    'public const double OverlineTextSize = 11',
    'public const double WindowWidth = 880',
    'public const double WindowHeight = 560',
    'public const double MinimumActionHeight = 40',
    'public const double PreferredActionHeight = 44'
)) {
    if (-not $tokens.Contains($required)) {
        throw "The Boostix typography/hit-target token is missing: $required"
    }
}

$mainCompositionStart = $program.IndexOf(
    'private Grid BuildShell()',
    [StringComparison]::Ordinal)
$mainCompositionEnd = $program.IndexOf(
    'private TextBlock AddLiveMetric(',
    $mainCompositionStart,
    [StringComparison]::Ordinal)
if ($mainCompositionStart -lt 0 -or $mainCompositionEnd -le $mainCompositionStart) {
    throw 'The main Boostix composition could not be located.'
}
$mainComposition = $program.Substring(
    $mainCompositionStart,
    $mainCompositionEnd - $mainCompositionStart)
foreach ($required in @(
    'private Grid BuildHomePage()',
    'private Button BuildTargetSelector()',
    'private Border BuildBoostCard()',
    'private Border BuildHomeSystemCard()',
    'Grid orb = BuildBoostButton();'
)) {
    if (-not $mainComposition.Contains($required)) {
        throw "The Home composition contract is missing: $required"
    }
}
if ($program.Contains('BuildPreferencePanel()') -or
    $program.Contains('BuildPreferenceToggle(') -or
    $program.Contains('Boostix.Keep.')) {
    throw 'The main window still composes the obsolete keep-toggle panel.'
}

$targetStart = $program.IndexOf(
    'private Button BuildTargetSelector()',
    [StringComparison]::Ordinal)
$targetEnd = $program.IndexOf(
    'private void SetTargetIcon(',
    $targetStart,
    [StringComparison]::Ordinal)
if ($targetEnd -le $targetStart) {
    throw 'The exact-target selector section could not be isolated.'
}
$targetSelector = $program.Substring($targetStart, $targetEnd - $targetStart)
foreach ($required in @(
    '"Boostix.Target.Select"',
    'BoostixDesignTokens.MetadataTextSize',
    'KeyboardNavigation.SetTabIndex(targetSelectorButton, 10)',
    'MinHeight = 74'
)) {
    if (-not $targetSelector.Contains($required)) {
        throw "The exact-target selector layout contract is missing: $required"
    }
}

$minimumTextViolations = @(
    Find-SubMinimumTextLiterals $mainComposition 'Program.cs/main'
    Find-SubMinimumTextLiterals $center 'BoostCenterOverlay.cs'
)
if ($minimumTextViolations.Count -gt 0) {
    $preview = ($minimumTextViolations | Select-Object -First 12) -join '; '
    throw (
        'Boostix exposes text below the 11 DIP minimum: ' + $preview)
}
foreach ($required in @(
    'Width = new GridLength(44 + ToggleSafeGutter)',
    'Margin = new Thickness(2, 0, 0, 0)',
    'new TranslateTransform(isChecked ? 20 : 0, 0)',
    'double targetX = active ? 20 : 0',
    'UseLayoutRounding = true',
    'ClipToBounds = false',
    'visible ? ScrollSafeInset - ScrollBarWidth : ScrollSafeInset'
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
