[CmdletBinding()]
param(
    [string]$ApplicationPath,
    [string]$InstallerPath,
    [string]$LegacyInstallerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5 -or
    [IntPtr]::Size -ne 8) {
    throw 'This regression test requires 64-bit Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandPath = Join-Path $projectRoot 'ProductBrand.cs'
$applicationSourcePath = Join-Path $projectRoot 'Boostix\Program.cs'
$updateSourcePath = Join-Path $projectRoot 'Boostix\UpdateFlow.cs'
$installerSourcePath = Join-Path $projectRoot 'BoostixInstaller\Program.cs'
foreach ($sourcePath in @(
    $brandPath,
    $applicationSourcePath,
    $updateSourcePath,
    $installerSourcePath
)) {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Cross-machine source contract is missing: $sourcePath"
    }
}

$brandSource = [IO.File]::ReadAllText($brandPath)
$versionMatches = [regex]::Matches(
    $brandSource,
    '(?m)^\s*public\s+const\s+string\s+ProductVersion\s*=\s*"' +
    '(?<version>\d+\.\d+\.\d+)"\s*;\s*$')
if ($versionMatches.Count -ne 1) {
    throw 'ProductBrand.cs must declare exactly one semantic ProductVersion.'
}
$productVersion = $versionMatches[0].Groups['version'].Value
$fileVersion = $productVersion + '.0'

$releaseSnapshotDirectory = $env:BOOSTIX_RELEASE_SNAPSHOT_DIRECTORY
if (-not [string]::IsNullOrWhiteSpace($releaseSnapshotDirectory)) {
    $releaseSnapshotDirectory = [IO.Path]::GetFullPath(
        $releaseSnapshotDirectory)
    if (-not (Test-Path -LiteralPath $releaseSnapshotDirectory -PathType Container)) {
        throw "The signed release snapshot is missing: $releaseSnapshotDirectory"
    }
}
$artifactRoot = if ($releaseSnapshotDirectory) {
    $releaseSnapshotDirectory
}
else {
    Join-Path $projectRoot 'dist'
}
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $artifactRoot 'Boostix.exe'
}
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $artifactRoot (
        'Boostix-Setup-' + $productVersion + '.exe')
}
if (-not $LegacyInstallerPath) {
    $LegacyInstallerPath = Join-Path $artifactRoot (
        'MajesticBoost-Setup-' + $productVersion + '.exe')
}
$latestInstallerPath = Join-Path $artifactRoot 'Boostix-Setup-Latest.exe'
foreach ($artifactPath in @(
    $ApplicationPath,
    $InstallerPath,
    $LegacyInstallerPath,
    $latestInstallerPath
)) {
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Required $productVersion executable is missing: $artifactPath"
    }
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$LegacyInstallerPath = (Resolve-Path -LiteralPath $LegacyInstallerPath).Path
$latestInstallerPath = (Resolve-Path -LiteralPath $latestInstallerPath).Path

$metadataContracts = @(
    @{ Path = $ApplicationPath; Product = 'Boostix' },
    @{ Path = $InstallerPath; Product = 'Boostix' },
    @{ Path = $latestInstallerPath; Product = 'Boostix' },
    @{ Path = $LegacyInstallerPath; Product = 'Majestic Boost' }
)
foreach ($contract in $metadataContracts) {
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($contract.Path)
    if ($versionInfo.FileVersion -cne $fileVersion -or
        $versionInfo.ProductName -cne $contract.Product -or
        $versionInfo.CompanyName -cne 'Silas Suspect') {
        throw "Incorrect $productVersion executable metadata: $($contract.Path)"
    }
}

$allFlags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'

function Get-RequiredType {
    param(
        [Parameter(Mandatory = $true)][Reflection.Assembly]$Assembly,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $type = $Assembly.GetType($Name, $false, $false)
    if ($null -eq $type) {
        throw "Compiled type is missing: $Name"
    }
    return $type
}

function Get-RequiredMethod {
    param(
        [Parameter(Mandatory = $true)][Type]$Type,
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$ParameterCount = -1
    )

    $methods = @(
        $Type.GetMethods($allFlags) |
            Where-Object {
                $_.Name -ceq $Name -and
                ($ParameterCount -lt 0 -or
                 $_.GetParameters().Count -eq $ParameterCount)
            }
    )
    if ($methods.Count -ne 1) {
        throw "Expected one compiled method $($Type.FullName).$Name; found $($methods.Count)."
    }
    return $methods[0]
}

function Invoke-ReflectedStatic {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [object[]]$Arguments = @()
    )

    try {
        return $Method.Invoke($null, $Arguments)
    }
    catch [Reflection.TargetInvocationException] {
        if ($null -ne $_.Exception.InnerException) {
            throw $_.Exception.InnerException
        }
        throw
    }
}

