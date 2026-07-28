[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$applyPath = Join-Path $projectRoot 'outputs\MaxFPS-Apply.ps1'
$restorePath = Join-Path $projectRoot 'outputs\MaxFPS-Restore.ps1'
$flowPath = Join-Path $projectRoot 'Boostix\OptimizationFlow.cs'
$apply = [IO.File]::ReadAllText($applyPath)
$restore = [IO.File]::ReadAllText($restorePath)
$flow = [IO.File]::ReadAllText($flowPath)

foreach ($scriptPath in @($applyPath, $restorePath)) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "Production optimization script does not parse: $scriptPath"
    }
}

$profileStart = $apply.IndexOf(
    "'Boostix Performance'",
    [StringComparison]::Ordinal)
$profileEnd = $apply.IndexOf(
    '$state.Status = ''Active''',
    $profileStart,
    [StringComparison]::Ordinal)
if ($profileStart -lt 0 -or $profileEnd -le $profileStart) {
    throw 'The bounded Boostix mutation section could not be located.'
}
$profile = $apply.Substring($profileStart, $profileEnd - $profileStart)

foreach ($forbidden in @(
    'Majestic',
    'GTA',
    'Rockstar',
    'EpicGamesLauncher',
    'ShadowPlay',
    'NVIDIA',
    'Wallpaper Engine',
    'VirtualPad',
    'SPUser',
    'SteelSeries',
    'HwSchMode',
    'Set-MpPreference',
    'Set-Service',
    'Stop-Service',
    'Disable-ScheduledTask',
    'Move-Item',
    'Apply-PreparedFile',
    'Start-Process'
)) {
    if ($profile.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The new Boostix profile still performs a forbidden product/vendor mutation: $forbidden"
    }
}

foreach ($required in @(
    "'Boostix Performance'",
    "'Before Boostix performance optimization'",
    "@(`$processorSubgroup, `$coreParkingMin, '100')",
    "@(`$processorSubgroup, `$coreParkingMax, '100')",
    "@(`$processorSubgroup, `$energyPerformancePreference, '0')",
    "@(`$processorSubgroup, `$boostMode, '2')",
    "@(`$usbSubgroup, `$usbSelectiveSuspend, '0')",
    "@(`$diskSubgroup, `$diskIdle, '0')",
    "HKCU:\Software\Microsoft\GameBar' -Name 'AutoGameModeEnabled' -Value 1",
    "HKCU:\Software\Microsoft\GameBar' -Name 'AllowAutoGameMode' -Value 1",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Name 'AppCaptureEnabled' -Value 0",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Name 'HistoricalCaptureEnabled' -Value 0",
    "HKCU:\System\GameConfigStore' -Name 'GameDVR_Enabled' -Value 0",
    "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling' -Name 'PowerThrottlingOff' -Value 1",
    "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' -Name 'SystemResponsiveness' -Value 10",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Name 'VisualFXSetting' -Value 2",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name 'EnableTransparency' -Value 0",
    "HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name 'AllowNewsAndInterests' -Value 0",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Dsh' -Name 'IsPrelaunchEnabled' -Value 0"
)) {
    if (-not $profile.Contains($required)) {
        throw "The documented Boostix profile is missing a required reversible setting: $required"
    }
}

$trackedWrites = [Regex]::Matches(
    $profile,
    '(?m)^\s*Set-TrackedRegistryValue\s').Count
if ($trackedWrites -ne 11) {
    throw "The new Boostix profile has $trackedWrites tracked registry writes; expected exactly 11."
}

foreach ($required in @(
    "Join-Path `$programDataRoot 'BoostixOptimization'",
    "'BoostixWindowsPerformanceV1'",
    "'Boostix-apply-{0}.json'",
    "'^Boostix-apply-[0-9a-fA-F]{32}\.json$'"
)) {
    if (-not $apply.Contains($required)) {
        throw "The new apply transaction is missing its Boostix identity/safety contract: $required"
    }
}

