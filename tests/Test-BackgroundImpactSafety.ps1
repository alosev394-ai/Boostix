$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'Boostix\BackgroundImpact.cs'
$source = [IO.File]::ReadAllText($sourcePath)

foreach ($forbidden in @(
    'Process.Kill(',
    '.Kill()',
    'Stop-Process',
    'EmptyWorkingSet',
    'SetProcessWorkingSetSize'
)) {
    if ($source.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Background analyzer contains a destructive contract: $forbidden"
    }
}
foreach ($required in @(
    'CloseMainWindow()',
    'StartTimeUtc',
    'GetProcessIoCounters',
    'ConfigureAwait(false)',
    'CancellationToken'
)) {
    if (-not $source.Contains($required)) {
        throw "Background analyzer safety contract is missing: $required"
    }
}

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$testRoot = Join-Path $env:TEMP (
    'Boostix-BackgroundImpact-Test-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)
$harnessPath = Join-Path $testRoot 'Harness.cs'
$outputPath = Join-Path $testRoot 'Harness.exe'
$harness = @'
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Boostix;

internal static class Harness
{
    private static int Main()
    {
        var empty = BackgroundImpactAnalyzer.RequestGracefulClose(null, 0);
        if (empty == null || empty.Count != 0) return 10;

        var invalid = BackgroundImpactAnalyzer.RequestGracefulClose(
            new[] { new BackgroundProcessIdentity { ProcessId = -1 } }, 0);
        if (invalid.Count != 1 || invalid[0].CloseRequested || invalid[0].Exited ||
            string.IsNullOrWhiteSpace(invalid[0].Message)) return 11;

        using (Process current = Process.GetCurrentProcess())
        {
            var measured = BackgroundImpactAnalyzer.MeasureAsync(
                1000,
                current.Id,
                CancellationToken.None).GetAwaiter().GetResult();
            if (measured == null || measured.Any(item =>
                    item == null || item.Identity == null ||
                    item.Identity.ProcessId == current.Id ||
                    item.CpuPercent < 0 || item.PrivateBytes < 0 ||
                    item.WorkingSetBytes < 0 || item.ReadBytes < 0 ||
                    item.WriteBytes < 0)) return 12;
        }

        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            BackgroundImpactAnalyzer.MeasureAsync(1000, -1, cancelled.Token)
                .GetAwaiter().GetResult();
            return 13;
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }
}
'@
[IO.File]::WriteAllText(
    $harnessPath,
    $harness,
    (New-Object Text.UTF8Encoding($false)))

try {
    $output = & $compiler /nologo /target:exe /optimize+ `
        /reference:System.dll /reference:System.Core.dll `
        "/out:$outputPath" $sourcePath $harnessPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Background impact harness did not compile:`n$($output -join [Environment]::NewLine)"
    }
    & $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Background impact harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'Background impact safety test passed.'
