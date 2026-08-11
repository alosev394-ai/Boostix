[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $projectRoot 'Boostix\Program.cs'
$guardPath = Join-Path $projectRoot 'Boostix\SessionGuard.cs'
$featuresPath = Join-Path $projectRoot 'Boostix\BoostFeatures.cs'
$buildPath = Join-Path $projectRoot 'build.ps1'

$program = [IO.File]::ReadAllText($programPath)
$guard = [IO.File]::ReadAllText($guardPath)
$features = [IO.File]::ReadAllText($featuresPath)
$build = [IO.File]::ReadAllText($buildPath)

function Get-SourceSection {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$StartToken,
        [Parameter(Mandatory = $true)][string]$EndToken
    )

    $start = $Source.IndexOf($StartToken, [StringComparison]::Ordinal)
    $end = if ($start -ge 0) {
        $Source.IndexOf($EndToken, $start, [StringComparison]::Ordinal)
    }
    else {
        -1
    }
    if ($start -lt 0 -or $end -le $start) {
        throw "Source section was not found: $StartToken -> $EndToken"
    }
    return $Source.Substring($start, $end - $start)
}

if (-not $build.Contains('Boostix\SessionGuard.cs')) {
    throw 'The production build does not compile Session Guard.'
}

$startGuard = Get-SourceSection `
    -Source $program `
    -StartToken 'private void StartSessionGuard(int generation)' `
    -EndToken 'private void StopSessionGuard()'
foreach ($required in @(
    'selectedTarget == null',
    'new WindowsSessionGuardMetricsSource()',
    'new SessionGuardTargetIdentity(',
    'selectedTarget.ProcessId',
    'selectedTarget.ProcessStartTimeUtc',
    'selectedTarget.ExecutablePath',
    'cancellation.Token'
)) {
    if (-not $startGuard.Contains($required)) {
        throw "Session Guard start boundary is missing: $required"
    }
}

foreach ($required in @(
    'SessionGuardSample CaptureCheap(SessionGuardTargetIdentity target)',
    'Task StartAsync(',
    'SessionGuardTargetIdentity target,',
    'processStartUtc != target.ProcessStartTimeUtc',
    'TimeSpan.FromSeconds(60)'
)) {
    if (-not $guard.Contains($required)) {
        throw "Exact-target Session Guard contract is missing: $required"
    }
}

$stopGuard = Get-SourceSection `
    -Source $program `
    -StartToken 'private void StopSessionGuard()' `
    -EndToken 'private void HandleSessionGuardSample('
foreach ($required in @(
    'cancellation.Cancel()',
    'sampler.Dispose()',
    'cancellation.Dispose()'
)) {
    if (-not $stopGuard.Contains($required)) {
        throw "Session Guard stop/disposal boundary is missing: $required"
    }
}

$refresh = Get-SourceSection `
    -Source $program `
    -StartToken 'private void RefreshActiveBoostMaintenance()' `
    -EndToken 'private void StopActiveBoostMaintenance()'
$refreshInvalidatesGeneration = $refresh.Contains(
    'AdvanceActiveMaintenanceGeneration()')
$refreshRestartsGuard = $refresh.Contains('StartSessionGuard(')
if ($refreshInvalidatesGeneration -and -not $refreshRestartsGuard) {
    throw (
        'Refreshing preferences invalidates the generation captured by Session ' +
        'Guard without restarting the sampler. Live telemetry will silently stop.')
}

$directLegacyCalls = [regex]::Matches(
    $program,
    '(?m)^\s*RunActiveMemoryMaintenance\s*\(')
if ($directLegacyCalls.Count -ne 0) {
    throw 'The legacy working-set maintenance method became reachable again.'
}

$maintenance = Get-SourceSection `
    -Source $program `
    -StartToken 'private void RunActiveBoostMaintenance(int generation)' `
    -EndToken 'private static bool IsExactTargetForeground(int processId)'
if ($maintenance.Contains('ActiveMemoryMaintenanceService') -or
    $maintenance.Contains('RunActiveMemoryMaintenance(')) {
    throw 'The live target-maintenance loop still invokes legacy memory relief.'
}

foreach ($sourceBoundary in @(
    @{ Name = 'Program'; Source = $program },
    @{ Name = 'SessionGuard'; Source = $guard }
)) {
    foreach ($forbidden in @(
        'EmptyWorkingSet',
        'NtSetSystemInformation',
        'MemoryPurgeStandbyList',
        'SetSystemFileCacheSize',
        'SetProcessWorkingSetSize',
        'AdjustTokenPrivileges',
        'SeDebugPrivilege'
    )) {
        if ($sourceBoundary.Source.Contains($forbidden)) {
            throw "$($sourceBoundary.Name) contains a forbidden memory mutation: $forbidden"
        }
    }
}

# During migration, the old implementation may remain compiled for report/test
# compatibility, but every reference must stay inside its own unreachable method
# until that entire section is deleted atomically.
$legacyMethodStart = $program.IndexOf(
    'private void RunActiveMemoryMaintenance(int generation)',
    [StringComparison]::Ordinal)
$programOutsideLegacyMethod = $program
if ($legacyMethodStart -ge 0) {
    $legacyMethodEnd = $program.IndexOf(
        'private void UpdateSessionMemoryTelemetry(',
        $legacyMethodStart,
        [StringComparison]::Ordinal)
    if ($legacyMethodEnd -le $legacyMethodStart) {
        throw 'The legacy memory-maintenance source boundary is incomplete.'
    }
    $programOutsideLegacyMethod =
        $program.Substring(0, $legacyMethodStart) +
        $program.Substring($legacyMethodEnd)
}
if ($programOutsideLegacyMethod.Contains('ActiveMemoryMaintenanceService.')) {
    throw 'Legacy memory relief has acquired a reachable production integration point.'
}
if (-not $features.Contains('internal static class ActiveMemoryMaintenanceService')) {
    Write-Host 'Legacy memory service has been fully removed.'
}
else {
    Write-Host 'Legacy memory service remains compiled but is isolated from runtime flow.'
}

Write-Host 'Session Guard integration-boundary regression test passed.' `
    -ForegroundColor Green
