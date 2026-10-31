[CmdletBinding()]
param()

# Release tooling that runs outside the regular build: the staged build that
# SignPath signs one file at a time, and the Windows Sandbox clean-install
# check. Neither SignPath nor Sandbox is needed to run this test.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$systemPowerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Description)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing release tooling contract: $Description"
    }
}

foreach ($script in @(
        'build.ps1',
        'tools\Sandbox\Invoke-SandboxInstallTest.ps1',
        'tools\Sandbox\SandboxInstallCheck.ps1')) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $projectRoot $script), [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        throw "$script does not parse: $($errors[0].Message)"
    }
}

# Windows PowerShell 5.1 reads a script without a BOM in the machine's ANSI
# code page, so UTF-8 text means one thing on a Russian PC (1251) and another
# on the CI runners (1252). Scripts stay ASCII or carry a UTF-8 BOM.
$ambiguousScripts = @(Get-ChildItem -LiteralPath $projectRoot -Recurse -File |
        Where-Object { $_.Extension -in '.ps1', '.psm1' -and $_.FullName -notmatch '\\(\.git|dist|work)\\' } |
        Where-Object {
            $bytes = [IO.File]::ReadAllBytes($_.FullName)
            $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
            -not $hasBom -and @($bytes | Where-Object { $_ -gt 127 }).Count -gt 0
        } |
        ForEach-Object { $_.FullName.Substring($projectRoot.Length + 1) })
if ($ambiguousScripts.Count -gt 0) {
    throw ('PowerShell scripts with non-ASCII text and no UTF-8 BOM read differently per code page: ' +
        ($ambiguousScripts -join ', '))
}

$brandSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'ProductBrand.cs'))
$version = [regex]::Match($brandSource, 'ProductVersion\s*=\s*"(?<v>[0-9.]+)"').Groups['v'].Value

# --- Staged build for SignPath. ---
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
Assert-Contains $build "[ValidateSet('All', 'App', 'Setup', 'Bridge', 'Release')]" 'build stages'
Assert-Contains $build '$signature.Status -ne [Management.Automation.SignatureStatus]::Valid' 'signed input must be valid'
Assert-Contains $build '$fileVersion -cne "$releaseVersion.0"' 'signed input must be this release'
Assert-Contains $build "`$cleansDist = `$Stage -in @('All', 'Release')" 'only full and release stages touch dist'
foreach ($stage in 'App', 'Setup', 'Bridge') {
    Assert-Contains $build ("if (`$Stage -eq '" + $stage + "') {") "stage $stage stops after its file"
}

# An unsigned app must never be built into an installer.
$unsigned = Join-Path $projectRoot 'dist\Boostix.exe'
if ((Get-AuthenticodeSignature -LiteralPath $unsigned).Status -eq 'Valid') {
    $unsigned = Join-Path ([Environment]::SystemDirectory) 'notepad.exe'
}
$distBefore = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'dist') -File |
    ForEach-Object { $_.Name + '|' + $_.Length + '|' + $_.LastWriteTimeUtc.Ticks })
# The refusal arrives on stderr, which Windows PowerShell turns into errors.
$ErrorActionPreference = 'Continue'
$output = & $systemPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'build.ps1') `
    -Stage Setup -SignedAppPath $unsigned 2>&1 | Out-String
$stageExitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($stageExitCode -eq 0) {
    throw 'The Setup stage accepted an app without a valid signature.'
}
if ($output -notmatch 'Authenticode status|expected ' + [regex]::Escape($version)) {
    throw "The Setup stage failed for another reason:`n$output"
}
$distAfter = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'dist') -File |
    ForEach-Object { $_.Name + '|' + $_.Length + '|' + $_.LastWriteTimeUtc.Ticks })
if (($distBefore -join ';') -cne ($distAfter -join ';')) {
    throw 'A refused stage changed dist.'
}

# --- The SignPath workflow. ---
$workflow = [IO.File]::ReadAllText((Join-Path $projectRoot '.github\workflows\signpath-release.yml'))
if ($workflow -notmatch '(?m)^on:\s*\r?\n\s+workflow_dispatch:\s*\r?\n\s*\r?\npermissions:') {
    throw 'The signing workflow must run only by hand.'
}
Assert-Contains $workflow 'persist-credentials: false' 'checkout keeps no token'
Assert-Contains $workflow 'SignPath is not set up for this repository yet' 'stops clearly before setup'
$stageOrder = @('-Stage App', '-Stage Setup', '-Stage Bridge', '-Stage Release') |
    ForEach-Object { $workflow.IndexOf($_, [StringComparison]::Ordinal) }
