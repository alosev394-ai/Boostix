[CmdletBinding()]
param(
    [string]$PowerShellPath,
    [ValidateRange(30, 1800)]
    [int]$TestTimeoutSeconds = 300,
    [string]$ResultsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$requiredTests = @(
    'Test-ActiveBoostMonitor.ps1',
    'Test-ActiveMemoryMaintenance.ps1',
    'Test-ApplicationReliability.ps1',
    'Test-AtomicFileReplace.ps1',
    'Test-BoostCenterFeatures.ps1',
    'Test-BoostCenterUi.ps1',
    'Test-BoostUiState.ps1',
    'Test-BrandIdentity.ps1',
    'Test-CompiledAtomicReplace.ps1',
    'Test-CrossMachineReliability.ps1',
    'Test-DiagnosticsFeatures.ps1',
    'Test-DllSearchHardening.ps1',
    'Test-GenericOptimizationProfile.ps1',
    'Test-InstallerCompatibility.ps1',
    'Test-InstallerLifecycleSafety.ps1',
    'Test-MemoryPressureRelief.ps1',
    'Test-OptimizationElevationSafety.ps1',
    'Test-PerformanceCapture.ps1',
    'Test-PowerPlanProcessTimeout.ps1',
    'Test-ReleaseUpdateChannel.ps1',
    'Test-ResponsiveLayout.ps1',
    'Test-SigningAndCiContracts.ps1',
    'Test-SessionInsights.ps1',
    'Test-UninstallCleanupSafety.ps1',
    'Test-UpdaterFailureRecovery.ps1',
    'Test-UpdaterInstallerValidation.ps1',
    'Test-UpdateRollbackSafety.ps1'
)

# This test performs a real machine-wide install/update/uninstall lifecycle and
# must run only in its dedicated elevated GitHub-hosted runner step.
$standaloneMachineTests = @(
    'Test-InstallerLifecycleE2E.ps1'
)

function Resolve-WindowsPowerShell {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath -ErrorAction Stop).Path
    }
    if ([string]::IsNullOrWhiteSpace($env:WINDIR)) {
        throw 'WINDIR is unavailable; Windows PowerShell 5.1 cannot be located.'
    }

    $systemFolder = if (
        [Environment]::Is64BitOperatingSystem -and
        -not [Environment]::Is64BitProcess
    ) {
        'Sysnative'
    }
    else {
        'System32'
    }
    $candidate = Join-Path $env:WINDIR (
        $systemFolder + '\WindowsPowerShell\v1.0\powershell.exe')
    return (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
}

function Assert-ScriptParses {
    param([Parameter(Mandatory = $true)][string]$Path)

    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        $messages = @($parseErrors | ForEach-Object {
            $_.Extent.StartLineNumber.ToString() + ':' +
            $_.Extent.StartColumnNumber.ToString() + ' ' + $_.Message
        })
        throw "PowerShell syntax validation failed for $Path`n$(
            [string]::Join([Environment]::NewLine, $messages))"
    }
}

