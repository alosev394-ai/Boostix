[CmdletBinding()]
param(
    # Retained for compatibility with older release-test callers. Session Guard
    # is tested from source so stale installed binaries cannot mask regressions.
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5 -or
    [IntPtr]::Size -ne 8) {
    throw 'This regression test requires 64-bit Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$guardPath = Join-Path $projectRoot 'Boostix\SessionGuard.cs'
$guardTestPath = Join-Path $PSScriptRoot 'Test-SessionGuard.ps1'
if (-not (Test-Path -LiteralPath $guardPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $guardTestPath -PathType Leaf)) {
    throw 'The Session Guard source or regression harness is missing.'
}

$guard = [IO.File]::ReadAllText($guardPath)
foreach ($required in @(
    'internal sealed class SessionGuardTargetIdentity',
    'internal sealed class SessionGuardPressurePolicy',
    'SessionGuardPressureDecision.CriticalAlertRaised',
    'SessionGuardPressureDecision.Recovered',
    'SessionGuardPressureDecision.Cooldown',
    'internal static class PagefileAdvisor',
    'TimeSpan.FromSeconds(60)',
    'processStartUtc != target.ProcessStartTimeUtc'
)) {
    if (-not $guard.Contains($required)) {
        throw "The advisory memory-pressure contract is missing: $required"
    }
}

foreach ($forbiddenPattern in @(
    '\bEmptyWorkingSet\b',
    '\bSetProcessWorkingSetSize(?:Ex)?\b',
    '\bNtSetSystemInformation\b',
    '\b(?:MemoryPurgeStandbyList|SystemMemoryListInformation|EmptyStandbyList|PurgeStandbyList)\b',
    '\bSetSystemFileCacheSize\b',
    '\b(?:System\s*\.\s*)?GC\s*\.\s*Collect\s*\(',
    '\bGCCollectionMode\s*\.\s*Forced\b'
)) {
    if ([regex]::IsMatch(
            $guard,
            $forbiddenPattern,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        throw "Session Guard contains forbidden memory mutation: $forbiddenPattern"
    }
}

# This source-built harness supplies deterministic clocks and samplers. It
# proves that a single spike is ignored, sustained pressure raises one alert,
# recovery needs hysteresis, cooldown is honored, zero commit headroom remains
# critical, and an exact PID/start-time identity rejects PID reuse.
& $guardTestPath
if (-not $?) {
    throw 'The deterministic Session Guard pressure-policy harness failed.'
}

Write-Host (
    'Memory-pressure safety regression passed: monitoring and recommendations ' +
    'work without cache purge, working-set trim, or forced GC.') `
    -ForegroundColor Green
