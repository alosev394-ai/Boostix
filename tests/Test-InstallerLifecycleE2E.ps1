[CmdletBinding()]
param(
    [string]$LegacyInstallerPath,
    [string]$PrimaryInstallerPath,
    [switch]$AllowMachineChanges,
    [ValidateRange(120, 900)]
    [int]$ProcessTimeoutSeconds = 360
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This is a standalone machine lifecycle test. It is intentionally excluded
# from Run-All.ps1 and may run only on an elevated, disposable Windows runner.
if (-not $AllowMachineChanges) {
    throw 'This lifecycle test changes the machine. Run it only on a disposable runner with -AllowMachineChanges.'
}
if ($env:GITHUB_ACTIONS -eq 'true' -and
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'CI lifecycle installation is allowed only on a disposable GitHub-hosted runner.'
}
if ($env:OS -ne 'Windows_NT' -or
    -not [Environment]::Is64BitOperatingSystem) {
    throw 'The installer lifecycle test requires a 64-bit Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The installer lifecycle test requires an elevated administrator token.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandPath = Join-Path $projectRoot 'ProductBrand.cs'

function Get-BrandConstant {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = [regex]::Matches(
        $Source,
        '(?m)^\s*public\s+const\s+string\s+' +
            [regex]::Escape($Name) +
            '\s*=\s*"(?<value>[^"]+)"\s*;\s*$')
    if ($matches.Count -ne 1) {
        throw "ProductBrand.cs must declare exactly one $Name value."
    }
    return $matches[0].Groups['value'].Value
}

function Get-MachineProgramFilesDirectory {
    param([Microsoft.Win32.RegistryView]$View)

    $baseKey = $null
    $currentVersion = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            $View)
        $currentVersion = $baseKey.OpenSubKey(
            'SOFTWARE\Microsoft\Windows\CurrentVersion',
            $false)
        if ($currentVersion) {
            $configured = $currentVersion.GetValue(
                'ProgramFilesDir',
                $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($configured -is [string] -and
                -not [string]::IsNullOrWhiteSpace($configured) -and
                [IO.Path]::IsPathRooted($configured)) {
                return [IO.Path]::GetFullPath($configured)
            }
        }
    }
    finally {
        if ($currentVersion) {
            $currentVersion.Dispose()
        }
        if ($baseKey) {
            $baseKey.Dispose()
        }
    }
    return [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::ProgramFiles))
}

function Test-RegistryKeyExists {
    param(
        [Parameter(Mandatory = $true)]
        [Microsoft.Win32.RegistryView]$View,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $baseKey = $null
    $key = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            $View)
        $key = $baseKey.OpenSubKey($Path, $false)
        return $null -ne $key
    }
    finally {
        if ($key) {
            $key.Dispose()
        }
        if ($baseKey) {
            $baseKey.Dispose()
        }
    }
}

function Invoke-SetupProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Arguments,
        [Parameter(Mandatory = $true)][string]$Stage
    )

    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $resolved
    $startInfo.Arguments = $Arguments
    $startInfo.WorkingDirectory = Split-Path -Parent $resolved
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start the $Stage process."
        }
        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            try {
                $process.Kill()
            }
            catch {
            }
            throw "$Stage exceeded the $ProcessTimeoutSeconds second timeout."
        }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "$Stage failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-ProductMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedProductName,
        [Parameter(Mandatory = $true)][string]$ExpectedFileVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedCompanyName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected executable is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Executable cannot be a reparse point: $Path"
    }
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
    if ($metadata.ProductName -cne $ExpectedProductName -or
        $metadata.FileVersion -cne $ExpectedFileVersion -or
        $metadata.CompanyName -cne $ExpectedCompanyName) {
        throw (
            'Unexpected executable metadata for ' + $Path + ': ProductName=' +
            $metadata.ProductName + '; FileVersion=' + $metadata.FileVersion +
            '; CompanyName=' + $metadata.CompanyName)
    }
}

function Get-HealthHandshakeCount {
    param([Parameter(Mandatory = $true)][string[]]$LogPaths)

    $count = 0
    foreach ($path in $LogPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $content = [IO.File]::ReadAllText($path)
            $count += [regex]::Matches(
                $content,
                'Update health probe succeeded after ').Count
        }
    }
    return $count
}

