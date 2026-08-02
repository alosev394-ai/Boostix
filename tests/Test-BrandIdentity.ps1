[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandPath = Join-Path $projectRoot 'ProductBrand.cs'
$programPath = Join-Path $projectRoot 'Boostix\Program.cs'
$centerPath = Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'
$updatePath = Join-Path $projectRoot 'Boostix\UpdateFlow.cs'
$sessionScriptPath = Join-Path $projectRoot 'outputs\Boost-Session.ps1'
$applyScriptPath = Join-Path $projectRoot 'outputs\MaxFPS-Apply.ps1'
$buildPath = Join-Path $projectRoot 'build.ps1'
$applicationManifestPath = Join-Path $projectRoot 'Boostix\app.manifest'
$installerManifestPath = Join-Path $projectRoot 'BoostixInstaller\app.manifest'

foreach ($requiredPath in @(
    $brandPath,
    $programPath,
    $centerPath,
    $updatePath,
    $sessionScriptPath,
    $applyScriptPath,
    $buildPath,
    $applicationManifestPath,
    $installerManifestPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required Boostix source is missing: $requiredPath"
    }
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'MajesticBoost')) {
    throw 'The application source directory still uses the legacy product name.'
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'MajesticBoostInstaller')) {
    throw 'The installer source directory still uses the legacy product name.'
}

function Get-BrandStringConstant {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = [regex]::Matches(
        $Source,
        '(?m)^\s*public\s+const\s+string\s+' +
            [regex]::Escape($Name) +
            '\s*=\s*"(?<value>[^"]+)"\s*;')
    if ($matches.Count -ne 1) {
        throw "ProductBrand.cs must define exactly one $Name string constant."
    }
    return $matches[0].Groups['value'].Value
}

$brand = [IO.File]::ReadAllText($brandPath)
$productName = Get-BrandStringConstant -Source $brand -Name 'ProductName'
$companyName = Get-BrandStringConstant -Source $brand -Name 'CompanyName'
$releaseVersion = Get-BrandStringConstant -Source $brand -Name 'ProductVersion'
$assemblyVersion = Get-BrandStringConstant -Source $brand -Name 'AssemblyVersion'
if ($releaseVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    $assemblyVersion -cne ($releaseVersion + '.0')) {
    throw 'ProductVersion and AssemblyVersion are inconsistent in ProductBrand.cs.'
}
foreach ($identityContract in @(
    'ProductName = "Boostix"',
    'ProductFileName = "Boostix"',
    'CompanyName = "Silas Suspect"',
    'ReleaseLabel = "BETA"',
    'AccentHex = "#FF7C3AED"',
    'AccentVisualHex = "#FF8B5CF6"',
    'AccentTextHex = "#FFA78BFA"'
)) {
    if (-not $brand.Contains($identityContract)) {
        throw "The central brand contract is missing: $identityContract"
    }
}

foreach ($manifestContract in @(
    @{
        Path = $applicationManifestPath
        Name = 'SilasSuspect.Boostix'
    },
    @{
        Path = $installerManifestPath
        Name = 'SilasSuspect.Boostix.Setup'
    }
)) {
    [xml]$manifest = [IO.File]::ReadAllText($manifestContract.Path)
    $namespaceManager = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
    $namespaceManager.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
    $identity = $manifest.SelectSingleNode(
        '/asm:assembly/asm:assemblyIdentity',
        $namespaceManager)
    if (-not $identity -or
        $identity.GetAttribute('version') -cne $assemblyVersion -or
        $identity.GetAttribute('name') -cne $manifestContract.Name) {
        throw "Manifest identity is inconsistent with ProductBrand.cs: $($manifestContract.Path)"
    }
}

$program = [IO.File]::ReadAllText($programPath)
$center = [IO.File]::ReadAllText($centerPath)
$update = [IO.File]::ReadAllText($updatePath)
$sessionScript = [IO.File]::ReadAllText($sessionScriptPath)
$applyScript = [IO.File]::ReadAllText($applyScriptPath)
$build = [IO.File]::ReadAllText($buildPath)

