[CmdletBinding()]
param(
    [string]$ApplicationPath,
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public sealed class MajesticBoostSlowDripServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Thread worker;
    private readonly int chunks;
    private readonly int delayMilliseconds;

    public MajesticBoostSlowDripServer(int chunks, int delayMilliseconds)
    {
        this.chunks = chunks;
        this.delayMilliseconds = delayMilliseconds;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = "http://127.0.0.1:" + port + "/update-v2.json";
        worker = new Thread(Serve);
        worker.IsBackground = true;
        worker.Start();
    }

    public string Address { get; private set; }

    private void Serve()
    {
        try
        {
            using (TcpClient client = listener.AcceptTcpClient())
            using (NetworkStream stream = client.GetStream())
            {
                int matched = 0;
                byte[] terminator = new byte[] { 13, 10, 13, 10 };
                while (matched < terminator.Length)
                {
                    int value = stream.ReadByte();
                    if (value < 0)
                    {
                        return;
                    }
                    matched = value == terminator[matched]
                        ? matched + 1
                        : (value == terminator[0] ? 1 : 0);
                }

                byte[] header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Content-Length: " + chunks + "\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Flush();
                for (int index = 0; index < chunks; index++)
                {
                    Thread.Sleep(delayMilliseconds);
                    stream.WriteByte((byte)'x');
                    stream.Flush();
                }
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        listener.Stop();
        if (worker.IsAlive)
        {
            worker.Join(2000);
        }
    }
}
'@

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'ProductBrand.cs'))
$versionMatch = [regex]::Match(
    $brandSource,
    'ProductVersion\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
    throw 'The release version was not found in ProductBrand.cs.'
}
$releaseVersion = $versionMatch.Groups['version'].Value
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot (
        'dist\Boostix-Setup-' + $releaseVersion + '.exe')
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$testRoot = Join-Path $env:TEMP ('MajesticBoost-UpdaterValidation-Test-' + [Guid]::NewGuid().ToString('N'))
$fixturePath = Join-Path $testRoot (Split-Path -Leaf $InstallerPath)

function Get-ChildWriterResult {
    param([Parameter(Mandatory = $true)][string]$Path)

    $command = @'
$ErrorActionPreference = 'Stop'
try {
    $stream = [IO.File]::Open(
        $env:MAJESTICBOOST_VALIDATION_FIXTURE,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Write,
        [IO.FileShare]::ReadWrite)
    $stream.Dispose()
    [Console]::Write('OPENED')
}
catch [IO.IOException] {
    [Console]::Write('BLOCKED')
}
'@
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $startInfo.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + $encodedCommand
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['MAJESTICBOOST_VALIDATION_FIXTURE'] = $Path
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            throw 'Timed out waiting for the cross-process writer probe.'
        }
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        if ($process.ExitCode -ne 0) {
            throw "Cross-process writer probe failed: $errorOutput"
        }
        return $output
    }
    finally {
        $process.Dispose()
    }
}

