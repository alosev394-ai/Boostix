[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = & (Join-Path $projectRoot 'tools\Get-BoostixCompiler.ps1')
$languageOptions = @(
    '/langversion:12.0',
    '/nullable:warnings',
    '/warnaserror:nullable'
)

if ($compiler.StartsWith($env:WINDIR, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The build must not use the compiler inside .NET Framework: $compiler"
}

# One script left on the old compiler would break as soon as a source file it
# compiles uses newer syntax, so nothing may name that compiler directly.
$scripts = @(
    Get-Item -LiteralPath (Join-Path $projectRoot 'build.ps1')
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.ps1' -File
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tools') -Filter '*.ps1' -File -Recurse
) | Where-Object {
    $_.FullName -cne $PSCommandPath -and $_.Name -cne 'Get-BoostixCompiler.ps1'
}
foreach ($script in $scripts) {
    if ([IO.File]::ReadAllText($script.FullName) -match '(?i)\bcsc\.exe\b') {
        throw "$($script.Name) names a compiler instead of asking Get-BoostixCompiler.ps1."
    }
}

$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
foreach ($option in $languageOptions) {
    $uses = [regex]::Matches($build, [regex]::Escape("'$option'")).Count
    if ($uses -ne 2) {
        throw "build.ps1 must pass $option to the application and the installer; found $uses."
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'boostix-compiler-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $tempRoot)

function Invoke-Probe {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Source
    )
    $sourcePath = Join-Path $tempRoot "$Name.cs"
    $outputPath = Join-Path $tempRoot "$Name.exe"
    [IO.File]::WriteAllText($sourcePath, $Source)
    $arguments = @('/nologo', '/target:exe', "/out:$outputPath", $sourcePath) +
        $languageOptions
    $output = @(& $compiler @arguments)
    return New-Object PSObject -Property @{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine)
        Path = $outputPath
    }
}

try {
    # Newer syntax must both compile and run on .NET Framework 4.8.
    $modern = Invoke-Probe -Name 'modern' -Source @'
using System;
using System.Collections.Generic;

internal static class Probe
{
    private static string Describe(object value) => value switch
    {
        int number when number > 1 => $"many {number}",
        string text => text?.Trim() ?? string.Empty,
        _ => "other"
    };

    private static (int Count, string Name) Pair() => (2, "pair");

    private static void Main()
    {
        List<string> names = new() { Describe(2), Describe(" text ") };
        var (count, name) = Pair();
        Console.WriteLine($"{names.Count}|{names[0]}|{names[1]}|{count}|{name}");
    }
}
'@
    if ($modern.ExitCode -ne 0) {
        throw "Modern C# did not compile:`n$($modern.Output)"
    }
    $printed = (& $modern.Path) -join ''
    if ($LASTEXITCODE -ne 0 -or $printed -cne '2|many 2|text|2|pair') {
        throw "Modern C# ran wrong on .NET Framework: '$printed' (exit $LASTEXITCODE)."
    }

    # A value the compiler can see is null must stop the build, not just warn.
    $unsafe = Invoke-Probe -Name 'unsafe' -Source @'
internal static class Probe
{
    private static void Main()
    {
        string text = null;
        System.Console.WriteLine(text.Length);
    }
}
'@
    if ($unsafe.ExitCode -eq 0 -or $unsafe.Output -notmatch 'error CS8602') {
        throw "A null dereference must fail the build:`n$($unsafe.Output)"
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Build compiler tests passed.'