function Assert-Registration {
    param(
        [Parameter(Mandatory = $true)]
        [Microsoft.Win32.RegistryView]$View,
        [Parameter(Mandatory = $true)][string]$UninstallPath,
        [Parameter(Mandatory = $true)][string]$AppPathsPath,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedInstallDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedExecutable
    )

    $baseKey = $null
    $uninstall = $null
    $appPath = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            $View)
        $uninstall = $baseKey.OpenSubKey($UninstallPath, $false)
        $appPath = $baseKey.OpenSubKey($AppPathsPath, $false)
        if (-not $uninstall -or -not $appPath) {
            throw 'Boostix machine registration is incomplete.'
        }
        if ([string]$uninstall.GetValue('DisplayName') -cne 'Boostix' -or
            [string]$uninstall.GetValue('DisplayVersion') -cne $ExpectedVersion -or
            [IO.Path]::GetFullPath(
                [string]$uninstall.GetValue('InstallLocation')) -cne
                [IO.Path]::GetFullPath($ExpectedInstallDirectory) -or
            [IO.Path]::GetFullPath([string]$appPath.GetValue('')) -cne
                [IO.Path]::GetFullPath($ExpectedExecutable)) {
            throw 'Boostix machine registration contains unexpected values.'
        }
    }
    finally {
        if ($appPath) {
            $appPath.Dispose()
        }
        if ($uninstall) {
            $uninstall.Dispose()
        }
        if ($baseKey) {
            $baseKey.Dispose()
        }
    }
}

function Test-DirectChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    return [string]::Equals(
        [IO.Path]::GetDirectoryName($fullPath),
        $fullRoot,
        [StringComparison]::OrdinalIgnoreCase)
}

function Remove-SafeOwnedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$AllowedLeafNames
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $fullRoot = [IO.Path]::GetFullPath($Root)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $leaf = [IO.Path]::GetFileName($fullPath.TrimEnd('\'))
    $allowed = $false
    foreach ($candidate in $AllowedLeafNames) {
        if ([string]::Equals(
                $leaf,
                $candidate,
                [StringComparison]::OrdinalIgnoreCase)) {
            $allowed = $true
            break
        }
    }
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container) -or
        -not (Test-Path -LiteralPath $fullPath -PathType Container) -or
        -not $allowed -or
        -not (Test-DirectChildPath -Root $fullRoot -Path $fullPath)) {
        throw "Refusing to remove a directory outside the E2E allowlist: $fullPath"
    }
    foreach ($item in @(
        Get-Item -LiteralPath $fullRoot -Force
        Get-Item -LiteralPath $fullPath -Force
        Get-ChildItem -LiteralPath $fullPath -Recurse -Force
    )) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to follow a reparse point during E2E cleanup: $($item.FullName)"
        }
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Remove-SafeOwnedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$AllowedLeafNames
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $fullRoot = [IO.Path]::GetFullPath($Root)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $leaf = [IO.Path]::GetFileName($fullPath)
    $allowed = $false
    foreach ($candidate in $AllowedLeafNames) {
        if ([string]::Equals(
                $leaf,
                $candidate,
                [StringComparison]::OrdinalIgnoreCase)) {
            $allowed = $true
            break
        }
    }
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "The E2E cleanup root is unavailable: $fullRoot"
    }
    $rootItem = Get-Item -LiteralPath $fullRoot -Force
    $item = Get-Item -LiteralPath $fullPath -Force
    if (-not $allowed -or
        -not (Test-DirectChildPath -Root $fullRoot -Path $fullPath) -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove a file outside the E2E allowlist: $fullPath"
    }
    Remove-Item -LiteralPath $fullPath -Force
}

