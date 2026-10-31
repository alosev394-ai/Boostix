[CmdletBinding()]
param(
    # Defaults to dist\Boostix-Setup-<ProductVersion>.exe.
    [string]$InstallerPath,

    [ValidateRange(3, 60)]
    [int]$TimeoutMinutes = 15,

    # Leave the sandbox open afterwards to look around.
    [switch]$KeepOpen,

    # The check runs offline by default; the update check then fails quietly.
    [switch]$AllowNetwork,

    # Write the .wsb and the input folder, print where, and stop.
    [switch]$PrepareOnly
)

# Clean-install test in Windows Sandbox: a fresh, throw-away Windows that has
# never seen Boostix. The host only maps the installer in (read-only) and
# reads result.json back; install, first start and uninstall happen inside.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$brandSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'ProductBrand.cs'))
$versionMatch = [regex]::Match($brandSource, 'ProductVersion\s*=\s*"(?<v>[0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
    throw 'ProductVersion was not found in ProductBrand.cs.'
}
$version = $versionMatch.Groups['v'].Value
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot ('dist\Boostix-Setup-' + $version + '.exe')
}
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$installerName = Split-Path -Leaf $InstallerPath

$sandboxExe = Join-Path ([Environment]::SystemDirectory) 'WindowsSandbox.exe'
if (-not $PrepareOnly -and -not (Test-Path -LiteralPath $sandboxExe -PathType Leaf)) {
    throw ('Windows Sandbox is not turned on. It needs Windows 10/11 Pro or Enterprise ' +
        'with virtualization enabled. Turn it on in "Turn Windows features on or off" ' +
        '(Windows Sandbox), or from an administrator PowerShell: ' +
        'Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All ' +
        '- then restart the PC and run this script again.')
}

$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('BoostixSandbox-' + [Guid]::NewGuid().ToString('N'))
$inputDirectory = Join-Path $runDirectory 'input'
$resultsDirectory = Join-Path $runDirectory 'results'
[void](New-Item -ItemType Directory -Path $inputDirectory -Force)
[void](New-Item -ItemType Directory -Path $resultsDirectory -Force)
Copy-Item -LiteralPath $InstallerPath -Destination $inputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SandboxInstallCheck.ps1') -Destination $inputDirectory

$checkArguments = '-NoProfile -ExecutionPolicy Bypass -File C:\BoostixInput\SandboxInstallCheck.ps1' +
    ' -InstallerName "' + $installerName + '" -ExpectedVersion ' + $version
if (-not $KeepOpen) {
    $checkArguments += ' -ShutDownWhenDone'
}

$settings = New-Object Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object Text.UTF8Encoding($false)
$wsbPath = Join-Path $runDirectory 'Boostix-clean-install.wsb'
$writer = [Xml.XmlWriter]::Create($wsbPath, $settings)
try {
    $writer.WriteStartElement('Configuration')
    $writer.WriteElementString('Networking', $(if ($AllowNetwork) { 'Default' } else { 'Disable' }))
    $writer.WriteElementString('ClipboardRedirection', 'Disable')
    $writer.WriteElementString('PrinterRedirection', 'Disable')
    $writer.WriteElementString('AudioInput', 'Disable')
    $writer.WriteElementString('VideoInput', 'Disable')
    $writer.WriteStartElement('MappedFolders')
    foreach ($mapping in @(
            @($inputDirectory, 'C:\BoostixInput', 'true'),
            @($resultsDirectory, 'C:\BoostixResults', 'false'))) {
        $writer.WriteStartElement('MappedFolder')
        $writer.WriteElementString('HostFolder', $mapping[0])
        $writer.WriteElementString('SandboxFolder', $mapping[1])
        $writer.WriteElementString('ReadOnly', $mapping[2])
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteStartElement('LogonCommand')
    $writer.WriteElementString('Command', 'powershell.exe ' + $checkArguments)
    $writer.WriteEndElement()
    $writer.WriteEndElement()
}
finally {
    $writer.Dispose()
}

if ($PrepareOnly) {
    Write-Output $wsbPath
    return
}

Write-Host "Starting Windows Sandbox with $installerName ($version)."
Write-Host "Results: $resultsDirectory"
$sandbox = Start-Process -FilePath $sandboxExe -ArgumentList ('"' + $wsbPath + '"') -PassThru
$resultPath = Join-Path $resultsDirectory 'result.json'
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
while (-not (Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 5
}
if (-not (Test-Path -LiteralPath $resultPath)) {
    throw "No result after $TimeoutMinutes minutes. Look at the sandbox window and $resultsDirectory\sandbox.log."
}
Start-Sleep -Seconds 1
$result = [IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
foreach ($step in $result.steps) {
    $mark = if ($step.passed) { 'PASS' } else { 'FAIL' }
    Write-Host ('{0}  {1}: {2}' -f $mark, $step.name, $step.detail)
}
Write-Host "Screenshots and log: $resultsDirectory"
if (-not $result.passed) {
    throw 'The clean-install check in Windows Sandbox failed.'
}
Write-Host 'Clean install, first start and uninstall passed in Windows Sandbox.'
