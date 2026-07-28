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
if ($installerMain -lt 0 -or
    $installerHardening -le $installerMain -or
    $installerUi -le $installerHardening) {
    throw 'The elevated installer does not harden DLL search before WinForms starts.'
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