$opCodesByValue = @{}
foreach ($opCodeField in [Reflection.Emit.OpCodes].GetFields(
    [Reflection.BindingFlags]'Public,Static')) {
    $opCode = [Reflection.Emit.OpCode]$opCodeField.GetValue($null)
    $value = [int]$opCode.Value
    if ($value -lt 0) {
        $value += 65536
    }
    $opCodesByValue[$value] = $opCode
}

function Get-MethodIlReferences {
    param([Parameter(Mandatory = $true)][Reflection.MethodBase]$Method)

    $body = $Method.GetMethodBody()
    if ($null -eq $body) {
        throw "Compiled method has no IL body: $($Method.Name)"
    }
    $bytes = $body.GetILAsByteArray()
    $position = 0
    $references = New-Object 'System.Collections.Generic.List[string]'
    while ($position -lt $bytes.Length) {
        $opCodeValue = [int]$bytes[$position]
        $position++
        if ($opCodeValue -eq 0xFE) {
            $opCodeValue = 0xFE00 -bor [int]$bytes[$position]
            $position++
        }
        $opCode = $opCodesByValue[$opCodeValue]
        if ($null -eq $opCode) {
            throw "Unknown IL opcode 0x$($opCodeValue.ToString('X4'))."
        }

        $operandSize = 0
        $metadataToken = $null
        $stringToken = $false
        switch ($opCode.OperandType.ToString()) {
            'InlineNone' { $operandSize = 0 }
            'InlinePhi' { $operandSize = 0 }
            'ShortInlineBrTarget' { $operandSize = 1 }
            'ShortInlineI' { $operandSize = 1 }
            'ShortInlineVar' { $operandSize = 1 }
            'InlineVar' { $operandSize = 2 }
            'InlineBrTarget' { $operandSize = 4 }
            'InlineI' { $operandSize = 4 }
            'ShortInlineR' { $operandSize = 4 }
            'InlineField' {
                $operandSize = 4
                $metadataToken = [BitConverter]::ToInt32($bytes, $position)
            }
            'InlineMethod' {
                $operandSize = 4
                $metadataToken = [BitConverter]::ToInt32($bytes, $position)
            }
            'InlineType' {
                $operandSize = 4
                $metadataToken = [BitConverter]::ToInt32($bytes, $position)
            }
            'InlineTok' {
                $operandSize = 4
                $metadataToken = [BitConverter]::ToInt32($bytes, $position)
            }
            'InlineString' {
                $operandSize = 4
                $metadataToken = [BitConverter]::ToInt32($bytes, $position)
                $stringToken = $true
            }
            'InlineSig' { $operandSize = 4 }
            'InlineI8' { $operandSize = 8 }
            'InlineR' { $operandSize = 8 }
            'InlineSwitch' {
                $switchCount = [BitConverter]::ToInt32($bytes, $position)
                $operandSize = 4 + (4 * $switchCount)
            }
            default {
                throw "Unsupported IL operand type: $($opCode.OperandType)."
            }
        }

        if ($null -ne $metadataToken) {
            try {
                if ($stringToken) {
                    [void]$references.Add(
                        'STRING::' + $Method.Module.ResolveString($metadataToken))
                }
                else {
                    $member = $Method.Module.ResolveMember($metadataToken)
                    if ($member -is [Type]) {
                        [void]$references.Add('TYPE::' + $member.FullName)
                    }
                    elseif ($null -ne $member.DeclaringType) {
                        [void]$references.Add(
                            $member.DeclaringType.FullName + '::' + $member.Name)
                    }
                }
            }
            catch [ArgumentException] {
                # Generic metadata outside the tested contracts can be unresolved.
            }
        }
        $position += $operandSize
    }
    return @($references | Sort-Object -Unique)
}

function Assert-ReferencesContain {
    param(
        [Parameter(Mandatory = $true)][string[]]$References,
        [Parameter(Mandatory = $true)][string[]]$Required,
        [Parameter(Mandatory = $true)][string]$ContractName
    )

    foreach ($reference in $Required) {
        if ($References -notcontains $reference) {
            throw "$ContractName does not reference $reference."
        }
    }
}

