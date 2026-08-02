[CmdletBinding()]
param(
    [ValidateRange(5, 300)]
    [int]$RequestTimeoutSeconds = 60,

    [ValidateRange(1, 5)]
    [int]$DownloadAttempts = 3,

    [ValidateRange(1048576, 268435456)]
    [long]$MaximumInstallerBytes = 268435456,

    [string]$ExpectedVersion,

    [string]$OfflineFixtureRoot,

    [switch]$RequireAuthenticode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This canary must run under Windows PowerShell 5.1.'
}

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDirectory
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$repository = 'alosev394-ai/Boostix'
$legacyRepository = 'alosev394-ai/MajesticBoost'
$rawManifestUrl =
    'https://raw.githubusercontent.com/' + $repository +
    '/main/update-v2.json'
$rawSignatureUrl = $rawManifestUrl + '.sig'
$mirrorManifestUrl =
    'https://cdn.jsdelivr.net/gh/' + $repository +
    '@latest/update-v2.json'
$mirrorSignatureUrl = $mirrorManifestUrl + '.sig'
$legacyManifestUrl =
    'https://raw.githubusercontent.com/' + $legacyRepository +
    '/main/update.json'
$legacySignatureUrl = $legacyManifestUrl + '.sig'
$trustedPublicKeyXml = '<RSAKeyValue><Modulus>vCSgQnLtxkncktDMNkZo6cnqx3cBrLMm8z6R+jj/ljBCAm/yiC8fs1GTy7mzPBkH+LhEiEYJlx/HAVVfVXUI4hMEamtYUffbjkeCwrcpOTm9dBXDEiLOQ4ZV5Niisvws/TVqCHPwZj8ck4c/gISjUWotDGkuViPThl5suJImn4zXSo9pnJS5c2G5Pn62NMk2L3HaCmBPSeuFMbYah3XYgjQj7+K8LQ2HkXIwNl9pcJc/Pt8VarA7lVH5u9boct9YIe811iLAyKZ/h+xxN2stBKEE1Eb+HQnO6X6SrdmY+I0jjqsT1uy7yNwAE+ASlAu7iAw+L+nQB1ndi0F2/TWQ73J9Nw5E/GLtVkco9p0aCsiYvBX99Cu+02EMuICSRzfljKWfCD+TIlyX0HzDnLhFV+M3JVweSRLo1UWlyfOWdda3Re4mSUXk0YNyGegCnW/PFSjKgvm9ufYeEHTFoiLCGrsPknSsH5nrSFqCk/UCefupyJnNLfLB53SM8luedAJ9</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'

function Assert-TrustedPublicKeyMatchesSource {
    $sourcePath = Join-Path $projectRoot 'Boostix\UpdateFlow.cs'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "UpdateFlow.cs is missing: $sourcePath"
    }
    $source = [IO.File]::ReadAllText($sourcePath)
    $match = [regex]::Match(
        $source,
        'UpdateSigningPublicKeyXml\s*=\s*"(?<xml><RSAKeyValue>.*?</RSAKeyValue>)"\s*;',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success -or
        $match.Groups['xml'].Value -cne $trustedPublicKeyXml) {
        throw 'The canary public key does not match the updater trust anchor.'
    }
}

function Assert-AllowedHttpsUri {
    param(
        [Parameter(Mandatory = $true)][Uri]$Uri,
        [Parameter(Mandatory = $true)][string[]]$AllowedHosts
    )

    if (-not $Uri.IsAbsoluteUri -or
        $Uri.Scheme -cne 'https' -or
        -not [string]::IsNullOrEmpty($Uri.UserInfo) -or
        $Uri.Port -ne 443 -or
        $AllowedHosts -notcontains $Uri.DnsSafeHost.ToLowerInvariant()) {
        throw "The update endpoint is outside the HTTPS allowlist: $($Uri.GetLeftPart([UriPartial]::Path))"
    }
}

