[CmdletBinding()]
param(
    [string]$PreviousTag = 'v3.2.1',
    [string]$ManifestPath,
    [string]$InstallerPath
)

# Proves that the released app at $PreviousTag accepts this build as its next
# update, using that release's own update code: the signed update-v3.json
# (key v2), the version step and the installer checks (size, SHA-256, PE
# header, ProductName, FileVersion). Nothing is downloaded or started.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ManifestPath) {
    $ManifestPath = Join-Path $projectRoot 'update-v3.json'
}
$signaturePath = $ManifestPath + '.sig'
$brand = [IO.File]::ReadAllText((Join-Path $projectRoot 'ProductBrand.cs'))
$version = [regex]::Match($brand, 'ProductVersion\s*=\s*"(?<v>[0-9.]+)"').Groups['v'].Value
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot ("dist\Boostix-Setup-$version.exe")
}
foreach ($path in @($ManifestPath, $signaturePath, $InstallerPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing release file: $path"
    }
}

$work = Join-Path $env:TEMP ('Boostix-UpdateFromPrevious-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $work)
try {
    # git show through cmd keeps the executable's bytes intact.
    $previousApp = Join-Path $work 'previous-Boostix.exe'
    $git = (Get-Command git -ErrorAction Stop).Source
    cmd /c "`"$git`" -C `"$projectRoot`" show $($PreviousTag):dist/Boostix.exe > `"$previousApp`""
    if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $previousApp).Length -lt 100000) {
        throw "Could not read dist/Boostix.exe from $PreviousTag."
    }
    $previousVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($previousApp).FileVersion

    Add-Type -AssemblyName PresentationFramework
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($previousApp))
    $flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
    $overlay = $assembly.GetType('Boostix.UpdateFlowOverlay', $true)
    $verify = $overlay.GetMethod('VerifyAndParseManifest', $flags)
    $validate = $overlay.GetMethod('ValidateHeldInstaller', $flags)
    if (-not $verify -or -not $validate) {
        throw "$PreviousTag does not expose the update checks this tool relies on."
    }

    $manifest = $null
    try {
        $manifest = $verify.Invoke($null, [object[]]@(
            [byte[]][IO.File]::ReadAllBytes($ManifestPath),
            [byte[]][IO.File]::ReadAllBytes($signaturePath)))
    }
    catch {
        throw "$PreviousTag rejects the manifest: $($_.Exception.InnerException.Message)"
    }
    $manifestType = $manifest.GetType()
    $fieldFlags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
    $offered = [string]$manifestType.GetField('Version', $fieldFlags).GetValue($manifest)
    $installerUrl = [string]$manifestType.GetField('InstallerUrl', $fieldFlags).GetValue($manifest)
    if ($offered -cne $version) {
        throw "The manifest offers $offered; this build is $version."
    }
    if ([version]$offered -le [version]($previousVersion -replace '\.0$', '')) {
        throw "$offered is not newer than $previousVersion, so $PreviousTag would not update."
    }

    $stream = [IO.File]::Open($InstallerPath, 'Open', 'Read', 'Read')
    try {
        $validate.Invoke($null, [object[]]@($manifest, [string]$InstallerPath, $stream))
    }
    catch {
        throw "$PreviousTag rejects the installer: $($_.Exception.InnerException.Message)"
    }
    finally {
        $stream.Dispose()
    }

    "PASS: Boostix $previousVersion ($PreviousTag) accepts $offered from $installerUrl with its own manifest, signature and installer checks."
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