function Remove-OwnedRegistryKeys {
    param(
        [Parameter(Mandatory = $true)]
        [Microsoft.Win32.RegistryView[]]$Views,
        [Parameter(Mandatory = $true)][string[]]$Paths
    )

    foreach ($view in $Views) {
        $baseKey = $null
        try {
            $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                [Microsoft.Win32.RegistryHive]::LocalMachine,
                $view)
            foreach ($path in $Paths) {
                $baseKey.DeleteSubKeyTree($path, $false)
            }
        }
        finally {
            if ($baseKey) {
                $baseKey.Dispose()
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $brandPath -PathType Leaf)) {
    throw "Product metadata source is missing: $brandPath"
}
$brandSource = [IO.File]::ReadAllText($brandPath)
$productName = Get-BrandConstant -Source $brandSource -Name 'ProductName'
$companyName = Get-BrandConstant -Source $brandSource -Name 'CompanyName'
$productVersion = Get-BrandConstant -Source $brandSource -Name 'ProductVersion'
$assemblyVersion = Get-BrandConstant -Source $brandSource -Name 'AssemblyVersion'
$installDirectoryName = Get-BrandConstant `
    -Source $brandSource `
    -Name 'InstallDirectoryName'
$legacyInstallDirectoryName = Get-BrandConstant `
    -Source $brandSource `
    -Name 'LegacyInstallDirectoryName'
$dataDirectoryName = Get-BrandConstant `
    -Source $brandSource `
    -Name 'DataDirectoryName'
$legacyDataDirectoryName = Get-BrandConstant `
    -Source $brandSource `
    -Name 'LegacyDataDirectoryName'

if ($productVersion -notmatch '^\d+\.\d+\.\d+$' -or
    $assemblyVersion -cne ($productVersion + '.0') -or
    $productName -cne 'Boostix') {
    throw 'ProductBrand.cs contains an unsupported lifecycle-test contract.'
}

if ([string]::IsNullOrWhiteSpace($LegacyInstallerPath)) {
    $LegacyInstallerPath = Join-Path $projectRoot (
        'dist\MajesticBoost-Setup-' + $productVersion + '.exe')
}
if ([string]::IsNullOrWhiteSpace($PrimaryInstallerPath)) {
    $PrimaryInstallerPath = Join-Path $projectRoot (
        'dist\Boostix-Setup-' + $productVersion + '.exe')
}
$LegacyInstallerPath = (Resolve-Path -LiteralPath $LegacyInstallerPath).Path
$PrimaryInstallerPath = (Resolve-Path -LiteralPath $PrimaryInstallerPath).Path
Assert-ProductMetadata `
    -Path $LegacyInstallerPath `
    -ExpectedProductName 'Majestic Boost' `
    -ExpectedFileVersion $assemblyVersion `
    -ExpectedCompanyName $companyName
Assert-ProductMetadata `
    -Path $PrimaryInstallerPath `
    -ExpectedProductName $productName `
    -ExpectedFileVersion $assemblyVersion `
    -ExpectedCompanyName $companyName

$machineRegistryView = [Microsoft.Win32.RegistryView]::Registry64
$registryViews = @(
    [Microsoft.Win32.RegistryView]::Registry64,
    [Microsoft.Win32.RegistryView]::Registry32
)
$programFilesDirectory = Get-MachineProgramFilesDirectory `
    -View $machineRegistryView
$installDirectory = [IO.Path]::GetFullPath((Join-Path `
    $programFilesDirectory $installDirectoryName))