function Test-TransientDownloadFailure {
    param([Parameter(Mandatory = $true)][Net.WebException]$Exception)

    if ($Exception.Status -in @(
        [Net.WebExceptionStatus]::ConnectFailure,
        [Net.WebExceptionStatus]::ConnectionClosed,
        [Net.WebExceptionStatus]::NameResolutionFailure,
        [Net.WebExceptionStatus]::ProxyNameResolutionFailure,
        [Net.WebExceptionStatus]::ReceiveFailure,
        [Net.WebExceptionStatus]::SendFailure,
        [Net.WebExceptionStatus]::Timeout
    )) {
        return $true
    }
    $httpResponse = $Exception.Response -as [Net.HttpWebResponse]
    if ($httpResponse) {
        $status = [int]$httpResponse.StatusCode
        return $status -eq 408 -or $status -eq 429 -or
            $status -eq 500 -or $status -eq 502 -or
            $status -eq 503 -or $status -eq 504
    }
    return $false
}

function Invoke-OneBoundedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Address,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string[]]$AllowedHosts
    )

    $current = New-Object Uri($Address, [UriKind]::Absolute)
    for ($redirect = 0; $redirect -le 5; $redirect++) {
        Assert-AllowedHttpsUri -Uri $current -AllowedHosts $AllowedHosts
        $request = [Net.HttpWebRequest]::CreateHttp($current)
        $request.Method = 'GET'
        $request.AllowAutoRedirect = $false
        $request.AutomaticDecompression = [Net.DecompressionMethods]::None
        $request.Timeout = $RequestTimeoutSeconds * 1000
        $request.ReadWriteTimeout = $RequestTimeoutSeconds * 1000
        $request.UserAgent = 'Boostix-Public-Update-Canary/1.0'
        $request.Accept = 'application/octet-stream, application/json, text/plain'
        $request.Headers[[Net.HttpRequestHeader]::AcceptEncoding] = 'identity'
        $request.CachePolicy = New-Object Net.Cache.RequestCachePolicy(
            [Net.Cache.RequestCacheLevel]::NoCacheNoStore)
        if ($request.Proxy) {
            $request.Proxy.Credentials = [Net.CredentialCache]::DefaultCredentials
        }

        $response = $null
        try {
            $response = [Net.HttpWebResponse]$request.GetResponse()
            $status = [int]$response.StatusCode
            if ($status -in @(301, 302, 303, 307, 308)) {
                $location = $response.Headers['Location']
                if ([string]::IsNullOrWhiteSpace($location) -or $redirect -eq 5) {
                    throw 'The update endpoint returned an invalid redirect.'
                }
                $current = New-Object Uri($current, $location)
                continue
            }
            if ($status -ne 200) {
                throw "The update endpoint returned HTTP $status."
            }
            if ($response.ContentLength -gt $MaximumBytes) {
                throw "The response exceeds its maximum size: $($response.ContentLength) bytes."
            }
            if (-not [string]::IsNullOrWhiteSpace($response.ContentEncoding) -and
                $response.ContentEncoding -cne 'identity') {
                throw "The endpoint ignored identity encoding: $($response.ContentEncoding)"
            }

            $input = $response.GetResponseStream()
            try {
                $output = New-Object IO.FileStream(
                    $Destination,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None,
                    65536,
                    [IO.FileOptions]::WriteThrough)
                try {
                    $buffer = New-Object byte[] 65536
                    [long]$total = 0
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $total += $read
                        if ($total -gt $MaximumBytes) {
                            throw "The response exceeded its maximum size of $MaximumBytes bytes."
                        }
                        $output.Write($buffer, 0, $read)
                    }
                    $output.Flush($true)
                }
                finally {
                    $output.Dispose()
                }
            }
            finally {
                $input.Dispose()
            }
            if ((Get-Item -LiteralPath $Destination).Length -le 0) {
                throw 'The endpoint returned an empty file.'
            }
            return
        }
        finally {
            if ($response) {
                $response.Dispose()
            }
            $request.Abort()
        }
    }
    throw 'The update endpoint exceeded the redirect limit.'
}

