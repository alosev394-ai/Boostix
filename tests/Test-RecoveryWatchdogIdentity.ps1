[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$installerSource = Join-Path $projectRoot 'BoostixInstaller\Program.cs'
$brandSource = Join-Path $projectRoot 'ProductBrand.cs'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler was not found: $compiler"
}

$source = [IO.File]::ReadAllText($installerSource)
foreach ($required in @(
    'parent.StartTime.ToUniversalTime().Ticks',
    'parentStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture)',
    'TryParseRecoveryArguments(',
    'WaitForExactRecoveryParent(',
    'IsExactRecoveryParent(',
    'hasExactParentIdentity',
    'values.Length != 3 && values.Length != 4',
    'NumberStyles.None'
)) {
    if (-not $source.Contains($required)) {
        throw "Recovery watchdog identity contract is missing: $required"
    }
}
if ($source.Contains(
        'parent.WaitForExit(UpdateRecoveryParentWaitMilliseconds);')) {
    throw 'Recovery watchdog still has an unconditional PID-only wait.'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-RecoveryWatchdogIdentity-' + [Guid]::NewGuid().ToString('N'))
$installerHarness = Join-Path $temporaryRoot 'InstallerHarness.dll'
$childSource = Join-Path $temporaryRoot 'WaitChild.cs'
$childExecutable = Join-Path $temporaryRoot 'WaitChild.exe'
$children = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'

$childCode = @'
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

internal static class WaitChild
{
    public static int Main(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            return 2;
        }

        int delay;
        if (!int.TryParse(
                arguments[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out delay) ||
            delay < 0)
        {
            return 3;
        }

        File.WriteAllText(
            arguments[1],
            "ready",
            new UTF8Encoding(false));
        Thread.Sleep(delay);
        return 0;
    }
}
'@

function Get-InnerException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while (($current -is [Reflection.TargetInvocationException] -or
        $current -is [Management.Automation.MethodInvocationException]) -and
        $current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

function Invoke-RecoveryParser {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $invokeArguments = [object[]]@($Arguments, $null, 0, 0L, $false)
    try {
        $accepted = [bool]$Method.Invoke($null, $invokeArguments)
    }
    catch {
        throw (Get-InnerException -Exception $_.Exception)
    }
    return [pscustomobject]@{
        Accepted = $accepted
        TransactionId = [string]$invokeArguments[1]
        ProcessId = [int]$invokeArguments[2]
        StartTicks = [long]$invokeArguments[3]
        HasExactIdentity = [bool]$invokeArguments[4]
    }
}

function Invoke-ParentWait {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][long]$StartTicks,
        [Parameter(Mandatory = $true)][bool]$HasExactIdentity,
        [Parameter(Mandatory = $true)][int]$TimeoutMilliseconds
    )

    $arguments = [object[]]@(
        $ProcessId,
        $StartTicks,
        $HasExactIdentity,
        $TimeoutMilliseconds)
    try {
        return [bool]$Method.Invoke($null, $arguments)
    }
    catch {
        throw (Get-InnerException -Exception $_.Exception)
    }
}