$legacyInstallDirectory = [IO.Path]::GetFullPath((Join-Path `
    $programFilesDirectory $legacyInstallDirectoryName))
$installedExecutable = Join-Path $installDirectory 'Boostix.exe'
$installedUninstaller = Join-Path $installDirectory 'Uninstall.exe'
$commonDesktop = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonDesktopDirectory))
$commonPrograms = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonPrograms))
$localApplicationData = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData))
$commonApplicationData = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData))
$uninstallRegistryPath =
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Boostix'
$appPathsRegistryPath =
    'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Boostix.exe'
$legacyUninstallRegistryPath =
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MajesticBoost'
$legacyAppPathsRegistryPath =
    'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\MajesticBoost.exe'
$registryPaths = @(
    $uninstallRegistryPath,
    $appPathsRegistryPath,
    $legacyUninstallRegistryPath,
    $legacyAppPathsRegistryPath
)
$desktopShortcut = Join-Path $commonDesktop ($productName + '.lnk')
$legacyDesktopShortcut = Join-Path (
    $commonDesktop) ($legacyInstallDirectoryName + '.lnk')
$startMenuDirectory = Join-Path $commonPrograms $productName
$legacyStartMenuDirectory = Join-Path `
    $commonPrograms $legacyInstallDirectoryName
$startMenuShortcut = Join-Path $startMenuDirectory ($productName + '.lnk')
$localDataDirectory = Join-Path $localApplicationData $dataDirectoryName
$legacyLocalDataDirectory = Join-Path `
    $localApplicationData $legacyDataDirectoryName
$programDataDirectory = Join-Path $commonApplicationData $dataDirectoryName
$optimizationStateDirectory = Join-Path `
    $commonApplicationData 'BoostixOptimization'
$legacyOptimizationStateDirectory = Join-Path `
    $commonApplicationData 'CodexGamingOptimization'
$setupLogPaths = @(
    (Join-Path $programDataDirectory 'Logs\setup.log'),
    (Join-Path ([IO.Path]::GetTempPath()) 'Boostix-Setup.log')
)

$preexistingEvidence = New-Object 'System.Collections.Generic.List[string]'
$candidatePaths = @(
    $installDirectory,
    $legacyInstallDirectory,
    $desktopShortcut,
    $legacyDesktopShortcut,
    $startMenuDirectory,
    $legacyStartMenuDirectory,
    $localDataDirectory,
    $legacyLocalDataDirectory,
    $programDataDirectory,
    $optimizationStateDirectory,
    $legacyOptimizationStateDirectory
)
if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
    $programFilesX86 = [IO.Path]::GetFullPath(${env:ProgramFiles(x86)})
    if (-not [string]::Equals(
            $programFilesX86,
            $programFilesDirectory,
            [StringComparison]::OrdinalIgnoreCase)) {
        $candidatePaths += Join-Path $programFilesX86 $installDirectoryName
        $candidatePaths += Join-Path `
            $programFilesX86 $legacyInstallDirectoryName
    }
}
foreach ($path in $candidatePaths) {
    if (Test-Path -LiteralPath $path) {
        [void]$preexistingEvidence.Add($path)
    }
}
foreach ($view in $registryViews) {
    foreach ($path in $registryPaths) {
        if (Test-RegistryKeyExists -View $view -Path $path) {
            [void]$preexistingEvidence.Add(
                'HKLM (' + $view.ToString() + '):\' + $path)
        }
    }
}
foreach ($processName in @('Boostix', 'MajesticBoost')) {
    foreach ($process in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
        [void]$preexistingEvidence.Add(
            'running process ' + $process.ProcessName + ' (' + $process.Id + ')')
    }
}
if ($preexistingEvidence.Count -ne 0) {
    throw (
        'Refusing to run because Boostix or a legacy installation already exists:' +
        [Environment]::NewLine +
        [string]::Join([Environment]::NewLine, @(
            $preexistingEvidence | ForEach-Object { ' - ' + $_ })))
}

$runnerTemp = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [IO.Path]::GetFullPath($env:RUNNER_TEMP)
}
else {
    [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
}
$uninstallerCopyName =
    'Boostix-E2E-Uninstall-' + [Guid]::NewGuid().ToString('N') + '.exe'
$uninstallerCopy = Join-Path $runnerTemp $uninstallerCopyName
$installationAttempted = $false
$testFailure = $null
$cleanupFailures = New-Object 'System.Collections.Generic.List[string]'

try {
    $installationAttempted = $true
    Invoke-SetupProcess `
        -Path $LegacyInstallerPath `
        -Arguments '/quiet' `
        -Stage 'legacy bridge installation'

    Assert-ProductMetadata `
        -Path $installedExecutable `
        -ExpectedProductName $productName `
        -ExpectedFileVersion $assemblyVersion `
        -ExpectedCompanyName $companyName
    Assert-ProductMetadata `
        -Path $installedUninstaller `
        -ExpectedProductName $productName `
        -ExpectedFileVersion $assemblyVersion `
        -ExpectedCompanyName $companyName
    if ((Get-FileHash -LiteralPath $installedUninstaller -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $PrimaryInstallerPath -Algorithm SHA256).Hash) {
        throw 'The legacy bridge did not install the canonical Boostix uninstaller.'
    }
    Assert-Registration `
        -View $machineRegistryView `
        -UninstallPath $uninstallRegistryPath `
        -AppPathsPath $appPathsRegistryPath `
        -ExpectedVersion $productVersion `
        -ExpectedInstallDirectory $installDirectory `
        -ExpectedExecutable $installedExecutable
    foreach ($shortcut in @($desktopShortcut, $startMenuShortcut)) {
        if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) {
            throw "The expected common shortcut was not installed: $shortcut"
        }
    }

    $healthSignalsBeforeRepair = Get-HealthHandshakeCount `
        -LogPaths $setupLogPaths
    Invoke-SetupProcess `
        -Path $PrimaryInstallerPath `
        -Arguments '/quiet' `
        -Stage 'same-version repair'
    $healthSignalsAfterRepair = Get-HealthHandshakeCount `
        -LogPaths $setupLogPaths
    if ($healthSignalsAfterRepair -le $healthSignalsBeforeRepair) {
        throw 'The same-version repair did not complete the update health handshake.'
    }

    Assert-ProductMetadata `
        -Path $installedExecutable `
        -ExpectedProductName $productName `
        -ExpectedFileVersion $assemblyVersion `
        -ExpectedCompanyName $companyName
    Assert-ProductMetadata `
        -Path $installedUninstaller `
        -ExpectedProductName $productName `
        -ExpectedFileVersion $assemblyVersion `
        -ExpectedCompanyName $companyName
    if ((Get-FileHash -LiteralPath $installedUninstaller -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $PrimaryInstallerPath -Algorithm SHA256).Hash) {
        throw 'Same-version repair did not preserve the canonical uninstaller.'
    }
    Assert-Registration `
        -View $machineRegistryView `
        -UninstallPath $uninstallRegistryPath `
        -AppPathsPath $appPathsRegistryPath `
        -ExpectedVersion $productVersion `
        -ExpectedInstallDirectory $installDirectory `
        -ExpectedExecutable $installedExecutable

    Copy-Item -LiteralPath $installedUninstaller -Destination $uninstallerCopy
    Invoke-SetupProcess `
        -Path $uninstallerCopy `
        -Arguments '/uninstall /quiet' `
        -Stage 'quiet uninstall'

    foreach ($path in @(
        $installDirectory,
        $legacyInstallDirectory,
        $desktopShortcut,
        $legacyDesktopShortcut,
        $startMenuDirectory,
        $legacyStartMenuDirectory
    )) {
        if (Test-Path -LiteralPath $path) {
            throw "Quiet uninstall left an installed artifact: $path"
        }
    }
    foreach ($view in $registryViews) {
        foreach ($path in $registryPaths) {
            if (Test-RegistryKeyExists -View $view -Path $path) {
                throw (
                    'Quiet uninstall left machine registration in ' +
                    $view.ToString() + ': ' + $path)
            }
        }
    }
}
catch {
    $testFailure = $_.Exception
}
finally {
    if ($installationAttempted) {
        try {
            $hasInstalledState =
                (Test-Path -LiteralPath $installDirectory) -or
                (Test-Path -LiteralPath $legacyInstallDirectory) -or
                (Test-RegistryKeyExists `
                    -View $machineRegistryView `
                    -Path $uninstallRegistryPath) -or
                (Test-RegistryKeyExists `
                    -View $machineRegistryView `
                    -Path $appPathsRegistryPath)
            if ($hasInstalledState) {
                $cleanupExecutable = if (
                    Test-Path -LiteralPath $uninstallerCopy -PathType Leaf
                ) {
                    $uninstallerCopy
                }
                else {
                    $PrimaryInstallerPath
                }
                Invoke-SetupProcess `
                    -Path $cleanupExecutable `
                    -Arguments '/uninstall /quiet' `
                    -Stage 'finally cleanup uninstall'
            }
        }
        catch {
            [void]$cleanupFailures.Add($_.Exception.Message)
        }

        try {
            Remove-OwnedRegistryKeys `
                -Views $registryViews `
                -Paths $registryPaths
        }
        catch {
            [void]$cleanupFailures.Add($_.Exception.Message)
        }

        $fileCleanupEntries = @(
            [pscustomobject]@{
                Root = $commonDesktop
                Path = $desktopShortcut
                AllowedLeafNames = @($productName + '.lnk')
            },
            [pscustomobject]@{
                Root = $commonDesktop
                Path = $legacyDesktopShortcut
                AllowedLeafNames = @($legacyInstallDirectoryName + '.lnk')
            },
            [pscustomobject]@{
                Root = $runnerTemp
                Path = $uninstallerCopy
                AllowedLeafNames = @($uninstallerCopyName)
            }
        )
        foreach ($fileCleanup in $fileCleanupEntries) {
            try {
                Remove-SafeOwnedFile `
                    -Root $fileCleanup.Root `
                    -Path $fileCleanup.Path `
                    -AllowedLeafNames $fileCleanup.AllowedLeafNames
            }
            catch {
                [void]$cleanupFailures.Add($_.Exception.Message)
            }
        }

        $directoryCleanupEntries = @(
            [pscustomobject]@{
                Root = $programFilesDirectory
                Path = $installDirectory
                AllowedLeafNames = @($installDirectoryName, $legacyInstallDirectoryName)
            },
            [pscustomobject]@{
                Root = $programFilesDirectory
                Path = $legacyInstallDirectory
                AllowedLeafNames = @($installDirectoryName, $legacyInstallDirectoryName)
            },
            [pscustomobject]@{
                Root = $commonPrograms
                Path = $startMenuDirectory
                AllowedLeafNames = @($installDirectoryName, $legacyInstallDirectoryName)
            },
            [pscustomobject]@{
                Root = $commonPrograms
                Path = $legacyStartMenuDirectory
                AllowedLeafNames = @($installDirectoryName, $legacyInstallDirectoryName)
            },
            [pscustomobject]@{
                Root = $localApplicationData
                Path = $localDataDirectory
                AllowedLeafNames = @($dataDirectoryName, $legacyDataDirectoryName)
            },
            [pscustomobject]@{
                Root = $localApplicationData
                Path = $legacyLocalDataDirectory
                AllowedLeafNames = @($dataDirectoryName, $legacyDataDirectoryName)
            },
            [pscustomobject]@{
                Root = $commonApplicationData
                Path = $programDataDirectory
                AllowedLeafNames = @($dataDirectoryName, 'BoostixOptimization', 'CodexGamingOptimization')
            },
            [pscustomobject]@{
                Root = $commonApplicationData
                Path = $optimizationStateDirectory
                AllowedLeafNames = @($dataDirectoryName, 'BoostixOptimization', 'CodexGamingOptimization')
            },
            [pscustomobject]@{
                Root = $commonApplicationData
                Path = $legacyOptimizationStateDirectory
                AllowedLeafNames = @($dataDirectoryName, 'BoostixOptimization', 'CodexGamingOptimization')
            }
        )
        foreach ($directoryCleanup in $directoryCleanupEntries) {
            try {
                Remove-SafeOwnedDirectory `
                    -Root $directoryCleanup.Root `
                    -Path $directoryCleanup.Path `
                    -AllowedLeafNames $directoryCleanup.AllowedLeafNames
            }
            catch {
                [void]$cleanupFailures.Add($_.Exception.Message)
            }
        }
    }
    elseif (Test-Path -LiteralPath $uninstallerCopy) {
        try {
            Remove-SafeOwnedFile `
                -Root $runnerTemp `
                -Path $uninstallerCopy `
                -AllowedLeafNames @($uninstallerCopyName)
        }
        catch {
            [void]$cleanupFailures.Add($_.Exception.Message)
        }
    }
}

if ($testFailure) {
    $message = 'Installer lifecycle E2E failed: ' + $testFailure.Message
    if ($cleanupFailures.Count -ne 0) {
        $message += [Environment]::NewLine + 'Cleanup failures:' +
            [Environment]::NewLine +
            [string]::Join([Environment]::NewLine, @(
                $cleanupFailures | ForEach-Object { ' - ' + $_ }))
    }
    throw $message
}
if ($cleanupFailures.Count -ne 0) {
    throw (
        'Installer lifecycle E2E passed, but cleanup was incomplete:' +
        [Environment]::NewLine +
        [string]::Join([Environment]::NewLine, @(
            $cleanupFailures | ForEach-Object { ' - ' + $_ })))
}

Write-Host (
    'Installer lifecycle E2E passed for Boostix ' + $productVersion +
    ' on a clean disposable runner.') -ForegroundColor Green