function Invoke-BoundedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Address,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string[]]$AllowedHosts
    )

    for ($attempt = 1; $attempt -le $DownloadAttempts; $attempt++) {
        if (Test-Path -LiteralPath $Destination) {
            Remove-Item -LiteralPath $Destination -Force
        }
        try {
            Invoke-OneBoundedDownload `
                -Address $Address `
                -Destination $Destination `
                -MaximumBytes $MaximumBytes `
                -AllowedHosts $AllowedHosts
            return
        }
        catch {
            $failure = $_.Exception
            while (($failure -is [Reflection.TargetInvocationException] -or
                $failure -is [Management.Automation.MethodInvocationException]) -and
                $failure.InnerException) {
                $failure = $failure.InnerException
            }
            $retry = $failure -is [IO.IOException]
            if ($failure -is [Net.WebException]) {
                $retry = Test-TransientDownloadFailure -Exception $failure
                if ($failure.Response) {
                    $failure.Response.Dispose()
                }
            }
            elseif ($failure -isnot [IO.IOException]) {
                throw $failure
            }
            if (-not $retry -or $attempt -eq $DownloadAttempts) {
                $safeAddress = (New-Object Uri(
                    $Address,
                    [UriKind]::Absolute)).GetLeftPart([UriPartial]::Path)
                throw (
                    "Public channel download failed: $safeAddress. " +
                    $failure.Message)
            }
        }
        Start-Sleep -Milliseconds (500 * [Math]::Pow(2, $attempt - 1))
    }
}

function Assert-SignedManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$SignaturePath
    )

    $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
    if ($manifestBytes.Length -le 0 -or $manifestBytes.Length -gt 16384) {
        throw 'The public manifest has an invalid size.'
    }
    $signatureText = [IO.File]::ReadAllText(
        $SignaturePath,
        [Text.Encoding]::UTF8).Trim()
    if ($signatureText -cnotmatch '^[A-Za-z0-9+/]+={0,2}$') {
        throw 'The public manifest signature is not canonical Base64.'
    }
    try {
        $signature = [Convert]::FromBase64String($signatureText)
    }
    catch [FormatException] {
        throw 'The public manifest signature is invalid Base64.'
    }
    if ($signature.Length -ne 384) {
        throw "The public manifest signature has an invalid length: $($signature.Length)."
    }

    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    try {
        $rsa.PersistKeyInCsp = $false
        $rsa.FromXmlString($trustedPublicKeyXml)
        $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
        if (-not $rsa.VerifyData($manifestBytes, $sha256Oid, $signature)) {
            throw 'The public manifest signature is not trusted.'
        }
    }
    finally {
        $rsa.Dispose()
        [Array]::Clear($signature, 0, $signature.Length)
        [Array]::Clear($manifestBytes, 0, $manifestBytes.Length)
    }
}

