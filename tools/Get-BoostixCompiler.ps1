# Prints the path of the Roslyn C# compiler from Visual Studio or its Build
# Tools. The compiler inside .NET Framework stops at C# 5. Boostix still runs
# on .NET Framework 4.8: only the language moves, not the runtime.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$vswhere = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) `
    'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "Visual Studio or its Build Tools are required: $vswhere was not found."
}

$found = @(& $vswhere `
    -latest `
    -products * `
    -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\Roslyn\csc.exe')
if ($LASTEXITCODE -ne 0) {
    throw "vswhere failed with exit code $LASTEXITCODE."
}
if ($found.Count -ne 1 -or
    -not (Test-Path -LiteralPath $found[0] -PathType Leaf)) {
    throw 'The Roslyn C# compiler was not found in Visual Studio or its Build Tools.'
}
$found[0]