function Invoke-IsolatedTest {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][IO.FileInfo]$TestFile,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    if ($TestFile.FullName.Contains('"')) {
        throw "A test path contains an unsupported quote: $($TestFile.FullName)"
    }

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $ExecutablePath
    $startInfo.Arguments =
        '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
        $TestFile.FullName + '"'
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    # A clean build is intentionally non-deterministic, so only the signed
    # release-channel contract may inspect the immutable pre-build snapshot.
    # Every other regression test must exercise the binaries in dist.
    if ($TestFile.Name -cne 'Test-ReleaseUpdateChannel.ps1') {
        [void]$startInfo.EnvironmentVariables.Remove(
            'BOOSTIX_RELEASE_SNAPSHOT_DIRECTORY')
    }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $exitCode = -1
    $standardOutput = ''
    $standardError = ''

    try {
        if (-not $process.Start()) {
            throw "Could not start $($TestFile.Name)."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            try { $process.Kill() }
            catch { }
        }
        $process.WaitForExit()
        $standardOutput = $stdoutTask.Result
        $standardError = $stderrTask.Result
        if (-not $timedOut) {
            $exitCode = $process.ExitCode
        }
    }
    finally {
        $stopwatch.Stop()
        $process.Dispose()
    }

    $status = if ($timedOut) {
        'TimedOut'
    }
    elseif ($exitCode -eq 0) {
        'Passed'
    }
    else {
        'Failed'
    }

    return [pscustomobject]@{
        Name = $TestFile.Name
        Status = $status
        ExitCode = $exitCode
        DurationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        StandardOutput = $standardOutput
        StandardError = $standardError
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$testHost = Resolve-WindowsPowerShell -RequestedPath $PowerShellPath
$hostIdentity = & $testHost -NoLogo -NoProfile -NonInteractive -Command (
    '[Console]::Write($PSVersionTable.PSEdition + ''|'' + ' +
    '$PSVersionTable.PSVersion.ToString() + ''|'' + ' +
    '[IntPtr]::Size.ToString())')
if ($LASTEXITCODE -ne 0 -or $hostIdentity -notmatch '^Desktop\|5\.1(\.|$).*\|8$') {
    throw "Tests require 64-bit Windows PowerShell 5.1; detected '$hostIdentity'."
}

$tests = @(
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Test-*.ps1' -File |
        Where-Object { $standaloneMachineTests -notcontains $_.Name } |
        Sort-Object Name
)
if ($tests.Count -eq 0) {
    throw 'No Test-*.ps1 files were discovered.'
}

$discoveredNames = @($tests | ForEach-Object { $_.Name })
$missingTests = @($requiredTests | Where-Object { $discoveredNames -notcontains $_ })
if ($missingTests.Count -ne 0) {
    throw 'Required regression tests are missing: ' +
        [string]::Join(', ', $missingTests)
}

$duplicateNames = @(
    $tests |
        Group-Object { $_.Name.ToUpperInvariant() } |
        Where-Object { $_.Count -ne 1 }
)
if ($duplicateNames.Count -ne 0) {
    throw 'Case-insensitive duplicate test names were discovered.'
}

foreach ($test in $tests) {
    if (($test.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Test scripts cannot be reparse points: $($test.FullName)"
    }
    Assert-ScriptParses -Path $test.FullName
}

Write-Host (
    'Running ' + $tests.Count.ToString() +
    ' mandatory regression tests with ' + $testHost)

$results = New-Object 'System.Collections.Generic.List[object]'
foreach ($test in $tests) {
    Write-Host ('[ RUN      ] ' + $test.Name)
    $result = Invoke-IsolatedTest `
        -ExecutablePath $testHost `
        -TestFile $test `
        -WorkingDirectory $projectRoot `
        -TimeoutSeconds $TestTimeoutSeconds
    [void]$results.Add($result)

    foreach ($line in @($result.StandardOutput -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-Host ('  ' + $line)
        }
    }
    foreach ($line in @($result.StandardError -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-Warning ($test.Name + ': ' + $line)
        }
    }

    if ($result.Status -eq 'Passed') {
        Write-Host (
            '[       OK ] ' + $test.Name + ' (' +
            $result.DurationSeconds.ToString('0.000') + ' s)')
    }
    else {
        Write-Host (
            '[  FAILED  ] ' + $test.Name + ' [' + $result.Status + '] (' +
            $result.DurationSeconds.ToString('0.000') + ' s)')
    }
}

if ($results.Count -ne $tests.Count) {
    throw "Only $($results.Count) of $($tests.Count) discovered tests were executed."
}

$passed = @($results | Where-Object { $_.Status -eq 'Passed' }).Count
$failed = $results.Count - $passed
$summary = [pscustomobject]@{
    GeneratedUtc = [DateTime]::UtcNow.ToString('o')
    Host = $hostIdentity
    Discovered = $tests.Count
    Executed = $results.Count
    Passed = $passed
    Failed = $failed
    Results = $results.ToArray()
}

if (-not [string]::IsNullOrWhiteSpace($ResultsPath)) {
    $fullResultsPath = [IO.Path]::GetFullPath($ResultsPath)
    $resultsDirectory = Split-Path -Parent $fullResultsPath
    if (-not [string]::IsNullOrWhiteSpace($resultsDirectory)) {
        [void](New-Item -ItemType Directory -Path $resultsDirectory -Force)
    }
    [IO.File]::WriteAllText(
        $fullResultsPath,
        ($summary | ConvertTo-Json -Depth 5),
        (New-Object Text.UTF8Encoding($false)))
    Write-Host ('Machine-readable results: ' + $fullResultsPath)
}

Write-Host (
    'Summary: discovered=' + $tests.Count +
    ', executed=' + $results.Count +
    ', passed=' + $passed +
    ', failed=' + $failed)

if ($failed -ne 0) {
    $failedNames = @(
        $results |
            Where-Object { $_.Status -ne 'Passed' } |
            ForEach-Object { $_.Name + ' (' + $_.Status + ')' }
    )
    throw 'Regression suite failed: ' + [string]::Join(', ', $failedNames)
}
