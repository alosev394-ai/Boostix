[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$fixture = Join-Path $PSScriptRoot 'fixtures\presentmon-v2.csv'
$captureSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'Boostix\PerformanceCapture.cs'))
$installerSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'BoostixInstaller\Program.cs'))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-PerformanceCapture-' + [Guid]::NewGuid().ToString('N'))
$harness = Join-Path $temporaryRoot 'PerformanceCaptureParserHarness.exe'

foreach ($required in @(
    'Environment.SpecialFolder.CommonApplicationData',
    'ResolveProtectedCaptureDirectory()',
    'ValidateProtectedCaptureDirectory',
    'ValidateElevatedCaptureFile(elevatedOutputPath);',
    'ProductBrand.ProductFileName + "-PresentMon-"',
    'FileAttributes.ReparsePoint',
    'CalculateOnePercentLowFps(sorted)',
    'Math.Ceiling(sortedFrameTimes.Count * 0.01)',
    'Process.GetProcesses()',
    'process.MainWindowHandle == IntPtr.Zero',
    'process.WorkingSet64 < MinimumTargetWorkingSetBytes',
    'ExcludedTargetProcesses.Contains(processName)'
)) {
    if (-not $captureSource.Contains($required)) {
        throw "Protected elevated capture contract is missing: $required"
    }
}

if ($captureSource.Contains('OnePercentLowFps = 1000.0 / p99')) {
    throw '1% low must average the slowest 1% frame times, not reuse P99.'
}
foreach ($forbiddenPublicTarget in @(
    '"GTA V',
    '"GTA5"',
    '"GTA5_Enhanced"'
)) {
    if ($captureSource.Contains($forbiddenPublicTarget)) {
        throw "Performance capture is still tied to a single game: $forbiddenPublicTarget"
    }
}

$elevatedPathStart = $captureSource.IndexOf(
    'private static string CreateElevatedCapturePath()',
    [StringComparison]::Ordinal)
$elevatedPathEnd = $captureSource.IndexOf(
    'private static void ValidateProtectedCaptureDirectory',
    $elevatedPathStart,
    [StringComparison]::Ordinal)
if ($elevatedPathStart -lt 0 -or $elevatedPathEnd -le $elevatedPathStart) {
    throw 'The protected elevated capture path section could not be located.'
}
$elevatedPathSection = $captureSource.Substring(
    $elevatedPathStart,
    $elevatedPathEnd - $elevatedPathStart)
if ($elevatedPathSection.Contains('SpecialFolder.Windows') -or
    $elevatedPathSection.Contains('"Temp"')) {
    throw 'Elevated capture must not stage CSV files in Windows\Temp.'
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
    'FileSystemRights.Delete',
    'PropagationFlags.InheritOnly',
    'Directory.CreateDirectory(path, security)',
    'TryPruneProtectedCaptureFiles(true)'
)) {
    if (-not $installerSource.Contains($required)) {
        throw "Protected capture installer contract is missing: $required"
    }
}

try {
    [void](New-Item -ItemType Directory -Path $temporaryRoot -Force)
    $arguments = @(
        '/nologo',
        '/target:exe',
        '/optimize+',
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Management.dll',
        "/out:$harness",
        (Join-Path $projectRoot 'ProductBrand.cs'),
        (Join-Path $projectRoot 'Boostix\BoostFeatures.cs'),
        (Join-Path $projectRoot 'Boostix\DiagnosticsFeatures.cs'),
        (Join-Path $projectRoot 'Boostix\SessionInsights.cs'),
        (Join-Path $projectRoot 'Boostix\PerformanceCapture.cs'),
        (Join-Path $PSScriptRoot 'PerformanceCaptureParserHarness.cs')
    )

    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "PerformanceCapture harness compilation failed with exit code $LASTEXITCODE."
    }

    & $harness $fixture
    if ($LASTEXITCODE -ne 0) {
        throw "PerformanceCapture parser test failed with exit code $LASTEXITCODE."
    }
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedTarget = [IO.Path]::GetFullPath($temporaryRoot)
    if (
        $resolvedTarget.StartsWith(
            $resolvedTemp + 'Boostix-PerformanceCapture-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTarget)
    ) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force -ErrorAction SilentlyContinue
    }
}
