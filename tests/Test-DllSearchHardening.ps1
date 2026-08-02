[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$program = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'Boostix\Program.cs'))
$updater = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'Boostix\UpdateFlow.cs'))
$installer = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'BoostixInstaller\Program.cs'))

foreach ($source in @($program, $installer)) {
    foreach ($requiredContract in @(
        'LoadLibrarySearchSystem32 = 0x00000800',
        'SetDefaultDllDirectories',
        'SetDllDirectory(string.Empty)',
        'HardenNativeDllSearch()'
    )) {
        if (-not $source.Contains($requiredContract)) {
            throw "Native DLL search hardening is missing: $requiredContract"
        }
    }
    if ($source.Contains('LoadLibrarySearchApplicationDir')) {
        throw 'A user-writable executable directory was restored to native DLL search.'
    }
}

$installerMain = $installer.IndexOf(
    'private static void Main',
    [StringComparison]::Ordinal)
$installerHardening = $installer.IndexOf(
    'if (!HardenNativeDllSearch())',
    $installerMain,
    [StringComparison]::Ordinal)
$installerUi = $installer.IndexOf(
    'Application.EnableVisualStyles()',
    $installerMain,
    [StringComparison]::Ordinal)
$installerLogging = $installer.IndexOf(
    'InstallerDiagnostics.Initialize(args);',
    $installerMain,
    [StringComparison]::Ordinal)
if ($installerMain -lt 0 -or
    $installerHardening -le $installerMain -or
    $installerLogging -le $installerHardening -or
    $installerUi -le $installerHardening) {
    throw 'The elevated installer does not harden DLL search before logging and WinForms start.'
}

$diagnosticsStart = $installer.IndexOf(
    'internal static class InstallerDiagnostics',
    [StringComparison]::Ordinal)
$engineStart = $installer.IndexOf(
    'internal static class InstallerEngine',
    $diagnosticsStart,
    [StringComparison]::Ordinal)
if ($diagnosticsStart -lt 0 -or $engineStart -le $diagnosticsStart) {
    throw 'The installer diagnostics source boundary was not found.'
}
$diagnosticsSource = $installer.Substring(
    $diagnosticsStart,
    $engineStart - $diagnosticsStart)
foreach ($requiredContract in @(
    'EnsureProtectedLogDirectory(',
    'SetAccessRuleProtection(true, false)',
    'FileAttributes.ReparsePoint',
    '"setup-" + CorrelationId + ".log"',
    'FileMode.CreateNew',
    'SanitizeLogValue(',
    'DescribeInvocationMode('
)) {
    if (-not $diagnosticsSource.Contains($requiredContract)) {
        throw "Protected installer diagnostics are missing: $requiredContract"
    }
}
foreach ($forbiddenContract in @(
    'Path.GetTempPath()',
    'Boostix-Setup.log',
    'string.Join(" ", args'
)) {
    if ($diagnosticsSource.Contains($forbiddenContract)) {
        throw "Elevated installer diagnostics retain an unsafe contract: $forbiddenContract"
    }
}

$healthHandshakeStart = $updater.IndexOf(
    'internal static class UpdateHealthHandshake',
    [StringComparison]::Ordinal)
$updateOverlayStart = $updater.IndexOf(
    'internal sealed class UpdateRequiredEventArgs',
    $healthHandshakeStart,
    [StringComparison]::Ordinal)
if ($healthHandshakeStart -lt 0 -or
    $updateOverlayStart -le $healthHandshakeStart) {
    throw 'The elevated update health-handshake source boundary was not found.'
}
$healthHandshakeSource = $updater.Substring(
    $healthHandshakeStart,
    $updateOverlayStart - $healthHandshakeStart)
if (-not $healthHandshakeSource.Contains('Trace.WriteLine(') -or
    $healthHandshakeSource.Contains('File.AppendAllText(') -or
    $healthHandshakeSource.Contains('SpecialFolder.LocalApplicationData') -or
    $healthHandshakeSource.Contains('update.log')) {
    throw 'The elevated update health probe can still append through a user-writable log path.'
}

$crashLogStart = $program.IndexOf(
    'internal static class CrashLog',
    [StringComparison]::Ordinal)
if ($crashLogStart -lt 0) {
    throw 'The application crash-log source boundary was not found.'
}
$crashLogSource = $program.Substring($crashLogStart)
foreach ($requiredContract in @(
    'CrashLog.Configure(args);',
    'healthProbe || IsCurrentProcessElevated()',
    'if (suppressFileLogging)',
    'Trace.WriteLine(BuildEntry(message, exception))'
)) {
    if (-not $program.Contains($requiredContract)) {
        throw "Elevated application log suppression is missing: $requiredContract"
    }
}
if (-not $program.Contains('if (updateHealthProbe)') -or
    -not $program.Contains('the probe only needs deterministic in-memory defaults')) {
    throw 'The elevated health probe can still load preferences from LocalAppData.'
}
$healthProbeMethodStart = $program.IndexOf(
    'private async Task VerifyLocalStartupForUpdateAsync()',
    [StringComparison]::Ordinal)
$healthProbeMethodEnd = $program.IndexOf(
    'private void BoostWindowClosing',
    $healthProbeMethodStart,
    [StringComparison]::Ordinal)
if ($healthProbeMethodStart -lt 0 -or $healthProbeMethodEnd -le $healthProbeMethodStart) {
    throw 'The application health-probe method boundary was not found.'
}
$applicationHealthProbeSource = $program.Substring(
    $healthProbeMethodStart,
    $healthProbeMethodEnd - $healthProbeMethodStart)
if (-not $applicationHealthProbeSource.Contains(
        'optimizationOverlay.IsInitializedForUpdateHealth()') -or
    $applicationHealthProbeSource.Contains('GetOptimizationStatus()') -or
    $applicationHealthProbeSource.Contains('SpecialFolder.LocalApplicationData')) {
    throw 'The elevated application health probe is not profile-neutral.'
}

if ($updater -notmatch
        'Environment\.GetFolderPath\(\s*Environment\.SpecialFolder\.System\)' -or
    -not $updater.Contains('WorkingDirectory = safeWorkingDirectory')) {
    throw 'The updater does not launch the elevated installer from System32.'
}
if ($updater.Contains('WorkingDirectory = directory')) {
    throw 'The updater still uses its user-writable download folder as the working directory.'
}

'Native DLL search hardening test passed.'