function Test-BinaryExcludesWScriptShell {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    try {
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        $unicode = [Text.Encoding]::Unicode.GetString($bytes)
        if ($ascii.IndexOf('WScript.Shell', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $unicode.IndexOf('WScript.Shell', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "The installer still embeds WScript.Shell: $Path"
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

$applicationAssembly = [Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($ApplicationPath))
$installerAssembly = [Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($InstallerPath))
$legacyInstallerAssembly = [Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($LegacyInstallerPath))
foreach ($assembly in @(
    $applicationAssembly,
    $installerAssembly,
    $legacyInstallerAssembly
)) {
    if ($assembly.GetName().Version.ToString() -cne $fileVersion) {
        throw "A loaded executable assembly is not version $fileVersion."
    }
}

# Exercise the compiled request factory without calling GetResponse: TLS must be
# additive, both standard content encodings enabled, and Windows proxy
# credentials supplied for authenticated enterprise proxies.
$updateType = Get-RequiredType `
    -Assembly $applicationAssembly `
    -Name 'Boostix.UpdateFlowOverlay'
$createRequestMethod = Get-RequiredMethod `
    -Type $updateType `
    -Name 'CreateRequest' `
    -ParameterCount 2
$originalSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
$originalProxy = [Net.WebRequest]::DefaultWebProxy
$request = $null
try {
    [Net.WebRequest]::DefaultWebProxy = New-Object Net.WebProxy(
        'http://127.0.0.1:9')
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.SecurityProtocolType]::Tls11
    $request = [Net.HttpWebRequest](Invoke-ReflectedStatic `
        -Method $createRequestMethod `
        -Arguments @('https://example.invalid/boostix-update', 1000))

    $protocol = [Net.ServicePointManager]::SecurityProtocol
    if (($protocol -band [Net.SecurityProtocolType]::Tls11) -eq 0 -or
        ($protocol -band [Net.SecurityProtocolType]::Tls12) -eq 0) {
        throw 'The updater overwrote the existing TLS flags instead of adding TLS 1.2.'
    }
    $requiredDecompression = [Net.DecompressionMethods]::GZip -bor
        [Net.DecompressionMethods]::Deflate
    if (($request.AutomaticDecompression -band $requiredDecompression) -ne
        $requiredDecompression) {
        throw 'The updater does not accept both GZip and Deflate responses.'
    }
    if ($request.AllowAutoRedirect) {
        throw 'The updater enabled implicit redirects.'
    }
    if ($null -eq $request.Proxy -or
        -not [object]::ReferenceEquals(
            $request.Proxy.Credentials,
            [Net.CredentialCache]::DefaultNetworkCredentials)) {
        throw 'The updater does not use current Windows proxy credentials.'
    }
}
finally {
    if ($null -ne $request) {
        $request.Abort()
    }
    [Net.WebRequest]::DefaultWebProxy = $originalProxy
    [Net.ServicePointManager]::SecurityProtocol = $originalSecurityProtocol
}

# Parse a synthetic signed-manifest payload locally. This exercises the exact
# compiled URL construction without making a live request.
$mirrorManifestUrl = [string]$updateType.GetField(
    'MirrorManifestUrl',
    $allFlags).GetRawConstantValue()
$mirrorSignatureUrl = [string]$updateType.GetField(
    'MirrorManifestSignatureUrl',
    $allFlags).GetRawConstantValue()
$expectedMirrorManifestUrl =
    'https://cdn.jsdelivr.net/gh/alosev394-ai/Boostix@latest/update-v2.json'
if ($mirrorManifestUrl -cne $expectedMirrorManifestUrl -or
    $mirrorSignatureUrl -cne ($expectedMirrorManifestUrl + '.sig')) {
    throw 'The signed manifest mirror is not pinned to the purgeable latest semver alias.'
}

$legacyRawUrl =
    'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/' +
    'MajesticBoost-Setup-' + $productVersion + '.exe'
$boostixRawUrl =
    'https://raw.githubusercontent.com/alosev394-ai/Boostix/main/dist/' +
    'Boostix-Setup-' + $productVersion + '.exe'
$releaseUrl =
    'https://github.com/alosev394-ai/Boostix/releases/download/v' +
    $productVersion + '/Boostix-Setup-' + $productVersion + '.exe'
$manifestJson =
    '{"schemaVersion":1,"version":"' + $productVersion +
    '","installerUrl":"' + $legacyRawUrl +
    '","sha256":"' + ('A' * 64) + '","size":1,' +
    '"boostixInstallerUrl":"' + $boostixRawUrl +
    '","boostixSha256":"' + ('B' * 64) + '","boostixSize":2}'
$parseManifestMethod = Get-RequiredMethod `
    -Type $updateType `
    -Name 'ParseAndValidateManifest' `
    -ParameterCount 1
$manifestBytes = [Text.Encoding]::UTF8.GetBytes($manifestJson)
try {
    $manifest = Invoke-ReflectedStatic `
        -Method $parseManifestMethod `
        -Arguments @(, $manifestBytes)
}
finally {
    [Array]::Clear($manifestBytes, 0, $manifestBytes.Length)
}
$manifestType = $manifest.GetType()
$primarySource = [string]$manifestType.GetField(
    'InstallerUrl',
    $allFlags).GetValue($manifest)
$fallbackSources = [string[]]@($manifestType.GetField(
    'FallbackInstallerUrls',
    $allFlags).GetValue($manifest))
$trustedSources = @($primarySource) + @($fallbackSources)
if ($trustedSources.Count -ne 2 -or
    @($trustedSources | Sort-Object -Unique).Count -ne 2 -or
    $trustedSources[0] -cne $boostixRawUrl -or
    $trustedSources[1] -cne $releaseUrl) {
    throw 'The updater does not compile exactly two trusted installer sources.'
}

$trustedReleaseMethod = Get-RequiredMethod `
    -Type $updateType `
    -Name 'IsTrustedReleaseInstallerAddress' `
    -ParameterCount 1
if (-not [bool](Invoke-ReflectedStatic `
    -Method $trustedReleaseMethod `
    -Arguments @($releaseUrl))) {
    throw 'The exact GitHub release installer URL was rejected.'
}
foreach ($untrustedReleaseUrl in @(
    ('http://github.com/alosev394-ai/Boostix/releases/download/v' +
        $productVersion + '/Boostix-Setup-' + $productVersion + '.exe'),
    ('https://github.com.evil.example/alosev394-ai/Boostix/releases/download/v' +
        $productVersion + '/Boostix-Setup-' + $productVersion + '.exe'),
    ('https://user@github.com/alosev394-ai/Boostix/releases/download/v' +
        $productVersion + '/Boostix-Setup-' + $productVersion + '.exe'),
    ('https://github.com:444/alosev394-ai/Boostix/releases/download/v' +
        $productVersion + '/Boostix-Setup-' + $productVersion + '.exe'),
    ($releaseUrl + '?asset=other'),
    ($releaseUrl + '#fragment')
)) {
    if ([bool](Invoke-ReflectedStatic `
        -Method $trustedReleaseMethod `
        -Arguments @($untrustedReleaseUrl))) {
        throw "An adversarial release URL was accepted: $untrustedReleaseUrl"
    }
}

$trustedRedirectMethod = Get-RequiredMethod `
    -Type $updateType `
    -Name 'IsTrustedReleaseRedirect' `
    -ParameterCount 1
foreach ($trustedRedirect in @(
    'https://release-assets.githubusercontent.com/github-production-release-asset/test?sig=x',
    'https://objects.githubusercontent.com/github-production-release-asset/test?sig=x',
    $releaseUrl
)) {
    if (-not [bool](Invoke-ReflectedStatic `
        -Method $trustedRedirectMethod `
        -Arguments @([Uri]$trustedRedirect))) {
        throw "A required GitHub release redirect was rejected: $trustedRedirect"
    }
}
foreach ($untrustedRedirect in @(
    'https://release-assets.githubusercontent.com.evil.example/asset',
    'https://evilrelease-assets.githubusercontent.com/asset',
    'https://release-assets.githubusercontent.com./asset',
    'http://release-assets.githubusercontent.com/asset',
    'https://user@release-assets.githubusercontent.com/asset',
    'https://objects.githubusercontent.com:444/asset',
    'https://cdn.jsdelivr.net/asset',
    'https://github.com.evil.example/asset',
    'https://github.com/asset#fragment'
)) {
    if ([bool](Invoke-ReflectedStatic `
        -Method $trustedRedirectMethod `
        -Arguments @([Uri]$untrustedRedirect))) {
        throw "An adversarial release redirect was accepted: $untrustedRedirect"
    }
}

# Validate the actual ACL produced by the updater and its guarded cleanup.
$createStageMethod = Get-RequiredMethod `
    -Type $updateType `
    -Name 'CreateSecureUpdateDownloadDirectoryAtRoot' `
    -ParameterCount 1
$deleteStageMethod = Get-RequiredMethod `
    -Type $updateType `
    -Name 'TryDeleteDownload' `
    -ParameterCount 2
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stageDirectory = $null
$stageInstallerPath = $null
try {
    $stageDirectory = [IO.Path]::GetFullPath([string](Invoke-ReflectedStatic `
        -Method $createStageMethod `
        -Arguments @($tempRoot)))
    if (-not [string]::Equals(
            [IO.Path]::GetDirectoryName($stageDirectory),
            $tempRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($stageDirectory) -notmatch
            '^Boostix\.Update\.[0-9a-f]{32}$') {
        throw 'The updater staging directory escaped the system temp root.'
    }
    $stageInfo = New-Object IO.DirectoryInfo($stageDirectory)
    if (-not $stageInfo.Exists -or
        ($stageInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The updater staging path is missing or is a reparse point.'
    }
    $security = $stageInfo.GetAccessControl(
        [Security.AccessControl.AccessControlSections]'Access,Owner')
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $administrators = New-Object Security.Principal.SecurityIdentifier(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null)
    $localSystem = New-Object Security.Principal.SecurityIdentifier(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null)
    if ($null -eq $currentSid -or
        -not $security.AreAccessRulesProtected -or
        -not $security.GetOwner(
            [Security.Principal.SecurityIdentifier]).Equals($currentSid)) {
        throw 'The updater staging ACL is inherited or has the wrong owner.'
    }
    $requiredSids = @(
        $currentSid.Value,
        $administrators.Value,
        $localSystem.Value
    )
    $accessRules = @($security.GetAccessRules(
        $true,
        $false,
        [Security.Principal.SecurityIdentifier]))
    $fullControl = [int][Security.AccessControl.FileSystemRights]::FullControl
    $requiredInheritance =
        [int][Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [int][Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in $requiredSids) {
        $matchingRules = @($accessRules | Where-Object {
            $_.IdentityReference.Value -ceq $sid -and
            $_.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            (([int]$_.FileSystemRights -band $fullControl) -eq $fullControl) -and
            (([int]$_.InheritanceFlags -band $requiredInheritance) -eq
                $requiredInheritance)
        })
        if ($matchingRules.Count -eq 0) {
            throw "The updater staging ACL lacks protected FullControl for $sid."
        }
    }
    foreach ($rule in $accessRules) {
        if ($requiredSids -notcontains $rule.IdentityReference.Value -or
            $rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow) {
            throw 'The updater staging ACL contains an unexpected principal or deny rule.'
        }
    }

    $stageInstallerPath = Join-Path $stageDirectory (
        'Boostix-Setup-' + $productVersion + '.exe')
    [IO.File]::WriteAllBytes(
        $stageInstallerPath,
        [byte[]](0x4D, 0x5A, 0x00, 0x00))
    $cleanupArguments = New-Object 'object[]' 2
    $cleanupArguments[0] = [string]$stageDirectory
    $cleanupArguments[1] = [string]$stageInstallerPath
    [void](Invoke-ReflectedStatic `
        -Method $deleteStageMethod `
        -Arguments $cleanupArguments)
    if ([IO.File]::Exists($stageInstallerPath) -or
        [IO.Directory]::Exists($stageDirectory)) {
        throw 'The guarded updater cleanup left its temporary staging payload behind.'
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($stageDirectory) -and
        [string]::Equals(
            [IO.Path]::GetDirectoryName($stageDirectory),
            $tempRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($stageDirectory) -match
            '^Boostix\.Update\.[0-9a-f]{32}$') {
        if ($stageInstallerPath -and [IO.File]::Exists($stageInstallerPath)) {
            [IO.File]::Delete($stageInstallerPath)
        }
        if ([IO.Directory]::Exists($stageDirectory) -and
            ([IO.File]::GetAttributes($stageDirectory) -band
                [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            [IO.Directory]::GetFileSystemEntries($stageDirectory).Length -eq 0) {
            [IO.Directory]::Delete($stageDirectory, $false)
        }
    }
}

# Inspect the generated async state machine, not source comments, to guarantee
# that health readiness does not depend on window visibility, dimensions, WMI,
# performance diagnostics, preflight, or session-history collection.
$boostWindowType = Get-RequiredType `
    -Assembly $applicationAssembly `
    -Name 'Boostix.BoostWindow'
$probeMethod = Get-RequiredMethod `
    -Type $boostWindowType `
    -Name 'VerifyLocalStartupForUpdateAsync' `
    -ParameterCount 0
$stateMachineAttributes = @($probeMethod.GetCustomAttributes(
    [Runtime.CompilerServices.AsyncStateMachineAttribute],
    $false))
if ($stateMachineAttributes.Count -ne 1) {
    throw 'The compiled update health probe has no async state machine.'
}
$moveNextMethod = Get-RequiredMethod `
    -Type $stateMachineAttributes[0].StateMachineType `
    -Name 'MoveNext' `
    -ParameterCount 0
$probeReferences = @(Get-MethodIlReferences -Method $moveNextMethod)
Assert-ReferencesContain `
    -References $probeReferences `
    -Required @(
        'Boostix.OptimizationFlowOverlay::IsInitializedForUpdateHealth',
        'System.Windows.Threading.Dispatcher::InvokeAsync',
        'System.Windows.Threading.Dispatcher::get_HasShutdownStarted',
        'System.Windows.Threading.Dispatcher::get_HasShutdownFinished'
    ) `
    -ContractName 'The local update health probe'
foreach ($forbiddenProbeReference in @(
    'Boostix\.OptimizationFlowOverlay::GetOptimizationStatus',
    '::get_IsVisible$',
    '::get_ActualWidth$',
    '::get_ActualHeight$',
    '^System\.Management\.',
    'ManagementObject',
    'PerformanceCounter',
    'Diagnostic',
    'Preflight',
    'SessionHistory',
    'Wmi'
)) {
    if (@($probeReferences | Where-Object {
        $_ -match $forbiddenProbeReference
    }).Count -ne 0) {
        throw "The health probe still depends on $forbiddenProbeReference."
    }
}

function Test-InstallerAssemblyContracts {
    param(
        [Parameter(Mandatory = $true)][Reflection.Assembly]$Assembly,
        [Parameter(Mandatory = $true)][string]$Path
    )

    Test-BinaryExcludesWScriptShell -Path $Path
    $engineType = Get-RequiredType `
        -Assembly $Assembly `
        -Name 'BoostixSetup.InstallerEngine'
    $diagnosticsType = Get-RequiredType `
        -Assembly $Assembly `
        -Name 'BoostixSetup.InstallerDiagnostics'
    $shellLinkType = Get-RequiredType `
        -Assembly $Assembly `
        -Name 'BoostixSetup.InstallerEngine+IShellLinkW'
    if (-not $shellLinkType.IsInterface) {
        throw "The installer does not compile IShellLinkW as an interface: $Path"
    }

    $createShortcutMethod = Get-RequiredMethod `
        -Type $engineType `
        -Name 'CreateShortcut' `
        -ParameterCount 4
    $shortcutReferences = @(Get-MethodIlReferences -Method $createShortcutMethod)
    Assert-ReferencesContain `
        -References $shortcutReferences `
        -Required @(
            'BoostixSetup.InstallerEngine+IShellLinkW::SetPath',
            'System.Runtime.InteropServices.ComTypes.IPersistFile::Save',
            'System.Runtime.InteropServices.Marshal::FinalReleaseComObject'
        ) `
        -ContractName 'The native shortcut implementation'

    $desktopMethod = Get-RequiredMethod `
        -Type $engineType `
        -Name 'TryResolveOptionalShortcutRoot' `
        -ParameterCount 3
    $desktopArguments = New-Object 'object[]' 3
    $desktopArguments[0] = [Environment+SpecialFolder]::CommonDesktopDirectory
    $desktopArguments[1] = [string]'desktop contract'
    $desktopArguments[2] = $null
    $desktopResolved = [bool](Invoke-ReflectedStatic `
        -Method $desktopMethod `
        -Arguments $desktopArguments)
    $actualDesktop = [string]$desktopArguments[2]
    $configuredDesktop = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonDesktopDirectory)
    if ($desktopResolved) {
        if ([string]::IsNullOrWhiteSpace($configuredDesktop) -or
            [string]::IsNullOrWhiteSpace($actualDesktop) -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($actualDesktop),
                [IO.Path]::GetFullPath($configuredDesktop),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The installer does not target the machine-wide desktop: $Path"
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($actualDesktop)) {
        throw "The installer returned a path for an unavailable optional desktop: $Path"
    }

    $registryViewProperty = $engineType.GetProperty(
        'MachineRegistryView',
        $allFlags)
    if ($null -eq $registryViewProperty) {
        throw "The installer has no machine registry view contract: $Path"
    }
    $actualRegistryView = [Microsoft.Win32.RegistryView](
        $registryViewProperty.GetValue($null, $null))
    $expectedRegistryView = if ([Environment]::Is64BitOperatingSystem) {
        [Microsoft.Win32.RegistryView]::Registry64
    }
    else {
        [Microsoft.Win32.RegistryView]::Registry32
    }
    if ($actualRegistryView -ne $expectedRegistryView) {
        throw "The installer selected the wrong machine registry view: $Path"
    }

    $healthTimeoutField = $engineType.GetField(
        'UpdateHealthTimeoutMilliseconds',
        $allFlags)
    if ($null -eq $healthTimeoutField -or
        [int]$healthTimeoutField.GetRawConstantValue() -lt 90000) {
        throw "The installer health timeout is below 90 seconds: $Path"
    }
    [void](Get-RequiredMethod `
        -Type $engineType `
        -Name 'LaunchAndWaitForUpdateHealth' `
        -ParameterCount 2)

    if ($null -ne $engineType.GetMethod(
        'PrepareLegacyInstallationForUpdate',
        $allFlags)) {
        throw "The unsafe pre-copy legacy migration returned: $Path"
    }
    $installMethod = Get-RequiredMethod `
        -Type $engineType `
        -Name 'Install' `
        -ParameterCount 2
    $installReferences = @(Get-MethodIlReferences -Method $installMethod)
    Assert-ReferencesContain `
        -References $installReferences `
        -Required @(
            'BoostixSetup.InstallerEngine::RecoverInterruptedUpdateTransactions',
            'BoostixSetup.InstallerEngine::InstallUpdateWithHealthRollback',
            'BoostixSetup.InstallerEngine::InstallWithSystemTransactionGuard',
            'BoostixSetup.InstallerEngine::CleanupLegacyInstallationAfterSuccess'
        ) `
        -ContractName 'The atomic install entry point'
    if (@($installReferences | Where-Object {
        $_ -match 'PrepareLegacyInstallationForUpdate|MigrateLegacy.*Copy'
    }).Count -ne 0) {
        throw "The installer invokes a legacy pre-copy migration: $Path"
    }

    $maximumLogField = $diagnosticsType.GetField(
        'MaximumLogBytes',
        $allFlags)
    if ($null -eq $maximumLogField -or
        [long]$maximumLogField.GetRawConstantValue() -lt 1048576L) {
        throw "The durable setup log has no bounded rotation size: $Path"
    }
    $ensureLogMethod = Get-RequiredMethod `
        -Type $diagnosticsType `
        -Name 'EnsureLogPath' `
        -ParameterCount 0
    $writeLogMethod = Get-RequiredMethod `
        -Type $diagnosticsType `
        -Name 'Write' `
        -ParameterCount 2
    $rotateLogMethod = Get-RequiredMethod `
        -Type $diagnosticsType `
        -Name 'RotateIfNeeded' `
        -ParameterCount 0
    Assert-ReferencesContain `
        -References @(Get-MethodIlReferences -Method $ensureLogMethod) `
        -Required @(
            'System.Environment::GetFolderPath',
            'BoostixSetup.InstallerDiagnostics::EnsureProtectedLogDirectory',
            'STRING::Logs',
            'STRING::setup-'
        ) `
        -ContractName 'The durable setup log path'
    Assert-ReferencesContain `
        -References @(Get-MethodIlReferences -Method $writeLogMethod) `
        -Required @(
            'BoostixSetup.InstallerDiagnostics::RotateIfNeeded',
            'System.IO.File::AppendAllText'
        ) `
        -ContractName 'The durable setup log writer'
    Assert-ReferencesContain `
        -References @(Get-MethodIlReferences -Method $rotateLogMethod) `
        -Required @(
            'System.IO.File::Move',
            'System.IO.File::Delete',
            'STRING::.previous'
        ) `
        -ContractName 'The durable setup log rotation'

    $retryMethod = Get-RequiredMethod `
        -Type $engineType `
        -Name 'ExecuteFileMutationWithRetry' `
        -ParameterCount 2
    $retryReferences = @(Get-MethodIlReferences -Method $retryMethod)
    Assert-ReferencesContain `
        -References $retryReferences `
        -Required @(
            'System.Action::Invoke',
            'System.Threading.Thread::Sleep',
            'BoostixSetup.InstallerDiagnostics::Write'
        ) `
        -ContractName 'The bounded installer file retry method'
    $catchTypes = @($retryMethod.GetMethodBody().ExceptionHandlingClauses |
        Where-Object { $null -ne $_.CatchType } |
        ForEach-Object { $_.CatchType.FullName })
    foreach ($requiredCatchType in @(
        'System.IO.IOException',
        'System.UnauthorizedAccessException'
    )) {
        if ($catchTypes -notcontains $requiredCatchType) {
            throw "The retry method does not handle $requiredCatchType in $Path."
        }
    }
}

Test-InstallerAssemblyContracts `
    -Assembly $installerAssembly `
    -Path $InstallerPath
Test-InstallerAssemblyContracts `
    -Assembly $legacyInstallerAssembly `
    -Path $LegacyInstallerPath
Test-BinaryExcludesWScriptShell -Path $latestInstallerPath

# Source assertions are intentionally limited to ordering/compile-time branch
# contracts that reflection cannot prove without executing an installer.
$installerSource = [IO.File]::ReadAllText($installerSourcePath)
$applicationSource = [IO.File]::ReadAllText($applicationSourcePath)
if ($installerSource.IndexOf(
        'WScript.Shell',
        [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $installerSource.Contains('PrepareLegacyInstallationForUpdate')) {
    throw 'A forbidden legacy installer contract returned to source.'
}
$initializeDiagnosticsIndex = $installerSource.IndexOf(
    'InstallerDiagnostics.Initialize(args);',
    [StringComparison]::Ordinal)
$dllHardeningIndex = $installerSource.IndexOf(
    'if (!HardenNativeDllSearch())',
    [StringComparison]::Ordinal)
$mainCoreIndex = $installerSource.IndexOf(
    'MainCore(args);',
    [StringComparison]::Ordinal)
if ($dllHardeningIndex -lt 0 -or
    $initializeDiagnosticsIndex -le $dllHardeningIndex -or
    $mainCoreIndex -lt 0 -or
    $initializeDiagnosticsIndex -ge $mainCoreIndex) {
    throw 'DLL search hardening and protected logging are not ordered safely before installer execution.'
}
$installStart = $installerSource.IndexOf(
    'public static void Install(bool createDesktopShortcut',
    [StringComparison]::Ordinal)
$cleanupDeclaration = $installerSource.IndexOf(
    'private static void CleanupLegacyInstallationAfterSuccess()',
    [StringComparison]::Ordinal)
if ($installStart -lt 0 -or $cleanupDeclaration -le $installStart) {
    throw 'The installer entry-point source contract could not be isolated.'
}
$installSource = $installerSource.Substring(
    $installStart,
    $cleanupDeclaration - $installStart)
$legacyCleanupCall = $installSource.IndexOf(
    'CleanupLegacyInstallationAfterSuccess();',
    [StringComparison]::Ordinal)
$freshInstallCall = $installSource.IndexOf(
    'InstallWithSystemTransactionGuard(createDesktopShortcut, progress);',
    [StringComparison]::Ordinal)
if ($legacyCleanupCall -lt 0 -or
    $freshInstallCall -lt 0 -or
    $legacyCleanupCall -le $freshInstallCall) {
    throw 'Legacy cleanup is no longer sequenced after a successful installation.'
}
if (-not [regex]::IsMatch(
    $applicationSource,
    '(?s)if\s*\(updateHealthProbe\).*?' +
    'await\s+VerifyLocalStartupForUpdateAsync\(\);.*?' +
    'UpdateHealthHandshake\.CompleteReadyHandshakeIfRequested\(.*?' +
    'Application\.Current\.Shutdown\(0\);')) {
    throw 'The health-probe branch does not verify local startup before signalling ready.'
}

Write-Host (
    'Cross-machine reliability regression test passed for Boostix ' +
    $productVersion + '.') -ForegroundColor Green