foreach ($requiredLegacyContract in @(
    "Join-Path `$programDataRoot 'CodexGamingOptimization'",
    'GTA5.exe',
    'Majestic Launcher.exe',
    'Wallpaper Engine Service',
    'nefarius_VirtualPad_Updater',
    'SPUser.disabled-by-majesticboost-'
)) {
    if (-not $restore.Contains($requiredLegacyContract)) {
        throw "Legacy rollback compatibility was removed: $requiredLegacyContract"
    }
}
foreach ($requiredRestoreContract in @(
    "Join-Path `$programDataRoot 'BoostixOptimization'",
    "'Boostix-restore-{0}.json'",
    "'^Boostix-restore-[0-9a-fA-F]{32}\.json$'",
    '$requestedBackupRoot -ieq $legacyBackupRoot',
    '$requestedBackupRoot -ine $boostixBackupRoot'
)) {
    if (-not $restore.Contains($requiredRestoreContract)) {
        throw "The dual-root Boostix restore contract is missing: $requiredRestoreContract"
    }
}

$consentStart = $flow.IndexOf(
    'private void ShowConsent',
    [StringComparison]::Ordinal)
$consentEnd = $flow.IndexOf(
    'private async void ContinueButtonClick',
    $consentStart,
    [StringComparison]::Ordinal)
if ($consentStart -lt 0 -or $consentEnd -le $consentStart) {
    throw 'The Boostix consent section could not be located.'
}
$consent = $flow.Substring($consentStart, $consentEnd - $consentStart)
$requiredConsent = @(
    'Boostix Performance',
    'Game Mode'
)
$requiredConsent += @(
    'Qm9vc3RpeCDQv9GA0LjQvNC10L3QuNGCINC+0LHRgNCw0YLQuNC80YvQuSDQv9GA0L7RhNC40LvRjCDQv9GA0L7QuNC30LLQvtC00LjRgtC10LvRjNC90L7RgdGC0LggV2luZG93cy4=',
    '0L/QsNGA0LrQvtCy0LrRgyDRj9C00LXRgA==',
    'VVNCLdGB0LXQu9C10LrRgtC40LLQvdC+0LUg0L7RgtC60LvRjtGH0LXQvdC40LU=',
    'RFZSLdC30LDQv9C40YHRjA==',
    '0YDQtdC30LXRgNCyIENQVSDQtNC70Y8g0YTQvtC90L7QstGL0YUg0LfQsNC00LDRhyDQtNC+IDEwJQ==',
    '0LLQuNC30YPQsNC70YzQvdGL0LUg0Y3RhNGE0LXQutGC0Ys=',
    '0YTQvtC90L7QstGL0LUg0LLQuNC00LbQtdGC0YsgV2luZG93cw==',
    '0YLQvtGH0LrRgyDQstC+0YHRgdGC0LDQvdC+0LLQu9C10L3QuNGP',
    '0L/QvtGC0YDQtdCx0YPQtdGC0YHRjyDQv9C10YDQtdC30LDQs9GA0YPQt9C60LA='
) | ForEach-Object {
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_))
}
foreach ($requiredConsentItem in $requiredConsent) {
    if (-not $consent.Contains($requiredConsentItem)) {
        throw "The consent screen does not disclose a profile mutation."
    }
}
foreach ($forbiddenConsent in @(
    'Majestic',
    'GTA',
    'NVIDIA',
    'Wallpaper',
    'VirtualPad',
    'Defender',
    'HAGS'
)) {
    if ($consent.IndexOf($forbiddenConsent, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The generic Boostix consent screen still exposes a legacy product/vendor: $forbiddenConsent"
    }
}

foreach ($requiredFlowContract in @(
    'Color.FromRgb(124, 58, 237)',
    'Environment.SpecialFolder.LocalApplicationData',
    '"Boostix"',
    '"Boostix-" + operation',
    'Path.Combine(programData, "BoostixOptimization")',
    'Path.Combine(programData, "CodexGamingOptimization")',
    'candidateBackupsRoot',
    'pointerBackupsRoot',
    'legacyPendingMarkerPath',
    'legacyCompletedMarkerPath'
)) {
    if (-not $flow.Contains($requiredFlowContract)) {
        throw "The Boostix UI/migration contract is missing: $requiredFlowContract"
    }
}

'Generic Boostix profile, exact consent, safe mutation boundary, and legacy rollback tests passed.'