for ($i = 0; $i -lt $stageOrder.Count; $i++) {
    if ($stageOrder[$i] -lt 0 -or ($i -gt 0 -and $stageOrder[$i] -lt $stageOrder[$i - 1])) {
        throw 'The signing workflow must build App, Setup, Bridge, Release in that order.'
    }
}
$slugs = @([regex]::Matches($workflow, 'artifact-configuration-slug:\s*(\S+)') |
    ForEach-Object { $_.Groups[1].Value })
if (($slugs -join ',') -cne 'boostix-app,boostix-setup,boostix-bridge') {
    throw "Unexpected SignPath artifact configurations: $($slugs -join ', ')"
}
foreach ($slug in $slugs) {
    $configPath = Join-Path $projectRoot ('.signpath\artifact-configurations\' + $slug + '.xml')
    [xml]$config = [IO.File]::ReadAllText($configPath)
    $peFile = $config.'artifact-configuration'.'zip-file'.'pe-file'
    if (-not ($peFile.ChildNodes | Where-Object { $_.LocalName -eq 'authenticode-sign' })) {
        throw "$slug.xml does not Authenticode-sign its file."
    }
}
$policy = [IO.File]::ReadAllText((Join-Path $projectRoot 'CODE_SIGNING.md'))
Assert-Contains $policy 'certificate by [SignPath Foundation]' 'SignPath Foundation attribution'
Assert-Contains $policy '## Privacy' 'privacy statement'
Assert-Contains $policy '## Team roles' 'signing roles'

# --- Windows Sandbox clean-install check. ---
$check = [IO.File]::ReadAllText((Join-Path $projectRoot 'tools\Sandbox\SandboxInstallCheck.ps1'))
Assert-Contains $check "-ArgumentList '/quiet'" 'silent install'
Assert-Contains $check "-ArgumentList '/uninstall', '/quiet'" 'silent uninstall'
Assert-Contains $check "-ArgumentList '--lang=en'" 'English start'

$wsbPath = & $systemPowerShell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $projectRoot 'tools\Sandbox\Invoke-SandboxInstallTest.ps1') `
    -InstallerPath (Join-Path $projectRoot 'dist\Boostix-Setup-Latest.exe') -PrepareOnly
if ($LASTEXITCODE -ne 0) {
    throw 'Preparing the sandbox run failed.'
}
$wsbPath = [string]($wsbPath | Select-Object -Last 1)
$runDirectory = Split-Path -Parent $wsbPath
try {
    if (-not $runDirectory.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "The sandbox run folder left TEMP: $runDirectory"
    }
    [xml]$wsb = [IO.File]::ReadAllText($wsbPath)
    $configuration = $wsb.Configuration
    if ($configuration.Networking -cne 'Disable') {
        throw 'The sandbox must run offline by default.'
    }
    $folders = @($configuration.MappedFolders.MappedFolder)
    if ($folders.Count -ne 2) {
        throw "Expected two mapped folders, found $($folders.Count)."
    }
    $inputFolder = $folders | Where-Object { $_.SandboxFolder -eq 'C:\BoostixInput' }
    $resultsFolder = $folders | Where-Object { $_.SandboxFolder -eq 'C:\BoostixResults' }
    if (-not $inputFolder -or $inputFolder.ReadOnly -cne 'true') {
        throw 'The installer must be mapped read-only.'
    }
    if (-not $resultsFolder -or $resultsFolder.ReadOnly -cne 'false') {
        throw 'The results folder must be writable.'
    }
    foreach ($name in 'Boostix-Setup-Latest.exe', 'SandboxInstallCheck.ps1') {
        if (-not (Test-Path -LiteralPath (Join-Path $inputFolder.HostFolder $name))) {
            throw "The sandbox input is missing $name."
        }
    }
    $command = [string]$configuration.LogonCommand.Command
    foreach ($part in @('C:\BoostixInput\SandboxInstallCheck.ps1', "-ExpectedVersion $version", '-ShutDownWhenDone')) {
        Assert-Contains $command $part "sandbox logon command: $part"
    }
}
finally {
    Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

'Release tooling checks passed.'