function Start-WaitChild {
    param([Parameter(Mandatory = $true)][int]$DelayMilliseconds)

    $readyPath = Join-Path $temporaryRoot (
        'ready-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $childExecutable
    $startInfo.Arguments = (
        $DelayMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture) +
        ' "' + $readyPath.Replace('"', '\"') + '"')
    $startInfo.WorkingDirectory = $temporaryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'The deterministic watchdog child could not be started.'
    }
    $children.Add($process)

    $readyTimer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $readyPath -PathType Leaf) -and
        -not $process.HasExited -and
        $readyTimer.ElapsedMilliseconds -lt 5000) {
        Start-Sleep -Milliseconds 10
        $process.Refresh()
    }
    if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
        throw 'The deterministic watchdog child did not become ready.'
    }

    return [pscustomobject]@{
        Process = $process
        StartTicks = $process.StartTime.ToUniversalTime().Ticks
    }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    [IO.File]::WriteAllText(
        $childSource,
        $childCode,
        [Text.UTF8Encoding]::new($false))

    $installerCompilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$installerHarness" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Security.dll `
        $brandSource `
        $installerSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Installer watchdog harness did not compile:`r`n$($installerCompilerOutput -join [Environment]::NewLine)"
    }

    $childCompilerOutput = & $compiler `
        /nologo `
        /target:exe `
        /utf8output `
        "/out:$childExecutable" `
        /reference:System.dll `
        $childSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Deterministic child did not compile:`r`n$($childCompilerOutput -join [Environment]::NewLine)"
    }

    $assembly = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($installerHarness))
    $engineType = $assembly.GetType(
        'BoostixSetup.InstallerEngine',
        $true,
        $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $parseMethod = $engineType.GetMethod(
        'TryParseRecoveryArguments',
        $flags)
    $waitMethod = $engineType.GetMethod(
        'WaitForExactRecoveryParent',
        $flags)
    if (-not $parseMethod -or -not $waitMethod) {
        throw 'Compiled watchdog parser/wait seam was not found.'
    }

    $transactionId = '0123456789abcdef0123456789abcdef'
    $current = [Diagnostics.Process]::GetCurrentProcess()
    try {
        $validTicks = $current.StartTime.ToUniversalTime().Ticks
    }
    finally {
        $current.Dispose()
    }
    $valid = Invoke-RecoveryParser `
        -Method $parseMethod `
        -Arguments @(
            '/update-recovery',
            $transactionId,
            '1234',
            $validTicks.ToString([Globalization.CultureInfo]::InvariantCulture))
    if (-not $valid.Accepted -or
        $valid.TransactionId -cne $transactionId -or
        $valid.ProcessId -ne 1234 -or
        $valid.StartTicks -ne $validTicks -or
        -not $valid.HasExactIdentity) {
        throw 'A canonical PID + StartTime watchdog invocation was rejected.'
    }

    $legacy = Invoke-RecoveryParser `
        -Method $parseMethod `
        -Arguments @('/update-recovery', $transactionId, '1234')
    if (-not $legacy.Accepted -or
        $legacy.HasExactIdentity -or
        $legacy.StartTicks -ne 0) {
        throw 'A legacy watchdog invocation was not retained as identity-unproven.'
    }

    foreach ($invalid in @(
        [string[]]@('/update-recovery', $transactionId.ToUpperInvariant(), '1234', "$validTicks"),
        [string[]]@('/update-recovery', $transactionId, '0', "$validTicks"),
        [string[]]@('/update-recovery', $transactionId, '1234', '0'),
        [string[]]@('/update-recovery', $transactionId, '1234', '-1'),
        [string[]]@('/update-recovery', $transactionId, '1234', ('0' + "$validTicks")),
        [string[]]@('/update-recovery', $transactionId, '1234', '3155378976000000000'),
        [string[]]@('/update-recovery', $transactionId, '1234', "$validTicks", 'extra')
    )) {
        if ((Invoke-RecoveryParser -Method $parseMethod -Arguments $invalid).Accepted) {
            throw "Malformed watchdog arguments were accepted: $($invalid -join ' ')"
        }
    }

    $wrongStartChild = Start-WaitChild -DelayMilliseconds 1000
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $wrongStartWaited = Invoke-ParentWait `
        -Method $waitMethod `
        -ProcessId $wrongStartChild.Process.Id `
        -StartTicks ($wrongStartChild.StartTicks + 1L) `
        -HasExactIdentity $true `
        -TimeoutMilliseconds 5000
    $timer.Stop()
    if ($wrongStartWaited -or $timer.ElapsedMilliseconds -ge 500 -or
        $wrongStartChild.Process.HasExited) {
        throw 'A reused PID with a different StartTime delayed recovery.'
    }
    [void]$wrongStartChild.Process.WaitForExit(5000)

    $missingTimer = [Diagnostics.Stopwatch]::StartNew()
    $missingWaited = Invoke-ParentWait `
        -Method $waitMethod `
        -ProcessId ([int]::MaxValue) `
        -StartTicks $validTicks `
        -HasExactIdentity $true `
        -TimeoutMilliseconds 1000
    $missingTimer.Stop()
    if ($missingWaited -or $missingTimer.ElapsedMilliseconds -ge 500) {
        throw 'A missing/reused PID delayed recovery.'
    }

    $legacyChild = Start-WaitChild -DelayMilliseconds 1000
    $legacyTimer = [Diagnostics.Stopwatch]::StartNew()
    $legacyWaited = Invoke-ParentWait `
        -Method $waitMethod `
        -ProcessId $legacyChild.Process.Id `
        -StartTicks $legacyChild.StartTicks `
        -HasExactIdentity $false `
        -TimeoutMilliseconds 5000
    $legacyTimer.Stop()
    if ($legacyWaited -or $legacyTimer.ElapsedMilliseconds -ge 500 -or
        $legacyChild.Process.HasExited) {
        throw 'A legacy PID-only invocation was allowed to wait on a process.'
    }
    [void]$legacyChild.Process.WaitForExit(5000)

    $exactChild = Start-WaitChild -DelayMilliseconds 800
    $exactTimer = [Diagnostics.Stopwatch]::StartNew()
    $exactWaited = Invoke-ParentWait `
        -Method $waitMethod `
        -ProcessId $exactChild.Process.Id `
        -StartTicks $exactChild.StartTicks `
        -HasExactIdentity $true `
        -TimeoutMilliseconds 5000
    $exactTimer.Stop()
    if (-not $exactWaited -or
        -not $exactChild.Process.HasExited -or
        $exactTimer.ElapsedMilliseconds -lt 400) {
        throw 'The exact PID + StartTime parent was not awaited.'
    }

    Write-Host 'Recovery watchdog identity regression test passed.' `
        -ForegroundColor Green
}
finally {
    foreach ($child in $children) {
        try {
            if (-not $child.HasExited) {
                $child.Kill()
                [void]$child.WaitForExit(5000)
            }
        }
        catch {
        }
        finally {
            $child.Dispose()
        }
    }

    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemporaryRoot.StartsWith(
            $resolvedSystemTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