function Assert-ManagedPeFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 512 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw 'The installer does not contain a valid DOS/PE header.'
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 256) {
            throw 'The installer PE header offset is invalid.'
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'The installer PE signature is invalid.'
        }
        $machine = $reader.ReadUInt16()
        [void]$reader.ReadUInt16()
        $stream.Position += 12
        $optionalHeaderSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        if ($machine -ne 0x014C -or
            ($characteristics -band 0x0002) -eq 0 -or
            $optionalHeaderSize -lt 224) {
            throw 'The installer is not the expected executable PE32 image.'
        }
        $optionalHeader = $peOffset + 24
        $stream.Position = $optionalHeader
        if ($reader.ReadUInt16() -ne 0x010B) {
            throw 'The installer optional header is not PE32.'
        }
        $stream.Position = $optionalHeader + 92
        $directoryCount = $reader.ReadUInt32()
        if ($directoryCount -lt 15) {
            throw 'The installer has no CLR data-directory entry.'
        }
        $stream.Position = $optionalHeader + 96 + (14 * 8)
        $clrRva = $reader.ReadUInt32()
        $clrSize = $reader.ReadUInt32()
        if ($clrRva -eq 0 -or $clrSize -lt 72) {
            throw 'The installer is not a managed .NET PE image.'
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-ReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedSize,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedProductName,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedFileName
    )

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $ExpectedSize) {
        throw "$Role has size $($file.Length); expected $ExpectedSize."
    }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($hash -cne $ExpectedSha256) {
        throw "$Role has SHA-256 $hash; expected $ExpectedSha256."
    }
    Assert-ManagedPeFile -Path $Path

    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $fileVersion = $ExpectedVersion + '.0'
    if ($metadata.ProductName -cne $ExpectedProductName -or
        $metadata.CompanyName -cne 'Silas Suspect' -or
        $metadata.FileVersion -cne $fileVersion -or
        $metadata.ProductVersion -cne $fileVersion -or
        $metadata.OriginalFilename -cne $ExpectedFileName -or
        $metadata.InternalName -cne $ExpectedFileName) {
        throw "$Role has unexpected Windows version-resource metadata."
    }

    $authenticode = Get-AuthenticodeSignature -LiteralPath $Path
    if ($RequireAuthenticode -and $authenticode.Status -ne 'Valid') {
        throw "$Role does not have a valid Authenticode signature: $($authenticode.Status)."
    }
    Write-Host (
        "Verified ${Role}: size=$ExpectedSize sha256=$ExpectedSha256 " +
        "Authenticode=$($authenticode.Status)")
}

function Copy-OfflineFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][long]$MaximumBytes
    )

    $file = Get-Item -LiteralPath $Source
    if ($file.Length -le 0 -or $file.Length -gt $MaximumBytes) {
        throw "Offline fixture has an invalid size: $Source"
    }
    [IO.File]::Copy($file.FullName, $Destination, $false)
}

$oldSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
$canaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-PublicChannel-' + [Guid]::NewGuid().ToString('N'))
try {
    [Net.ServicePointManager]::SecurityProtocol =
        $oldSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Assert-TrustedPublicKeyMatchesSource
    [void][IO.Directory]::CreateDirectory($canaryRoot)

    $rawManifestPath = Join-Path $canaryRoot 'raw-update-v2.json'
    $rawSignaturePath = Join-Path $canaryRoot 'raw-update-v2.json.sig'
    $mirrorManifestPath = Join-Path $canaryRoot 'mirror-update-v2.json'
    $mirrorSignaturePath = Join-Path $canaryRoot 'mirror-update-v2.json.sig'
    $legacyManifestPath = Join-Path $canaryRoot 'legacy-update.json'
    $legacySignaturePath = Join-Path $canaryRoot 'legacy-update.json.sig'

    if ([string]::IsNullOrWhiteSpace($OfflineFixtureRoot)) {
        Invoke-BoundedDownload -Address $rawManifestUrl `
            -Destination $rawManifestPath -MaximumBytes 16384 `
            -AllowedHosts @('raw.githubusercontent.com')
        Invoke-BoundedDownload -Address $rawSignatureUrl `
            -Destination $rawSignaturePath -MaximumBytes 1024 `
            -AllowedHosts @('raw.githubusercontent.com')
        Invoke-BoundedDownload -Address $mirrorManifestUrl `
            -Destination $mirrorManifestPath -MaximumBytes 16384 `
            -AllowedHosts @('cdn.jsdelivr.net')
        Invoke-BoundedDownload -Address $mirrorSignatureUrl `
            -Destination $mirrorSignaturePath -MaximumBytes 1024 `
            -AllowedHosts @('cdn.jsdelivr.net')
        Invoke-BoundedDownload -Address $legacyManifestUrl `
            -Destination $legacyManifestPath -MaximumBytes 16384 `
            -AllowedHosts @('raw.githubusercontent.com')
        Invoke-BoundedDownload -Address $legacySignatureUrl `
            -Destination $legacySignaturePath -MaximumBytes 1024 `
            -AllowedHosts @('raw.githubusercontent.com')
    }
    else {
        $fixtureRoot = (Resolve-Path -LiteralPath $OfflineFixtureRoot).Path
        Copy-OfflineFixture -Source (Join-Path $fixtureRoot 'update-v2.json') `
            -Destination $rawManifestPath -MaximumBytes 16384
        Copy-OfflineFixture -Source (Join-Path $fixtureRoot 'update-v2.json.sig') `
            -Destination $rawSignaturePath -MaximumBytes 1024
        [IO.File]::Copy($rawManifestPath, $mirrorManifestPath, $false)
        [IO.File]::Copy($rawSignaturePath, $mirrorSignaturePath, $false)
        Copy-OfflineFixture -Source (Join-Path $fixtureRoot 'update.json') `
            -Destination $legacyManifestPath -MaximumBytes 16384
        Copy-OfflineFixture -Source (Join-Path $fixtureRoot 'update.json.sig') `
            -Destination $legacySignaturePath -MaximumBytes 1024
    }

    Assert-SignedManifest -ManifestPath $rawManifestPath `
        -SignaturePath $rawSignaturePath
    Assert-SignedManifest -ManifestPath $mirrorManifestPath `
        -SignaturePath $mirrorSignaturePath
    Assert-SignedManifest -ManifestPath $legacyManifestPath `
        -SignaturePath $legacySignaturePath
    if ((Get-FileHash $rawManifestPath -Algorithm SHA256).Hash -cne
            (Get-FileHash $mirrorManifestPath -Algorithm SHA256).Hash -or
        (Get-FileHash $rawSignaturePath -Algorithm SHA256).Hash -cne
            (Get-FileHash $mirrorSignaturePath -Algorithm SHA256).Hash) {
        throw 'Raw GitHub and jsDelivr publish different signed manifests.'
    }

    $manifest = Get-Content -LiteralPath $rawManifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $legacyManifest = Get-Content -LiteralPath $legacyManifestPath `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    $expectedManifestFields = @(
        'schemaVersion',
        'version',
        'installerUrl',
        'sha256',
        'size',
        'boostixInstallerUrl',
        'boostixSha256',
        'boostixSize')
    $actualManifestFields = @($manifest.PSObject.Properties.Name)
    if ($actualManifestFields.Count -ne $expectedManifestFields.Count -or
        @($expectedManifestFields | Where-Object {
            $actualManifestFields -cnotcontains $_
        }).Count -ne 0) {
        throw 'update-v2.json must contain exactly the eight schema-v1 fields.'
    }
    $expectedLegacyFields = @(
        'schemaVersion',
        'version',
        'installerUrl',
        'sha256',
        'size')
    $actualLegacyFields = @($legacyManifest.PSObject.Properties.Name)
    if ($actualLegacyFields.Count -ne $expectedLegacyFields.Count -or
        @($expectedLegacyFields | Where-Object {
            $actualLegacyFields -cnotcontains $_
        }).Count -ne 0) {
        throw 'Legacy update.json must contain exactly the five schema-v1 fields.'
    }
    if ($manifest.schemaVersion -isnot [int] -or
        $manifest.version -isnot [string] -or
        $manifest.installerUrl -isnot [string] -or
        $manifest.sha256 -isnot [string] -or
        $manifest.size -isnot [int] -or
        $manifest.boostixInstallerUrl -isnot [string] -or
        $manifest.boostixSha256 -isnot [string] -or
        $manifest.boostixSize -isnot [int]) {
        throw 'update-v2.json contains an unexpected field type.'
    }
    if ($legacyManifest.schemaVersion -isnot [int] -or
        $legacyManifest.version -isnot [string] -or
        $legacyManifest.installerUrl -isnot [string] -or
        $legacyManifest.sha256 -isnot [string] -or
        $legacyManifest.size -isnot [int]) {
        throw 'Legacy update.json contains an unexpected field type.'
    }
    $version = [string]$manifest.version
    if ($version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and
         $version -cne $ExpectedVersion)) {
        throw "The public channel version is unexpected: $version"
    }
    if ([int]$manifest.schemaVersion -ne 1) {
        throw "The public manifest schema is unsupported: $($manifest.schemaVersion)"
    }

    $canonicalFileName = 'Boostix-Setup-' + $version + '.exe'
    $legacyFileName = 'MajesticBoost-Setup-' + $version + '.exe'
    $expectedRawInstaller =
        'https://raw.githubusercontent.com/' + $repository +
        '/main/dist/' + $canonicalFileName
    $expectedLegacyInstaller =
        'https://raw.githubusercontent.com/' + $legacyRepository +
        '/main/dist/' + $legacyFileName
    if ([string]$manifest.boostixInstallerUrl -cne $expectedRawInstaller -or
        [string]$manifest.installerUrl -cne $expectedLegacyInstaller -or
        [string]$manifest.boostixSha256 -cnotmatch '^[0-9A-F]{64}$' -or
        [string]$manifest.sha256 -cnotmatch '^[0-9A-F]{64}$') {
        throw 'The public manifest contains an unexpected URL or SHA-256 value.'
    }
    $canonicalSize = [long]$manifest.boostixSize
    $legacySize = [long]$manifest.size
    if ($canonicalSize -le 0 -or $canonicalSize -gt $MaximumInstallerBytes -or
        $legacySize -le 0 -or $legacySize -gt $MaximumInstallerBytes) {
        throw 'The public manifest contains an invalid installer size.'
    }
    $legacyBridgeErrors = New-Object Collections.Generic.List[string]
    if ([int]$legacyManifest.schemaVersion -ne 1) {
        $legacyBridgeErrors.Add(
            "schemaVersion=$($legacyManifest.schemaVersion), expected 1")
    }
    if ([string]$legacyManifest.version -cne $version) {
        $legacyBridgeErrors.Add(
            "version=$($legacyManifest.version), expected $version")
    }
    if ([string]$legacyManifest.installerUrl -cne $expectedLegacyInstaller) {
        $legacyBridgeErrors.Add('installerUrl does not match the v2 bridge')
    }
    if ([string]$legacyManifest.sha256 -cne [string]$manifest.sha256) {
        $legacyBridgeErrors.Add('sha256 does not match the v2 bridge')
    }
    if ([long]$legacyManifest.size -ne $legacySize) {
        $legacyBridgeErrors.Add(
            "size=$($legacyManifest.size), expected $legacySize")
    }
    if ($legacyBridgeErrors.Count -ne 0) {
        throw (
            'Legacy update.json is not linked to the v2 compatibility bridge: ' +
            [string]::Join('; ', $legacyBridgeErrors.ToArray()) + '.')
    }

    $rawInstallerPath = Join-Path $canaryRoot 'raw-boostix-setup.exe'
    $latestInstallerPath = Join-Path $canaryRoot 'raw-boostix-latest.exe'
    $releaseInstallerPath = Join-Path $canaryRoot 'release-boostix-setup.exe'
    $jsDelivrInstallerPath = Join-Path $canaryRoot 'jsdelivr-boostix-setup.exe'
    $legacyInstallerPath = Join-Path $canaryRoot 'legacy-bridge-setup.exe'
    $releaseInstallerUrl =
        'https://github.com/' + $repository + '/releases/download/v' +
        $version + '/' + $canonicalFileName
    $latestInstallerUrl =
        'https://raw.githubusercontent.com/' + $repository +
        '/main/dist/Boostix-Setup-Latest.exe'
    $jsDelivrInstallerUrl =
        'https://cdn.jsdelivr.net/gh/' + $repository + '@v' + $version +
        '/dist/' + $canonicalFileName

    if ([string]::IsNullOrWhiteSpace($OfflineFixtureRoot)) {
        Invoke-BoundedDownload -Address $expectedRawInstaller `
            -Destination $rawInstallerPath -MaximumBytes $canonicalSize `
            -AllowedHosts @('raw.githubusercontent.com')
        Invoke-BoundedDownload -Address $latestInstallerUrl `
            -Destination $latestInstallerPath -MaximumBytes $canonicalSize `
            -AllowedHosts @('raw.githubusercontent.com')
        Invoke-BoundedDownload -Address $releaseInstallerUrl `
            -Destination $releaseInstallerPath -MaximumBytes $canonicalSize `
            -AllowedHosts @(
                'github.com',
                'release-assets.githubusercontent.com',
                'objects.githubusercontent.com')
        Invoke-BoundedDownload -Address $jsDelivrInstallerUrl `
            -Destination $jsDelivrInstallerPath -MaximumBytes $canonicalSize `
            -AllowedHosts @('cdn.jsdelivr.net')
        Invoke-BoundedDownload -Address $expectedLegacyInstaller `
            -Destination $legacyInstallerPath -MaximumBytes $legacySize `
            -AllowedHosts @('raw.githubusercontent.com')
    }
    else {
        $fixtureRoot = (Resolve-Path -LiteralPath $OfflineFixtureRoot).Path
        $fixtureCanonical = Join-Path $fixtureRoot ('dist\' + $canonicalFileName)
        $fixtureLatest = Join-Path $fixtureRoot 'dist\Boostix-Setup-Latest.exe'
        $fixtureLegacy = Join-Path $fixtureRoot ('dist\' + $legacyFileName)
        foreach ($destination in @(
            $rawInstallerPath,
            $releaseInstallerPath,
            $jsDelivrInstallerPath
        )) {
            Copy-OfflineFixture -Source $fixtureCanonical `
                -Destination $destination -MaximumBytes $canonicalSize
        }
        Copy-OfflineFixture -Source $fixtureLatest `
            -Destination $latestInstallerPath -MaximumBytes $canonicalSize
        Copy-OfflineFixture -Source $fixtureLegacy `
            -Destination $legacyInstallerPath -MaximumBytes $legacySize
    }

    foreach ($artifact in @(
        @{ Role = 'raw canonical installer'; Path = $rawInstallerPath },
        @{ Role = 'raw latest installer'; Path = $latestInstallerPath },
        @{ Role = 'GitHub Release asset'; Path = $releaseInstallerPath },
        @{ Role = 'jsDelivr tagged fallback'; Path = $jsDelivrInstallerPath }
    )) {
        Assert-ReleaseArtifact -Role $artifact.Role -Path $artifact.Path `
            -ExpectedSize $canonicalSize `
            -ExpectedSha256 ([string]$manifest.boostixSha256) `
            -ExpectedProductName 'Boostix' -ExpectedVersion $version `
            -ExpectedFileName $canonicalFileName
    }
    Assert-ReleaseArtifact -Role 'legacy raw compatibility bridge' `
        -Path $legacyInstallerPath -ExpectedSize $legacySize `
        -ExpectedSha256 ([string]$manifest.sha256) `
        -ExpectedProductName 'Majestic Boost' -ExpectedVersion $version `
        -ExpectedFileName $legacyFileName

    $mode = if ([string]::IsNullOrWhiteSpace($OfflineFixtureRoot)) {
        'public network'
    }
    else {
        'offline fixture'
    }
    Write-Host (
        "PASS: Boostix $version update channel is internally consistent " +
        "($mode). No downloaded executable was started.") -ForegroundColor Green
}
finally {
    [Net.ServicePointManager]::SecurityProtocol = $oldSecurityProtocol
    if (Test-Path -LiteralPath $canaryRoot) {
        $resolvedCanaryRoot = [IO.Path]::GetFullPath($canaryRoot)
        $resolvedTempRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $leaf = Split-Path -Leaf $resolvedCanaryRoot
        $item = Get-Item -LiteralPath $resolvedCanaryRoot -Force
        if (-not $resolvedCanaryRoot.StartsWith(
                $resolvedTempRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            $leaf -cnotmatch '^Boostix-PublicChannel-[0-9a-f]{32}$' -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to clean an unexpected canary path: $resolvedCanaryRoot"
        }
        Remove-Item -LiteralPath $resolvedCanaryRoot -Recurse -Force
    }
}