if (-not $program.Contains('MakeText("BOOSTIX", 30')) {
    throw 'The main page title is not the required BOOSTIX wordmark.'
}
if ($program.Contains('MakeText("BOOST",')) {
    throw 'The retired two-line Boost title returned to the main page.'
}
if (-not $program.Contains('AssemblyCompany(ProductBrand.CompanyName)') -or
    -not $program.Contains('"by Silas Suspect"')) {
    throw 'The application author identity is not sourced consistently.'
}

foreach ($source in @($program, $center)) {
    foreach ($match in [regex]::Matches(
        $source,
        'SetAutomationId\([\s\S]{0,180}?"(?<id>[A-Za-z0-9_.]+)"\)')) {
        $automationId = $match.Groups['id'].Value
        if (-not $automationId.StartsWith(
            'Boostix.',
            [StringComparison]::Ordinal)) {
            throw "A public AutomationId has the wrong prefix: $automationId"
        }
    }
}

foreach ($forbiddenPink in @(
    '#FFE81C5A',
    '#FFEF185B',
    '#FFE71857'
)) {
    if ($program.Contains($forbiddenPink) -or
        $center.Contains($forbiddenPink)) {
        throw "A retired pink accent remains in the Boostix UI: $forbiddenPink"
    }
}

foreach ($genericSource in @(
    @{ Name = 'Boost session'; Text = $sessionScript },
    @{ Name = 'optimization apply'; Text = $applyScript },
    @{ Name = 'Boost Center'; Text = $center }
)) {
    if ($genericSource.Text -match '(?i)\b(?:Majestic|GTA|Grand Theft|Rockstar)\b') {
        throw "$($genericSource.Name) is still tied to the retired game/launcher identity."
    }
}
foreach ($launcherContract in @(
    'majestic://',
    'majestic-rp.ru',
    'LaunchMajestic',
    'StartMajestic',
    'DoNotLaunchMajestic'
)) {
    if ($program.Contains($launcherContract) -or
        $sessionScript.Contains($launcherContract)) {
        throw "Automatic third-party launcher coupling remains: $launcherContract"
    }
}

foreach ($requiredUpdateContract in @(
    'https://api.github.com/repos/alosev394-ai/Boostix',
    'https://raw.githubusercontent.com/alosev394-ai/Boostix/',
    'boostixInstallerUrl',
    'boostixSha256',
    'boostixSize'
)) {
    if (-not $update.Contains($requiredUpdateContract) -and
        -not $build.Contains($requiredUpdateContract)) {
        throw "The Boostix update contract is missing: $requiredUpdateContract"
    }
}
if (-not $update.Contains(
    'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/MajesticBoost-Setup-')) {
    throw 'The exact 1.8.x compatibility bridge URL was accidentally removed.'
}

foreach ($requiredBuildContract in @(
    'Boostix.exe',
    'Boostix-Setup-$releaseVersion.exe',
    'MajesticBoost-Setup-$releaseVersion.exe'
)) {
    if (-not $build.Contains($requiredBuildContract)) {
        throw "The release build contract is missing: $requiredBuildContract"
    }
}

$dist = Join-Path $projectRoot 'dist'
$appPath = Join-Path $dist 'Boostix.exe'
$setupPath = Join-Path $dist ('Boostix-Setup-' + $releaseVersion + '.exe')
$latestPath = Join-Path $dist 'Boostix-Setup-Latest.exe'
$bridgePath = Join-Path $dist (
    'MajesticBoost-Setup-' + $releaseVersion + '.exe')
foreach ($artifactPath in @($appPath, $setupPath, $latestPath, $bridgePath)) {
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Required release artifact is missing: $artifactPath"
    }
}

foreach ($artifactPath in @($appPath, $setupPath, $latestPath)) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($artifactPath)
    if ($version.ProductName -cne $productName -or
        $version.CompanyName -cne $companyName -or
        $version.ProductVersion -cne $assemblyVersion) {
        throw "Incorrect Boostix metadata in $artifactPath"
    }
}
$bridgeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($bridgePath)
if ($bridgeVersion.ProductName -cne 'Majestic Boost' -or
    $bridgeVersion.CompanyName -cne $companyName -or
    $bridgeVersion.ProductVersion -cne $assemblyVersion) {
    throw 'The isolated 1.8.x compatibility bridge metadata is incorrect.'
}

'Boostix brand identity test passed.'