$sourcePath = Join-Path $projectRoot 'Boostix\UpdateFlow.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$downloadClose = $source.IndexOf('using (var downloadStream = new FileStream(', [StringComparison]::Ordinal)
$verificationOpen = $source.IndexOf('using (FileStream verificationStream = OpenInstallerForVerification(installerPath))', [StringComparison]::Ordinal)
if ($downloadClose -lt 0 -or $verificationOpen -le $downloadClose) {
    throw 'The updater does not close the write-capable download stream before verification.'
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$overlayType = $assembly.GetType('Boostix.UpdateFlowOverlay', $true, $false)
$staticFlags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$nestedFlags = [Reflection.BindingFlags]::NonPublic
$openMethod = $overlayType.GetMethod('OpenInstallerForVerification', $staticFlags)
$validateMethod = $overlayType.GetMethod('ValidateHeldInstaller', $staticFlags)
$refreshMethod = $overlayType.GetMethod('RefreshAvailableUpdateAsync', [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance)
$lockMethod = $overlayType.GetMethod('TryAcquireUpdateLock', $staticFlags)
$buildHeadAddressMethod = $overlayType.GetMethod('BuildRepositoryHeadRequestAddress', $staticFlags)
$parseHeadMethod = $overlayType.GetMethod('ParseRepositoryHeadCommit', $staticFlags)
$buildImmutableAddressMethod = $overlayType.GetMethod('BuildImmutableManifestAddress', $staticFlags)
$transientFailureMethod = $overlayType.GetMethod('IsTransientManifestFailure', $staticFlags)
$findInstallerNetworkFailureMethod = $overlayType.GetMethod(
    'FindInstallerNetworkFailure',
    $staticFlags)
$transientInstallerFailureMethod = $overlayType.GetMethod(
    'IsTransientInstallerDownloadFailure',
    $staticFlags)
$networkFailurePriorityMethod = $overlayType.GetMethod(
    'GetInstallerNetworkFailurePriority',
    $staticFlags)
$selectNetworkFailureMethod = $overlayType.GetMethod(
    'SelectRepresentativeInstallerNetworkFailure',
    $staticFlags)
$networkFailureMessageMethod = $overlayType.GetMethod(
    'GetUpdateNetworkFailureMessage',
    $staticFlags)
$installerNetworkExceptionType = $overlayType.GetNestedType(
    'InstallerNetworkException',
    $nestedFlags)
$waitForRetryMethod = $overlayType.GetMethod('WaitForManifestRetry', $staticFlags)
$manifestTotalTimeoutField = $overlayType.GetField(
    'ManifestTotalTimeoutMilliseconds',
    $staticFlags)
$downloadSmallFileMethod = $overlayType.GetMethod('DownloadSmallFile', $staticFlags)
$createRequestMethod = $overlayType.GetMethod('CreateRequest', $staticFlags)
$parseManifestMethod = $overlayType.GetMethod('ParseAndValidateManifest', $staticFlags)
if (-not $openMethod -or -not $validateMethod -or -not $refreshMethod -or -not $lockMethod -or
    -not $buildHeadAddressMethod -or -not $parseHeadMethod -or -not $buildImmutableAddressMethod -or
    -not $transientFailureMethod -or -not $waitForRetryMethod -or
    -not $findInstallerNetworkFailureMethod -or
    -not $transientInstallerFailureMethod -or
    -not $networkFailurePriorityMethod -or
    -not $selectNetworkFailureMethod -or
    -not $networkFailureMessageMethod -or
    -not $installerNetworkExceptionType -or
    -not $manifestTotalTimeoutField -or -not $downloadSmallFileMethod -or
    -not $createRequestMethod -or -not $parseManifestMethod) {
    throw 'Compiled updater validation helpers were not found.'
}

$networkProbe = [Net.WebException]::new(
    'network read failed',
    [Net.WebExceptionStatus]::ReceiveFailure)
$wrappedNetworkProbe = [IO.IOException]::new(
    'stream read failed',
    $networkProbe)
$classificationArguments = New-Object 'object[]' 1
$classificationArguments[0] = $wrappedNetworkProbe
$classifiedNetwork = $findInstallerNetworkFailureMethod.Invoke(
    $null,
    $classificationArguments)
if (-not [object]::ReferenceEquals($classifiedNetwork, $networkProbe)) {
    throw 'A WebException wrapped by an IO stream failure was not classified as network.'
}
$classificationArguments[0] = [IO.IOException]::new('disk full')
if ($null -ne $findInstallerNetworkFailureMethod.Invoke(
        $null,
        $classificationArguments)) {
    throw 'A local storage IOException was misclassified as a network failure.'
}
$classificationArguments[0] = $networkProbe
if (-not [bool]$transientInstallerFailureMethod.Invoke(
        $null,
        $classificationArguments)) {
    throw 'A transient installer download failure is not eligible for bounded retry.'
}
$classificationArguments[0] = [Net.WebException]::new(
    'TLS trust failed',
    [Net.WebExceptionStatus]::TrustFailure)
if ([bool]$transientInstallerFailureMethod.Invoke(
        $null,
        $classificationArguments)) {
    throw 'A TLS trust failure must not be retried as a transient download outage.'
}

$networkConstructor = @(
    $installerNetworkExceptionType.GetConstructors(
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Instance)
)[0]
$proxyFailure = $networkConstructor.Invoke([object[]]@(
    'proxy authentication failed',
    [Net.WebException]::new(
        'protocol error',
        [Net.WebExceptionStatus]::ProtocolError),
    [Net.WebExceptionStatus]::ProtocolError,
    407
))
$timeoutFailure = [Net.WebException]::new(
    'download timed out',
    [Net.WebExceptionStatus]::Timeout)
$priorityArguments = New-Object 'object[]' 1
$priorityArguments[0] = $proxyFailure
$proxyPriority = [int]$networkFailurePriorityMethod.Invoke(
    $null,
    $priorityArguments)
$priorityArguments[0] = $timeoutFailure
$timeoutPriority = [int]$networkFailurePriorityMethod.Invoke(
    $null,
    $priorityArguments)
if ($proxyPriority -le $timeoutPriority) {
    throw 'Proxy authentication is not preserved as the most actionable download failure.'
}
$networkFailures = New-Object 'System.Collections.Generic.List[System.Exception]'
$networkFailures.Add($timeoutFailure)
$networkFailures.Add($proxyFailure)
$selectionArguments = New-Object 'object[]' 1
$selectionArguments[0] = $networkFailures.PSObject.BaseObject
$selectedFailure = $selectNetworkFailureMethod.Invoke(
    $null,
    $selectionArguments)
if (-not [object]::ReferenceEquals($selectedFailure, $proxyFailure)) {
    throw 'The final updater error discarded the actionable proxy failure.'
}
$messageArguments = New-Object 'object[]' 1
$messageArguments[0] = $proxyFailure
$proxyMessage = [string]$networkFailureMessageMethod.Invoke(
    $null,
    $messageArguments)
$messageArguments[0] = $timeoutFailure
$timeoutMessage = [string]$networkFailureMessageMethod.Invoke(
    $null,
    $messageArguments)
if ([string]::IsNullOrWhiteSpace($proxyMessage) -or
    [string]::IsNullOrWhiteSpace($timeoutMessage) -or
    $proxyMessage -ceq $timeoutMessage) {
    throw 'The updater no longer provides category-specific network guidance.'
}

$headAddressArguments = New-Object 'object[]' 1
$headAddressArguments[0] = 'test-token-1'
$headAddress = [string]$buildHeadAddressMethod.Invoke($null, $headAddressArguments)
if ($headAddress -cne 'https://api.github.com/repos/alosev394-ai/Boostix/git/ref/heads/main?mb=test-token-1') {
    throw "Repository-head cache-busting URL is unexpected: $headAddress"
}

$commitSha = '8a3231c14cf92876a62be73395ca8ec7fe86d9a6'
$headFixture = [Text.Encoding]::UTF8.GetBytes(
    '{"ref":"refs/heads/main","object":{"sha":"' + $commitSha + '","type":"commit"}}')
$parseArguments = New-Object 'object[]' 1
$parseArguments[0] = $headFixture
if ([string]$parseHeadMethod.Invoke($null, $parseArguments) -cne $commitSha) {
    throw 'Repository-head commit parsing failed.'
}
$parseArguments[0] = [Text.Encoding]::UTF8.GetBytes(
    '<html>corporate proxy response</html>')
try {
    [void]$parseHeadMethod.Invoke($null, $parseArguments)
    throw 'Malformed HTTP-200 repository-head content was accepted.'
}
catch {
    $failure = $_.Exception
    while (($failure -is [Reflection.TargetInvocationException] -or
        $failure -is [Management.Automation.MethodInvocationException]) -and
        $failure.InnerException) {
        $failure = $failure.InnerException
    }
    if ($failure -isnot [IO.InvalidDataException]) {
        throw $failure
    }
}
$parseArguments[0] = $headFixture
$fetchStart = $source.IndexOf(
    'private UpdateManifest FetchAndValidateManifest()',
    [StringComparison]::Ordinal)
$fallbackStart = $source.IndexOf(
    'if (useSignedMainFallback)',
    $fetchStart,
    [StringComparison]::Ordinal)
$malformedHeadFallback = $source.IndexOf(
    'catch (InvalidDataException ex)',
    $fetchStart,
    [StringComparison]::Ordinal)
if ($fetchStart -lt 0 -or $fallbackStart -lt 0 -or
    $malformedHeadFallback -lt $fetchStart -or
    $malformedHeadFallback -gt $fallbackStart) {
    throw 'Malformed repository-head data does not enter the signed-main fallback.'
}

$immutableArguments = New-Object 'object[]' 2
$immutableArguments[0] = $commitSha
$immutableArguments[1] = 'update-v2.json'
$immutableAddress = [string]$buildImmutableAddressMethod.Invoke($null, $immutableArguments)
if ($immutableAddress -cne ('https://raw.githubusercontent.com/alosev394-ai/Boostix/' + $commitSha + '/update-v2.json')) {
    throw "Immutable manifest URL is unexpected: $immutableAddress"
}

$transientArguments = New-Object 'object[]' 1
foreach ($status in @(
    [Net.WebExceptionStatus]::ConnectFailure,
    [Net.WebExceptionStatus]::ConnectionClosed,
    [Net.WebExceptionStatus]::NameResolutionFailure,
    [Net.WebExceptionStatus]::ReceiveFailure,
    [Net.WebExceptionStatus]::SendFailure,
    [Net.WebExceptionStatus]::Timeout
)) {
    $transientArguments[0] = [Net.WebException]::new(
        'temporary connection failure',
        $status)
    if (-not [bool]$transientFailureMethod.Invoke($null, $transientArguments)) {
        throw "A temporary $status failure is not eligible for a bounded retry."
    }
}
foreach ($status in @(
    [Net.WebExceptionStatus]::TrustFailure,
    [Net.WebExceptionStatus]::SecureChannelFailure
)) {
    $transientArguments[0] = [Net.WebException]::new(
        'certificate failure',
        $status)
    if ([bool]$transientFailureMethod.Invoke($null, $transientArguments)) {
        throw "A $status failure must not be retried as a transient outage."
    }
}

$budgetArguments = New-Object 'object[]' 3
$manifestTotalTimeoutMilliseconds =
    [int]$manifestTotalTimeoutField.GetRawConstantValue()
if ($manifestTotalTimeoutMilliseconds -ne 45000) {
    throw "The compiled manifest total timeout is unexpected: $manifestTotalTimeoutMilliseconds"
}
$budgetArguments[0] = [Diagnostics.Stopwatch]::StartNew()
$budgetArguments[1] = $manifestTotalTimeoutMilliseconds
$budgetArguments[2] = [Net.WebException]::new(
    'temporary connection failure',
    [Net.WebExceptionStatus]::ConnectFailure)
try {
    [void]$waitForRetryMethod.Invoke($null, $budgetArguments)
    throw 'A retry delay larger than the total startup budget was accepted.'
}
catch {
    $budgetFailure = $_.Exception
    while (($budgetFailure -is [Reflection.TargetInvocationException] -or
        $budgetFailure -is [Management.Automation.MethodInvocationException]) -and
        $budgetFailure.InnerException) {
        $budgetFailure = $budgetFailure.InnerException
    }
    if ($budgetFailure -isnot [Net.WebException] -or
        $budgetFailure.Status -ne [Net.WebExceptionStatus]::Timeout) {
        throw $budgetFailure
    }
}
finally {
    $budgetArguments[0].Stop()
}

$slowServer = New-Object MajesticBoostSlowDripServer -ArgumentList 5, 450
$slowTimer = [Diagnostics.Stopwatch]::StartNew()
$slowArguments = New-Object 'object[]' 5
$slowArguments[0] = $slowServer.Address
$slowArguments[1] = 16
$slowArguments[2] = 5000
$slowArguments[3] = $slowTimer
$slowArguments[4] = 900
try {
    [void]$downloadSmallFileMethod.Invoke($null, $slowArguments)
    throw 'A slow-drip response bypassed the hard update-check deadline.'
}
catch {
    $slowFailure = $_.Exception
    while (($slowFailure -is [Reflection.TargetInvocationException] -or
        $slowFailure -is [Management.Automation.MethodInvocationException]) -and
        $slowFailure.InnerException) {
        $slowFailure = $slowFailure.InnerException
    }
    if ($slowFailure -isnot [Net.WebException] -or
        $slowFailure.Status -ne [Net.WebExceptionStatus]::Timeout) {
        throw $slowFailure
    }
}
finally {
    $slowTimer.Stop()
    $slowServer.Dispose()
}

$requestArguments = New-Object 'object[]' 2
$requestArguments[0] = $headAddress
$requestArguments[1] = 5000
$request = [Net.HttpWebRequest]$createRequestMethod.Invoke($null, $requestArguments)
try {
    if ($request.CachePolicy.Level -cne [Net.Cache.HttpRequestCacheLevel]::NoCacheNoStore -or
        $request.Headers[[Net.HttpRequestHeader]::CacheControl] -cne 'no-cache' -or
        $request.Headers[[Net.HttpRequestHeader]::Pragma] -cne 'no-cache') {
        throw 'The compiled updater request does not bypass stale HTTP cache entries.'
    }
}
finally {
    $request.Abort()
}

$manifestType = $overlayType.GetNestedType('UpdateManifest', $nestedFlags)
$semanticType = $overlayType.GetNestedType('SemanticVersion', $nestedFlags)
if (-not $manifestType -or -not $semanticType) {
    throw 'Compiled updater manifest types were not found.'
}

$legacyInstallerUrl =
    'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/' +
    'MajesticBoost-Setup-' + $releaseVersion + '.exe'
$boostixInstallerUrl =
    'https://raw.githubusercontent.com/alosev394-ai/Boostix/main/dist/' +
    'Boostix-Setup-' + $releaseVersion + '.exe'
$dualManifestJson = @"
{
  "schemaVersion": 1,
  "version": "$releaseVersion",
  "installerUrl": "$legacyInstallerUrl",
  "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "size": 123,
  "boostixInstallerUrl": "$boostixInstallerUrl",
  "boostixSha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
  "boostixSize": 456
}
"@
$parseManifestArguments = New-Object 'object[]' 1
$parseManifestArguments[0] = [Text.Encoding]::UTF8.GetBytes($dualManifestJson)
$parsedDualManifest = $parseManifestMethod.Invoke($null, $parseManifestArguments)
$selectedInstallerUrl = [string]$manifestType.GetField(
    'InstallerUrl').GetValue($parsedDualManifest)
if ($selectedInstallerUrl -cne
    $boostixInstallerUrl) {
    throw 'The updater did not prefer the validated Boostix URL over the schema-v1 bridge.'
}
$selectedSha256 = [string]$manifestType.GetField(
    'Sha256').GetValue($parsedDualManifest)
$selectedSize = [long]$manifestType.GetField(
    'Size').GetValue($parsedDualManifest)
if ($selectedSha256 -cne ('B' * 64) -or $selectedSize -ne 456L) {
    throw 'The updater mixed legacy and Boostix integrity metadata.'
}

$boostixTrustedPath =
    '/Boostix/main/dist/Boostix-Setup-' + $releaseVersion + '.exe'
$tamperedDualManifest = $dualManifestJson.Replace(
    $boostixTrustedPath,
    '/Boostix/main/dist/other.exe')
$parseManifestArguments[0] = [Text.Encoding]::UTF8.GetBytes($tamperedDualManifest)
try {
    [void]$parseManifestMethod.Invoke($null, $parseManifestArguments)
    throw 'A dual-field manifest with an untrusted Boostix URL was accepted.'
}
catch {
    $failure = $_.Exception
    while (($failure -is [Reflection.TargetInvocationException] -or
        $failure -is [Management.Automation.MethodInvocationException]) -and
        $failure.InnerException) {
        $failure = $failure.InnerException
    }
    if ($failure -isnot [IO.InvalidDataException]) {
        throw $failure
    }
}

$incompleteDualManifest = $dualManifestJson -replace
    '(?m)^\s*"boostixSize": 456\s*\r?\n',
    ''
$incompleteDualManifest = $incompleteDualManifest.Replace(
    '"boostixSha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",',
    '"boostixSha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"')
$parseManifestArguments[0] = [Text.Encoding]::UTF8.GetBytes(
    $incompleteDualManifest)
try {
    [void]$parseManifestMethod.Invoke($null, $parseManifestArguments)
    throw 'An incomplete Boostix integrity metadata triple was accepted.'
}
catch {
    $failure = $_.Exception
    while (($failure -is [Reflection.TargetInvocationException] -or
        $failure -is [Management.Automation.MethodInvocationException]) -and
        $failure.InnerException) {
        $failure = $failure.InnerException
    }
    if ($failure -isnot [IO.InvalidDataException]) {
        throw $failure
    }
}

$verificationStream = $null
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    Copy-Item -LiteralPath $InstallerPath -Destination $fixturePath

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($fixturePath)
    $fileVersion = [Version]::Parse($versionInfo.FileVersion)
    $semanticVersion = [Activator]::CreateInstance($semanticType)
    $semanticType.GetField('Major').SetValue($semanticVersion, $fileVersion.Major)
    $semanticType.GetField('Minor').SetValue($semanticVersion, $fileVersion.Minor)
    $semanticType.GetField('Patch').SetValue($semanticVersion, $fileVersion.Build)

    $manifest = [Activator]::CreateInstance($manifestType, $true)
    $manifestType.GetField('Version').SetValue($manifest, $semanticVersion)
    $manifestType.GetField('InstallerUrl').SetValue($manifest, 'https://example.invalid/setup.exe')
    $manifestType.GetField('Sha256').SetValue(
        $manifest,
        (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash)
    $manifestType.GetField('Size').SetValue($manifest, [long](Get-Item -LiteralPath $fixturePath).Length)

    $openArguments = New-Object 'object[]' 1
    $openArguments[0] = [string]$fixturePath
    $verificationStream = [IO.FileStream]$openMethod.Invoke($null, $openArguments)

    if ($verificationStream.CanWrite) {
        throw 'The verification handle is unexpectedly write-capable.'
    }

    $heldVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($fixturePath)
    if ($heldVersionInfo.ProductName -cne 'Boostix' -or
        [string]::IsNullOrWhiteSpace($heldVersionInfo.FileVersion)) {
        throw 'Windows version metadata is not readable while the verification handle is held.'
    }

    if ((Get-ChildWriterResult -Path $fixturePath) -cne 'BLOCKED') {
        throw 'A second writer could open the installer during final verification.'
    }

    $validateArguments = New-Object 'object[]' 3
    $validateArguments[0] = $manifest
    $validateArguments[1] = [string]$fixturePath
    $validateArguments[2] = $verificationStream
    try {
        [void]$validateMethod.Invoke($null, $validateArguments)
    }
    catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
}
finally {
    if ($verificationStream) {
        $verificationStream.Dispose()
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

foreach ($requiredText in @(
    'state == UpdateState.Retry && !demoMode',
    'RefreshAvailableUpdateAsync()',
    'ManifestFetchAttempts = 3',
    'ManifestRequestTimeoutMilliseconds = 12000',
    'ManifestTotalTimeoutMilliseconds = 45000',
    'BuildRepositoryHeadRequestAddress(',
    'ParseRepositoryHeadCommit(',
    'BuildImmutableManifestAddress(',
    'IsTransientManifestFailure(',
    'HttpRequestCacheLevel.NoCacheNoStore',
    'HttpRequestHeader.CacheControl',
    'update-operation.lock',
    'FileOptions.DeleteOnClose',
    'FindInstallerNetworkFailure(',
    'IsTransientInstallerDownloadFailure(',
    'SelectRepresentativeInstallerNetworkFailure(',
    'GetUpdateNetworkFailureMessage(',
    'EnsureSufficientUpdateDownloadSpace(update.Size)',
    'drive.AvailableFreeSpace < required',
    'var integrityFailures = new List<Exception>();',
    'throw new UpdateStorageException(',
    'catch (InvalidDataException ex)',
    'catch (WebException ex)'
)) {
    if (-not $source.Contains($requiredText)) {
        throw "Updater retry/resilience policy is missing: $requiredText"
    }
}

Write-Host 'Updater installer validation regression test passed.' -ForegroundColor Green
