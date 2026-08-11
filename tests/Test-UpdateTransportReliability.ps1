[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public sealed class BoostixOneShotHttpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Thread worker;
    private readonly string response;

    public BoostixOneShotHttpServer(string status, string headers, string body)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = "http://127.0.0.1:" + port + "/artifact";
        response = "HTTP/1.1 " + status + "\r\n" +
            headers + "Connection: close\r\n\r\n" + body;
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
                byte[] bytes = Encoding.ASCII.GetBytes(response);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
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

public sealed class BoostixTrackingWebResponse : WebResponse
{
    public bool WasClosed { get; private set; }

    public override void Close()
    {
        WasClosed = true;
        base.Close();
    }
}

public static class BoostixWebFailureFixture
{
    public static WebException Create(WebResponse response)
    {
        return new WebException(
            "fixture network failure",
            null,
            WebExceptionStatus.ReceiveFailure,
            response);
    }
}
'@

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandSource = Join-Path $projectRoot 'ProductBrand.cs'
$tokensSource = Join-Path $projectRoot 'Boostix\DesignTokens.cs'
$updateSource = Join-Path $projectRoot 'Boostix\UpdateFlow.cs'
$installerSource = Join-Path $projectRoot 'BoostixInstaller\Program.cs'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$wpfRoot = Join-Path $frameworkRoot 'WPF'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-UpdateTransport-' + [Guid]::NewGuid().ToString('N'))
$updateHarness = Join-Path $temporaryRoot 'UpdateTransportHarness.dll'
$installerHarness = Join-Path $temporaryRoot 'InstallerTransportHarness.dll'

function Get-DeepestException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while (($current -is [Reflection.TargetInvocationException] -or
        $current -is [Management.Automation.MethodInvocationException]) -and
        $current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

function Invoke-Static {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Arguments
    )

    try {
        return $Method.Invoke($null, $Arguments)
    }
    catch {
        throw (Get-DeepestException -Exception $_.Exception)
    }
}

function Invoke-ResponseValidation {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][string]$Headers,
        [Parameter(Mandatory = $true)][string]$Body,
        [Parameter(Mandatory = $true)][long]$ExpectedBytes
    )

    $server = New-Object BoostixOneShotHttpServer -ArgumentList @(
        $Status,
        $Headers,
        $Body)
    $response = $null
    try {
        $request = [Net.HttpWebRequest][Net.WebRequest]::Create($server.Address)
        $request.AllowAutoRedirect = $false
        $request.Timeout = 5000
        $response = [Net.HttpWebResponse]$request.GetResponse()
        return Invoke-Static `
            -Method $Method `
            -Arguments ([object[]]@(
                $response,
                [long]1024,
                [long]$ExpectedBytes,
                [string]$server.Address))
    }
    finally {
        if ($response) {
            $response.Dispose()
        }
        $server.Dispose()
    }
}

function Assert-ValidationRejects {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][string]$Headers,
        [Parameter(Mandatory = $true)][string]$Body,
        [Parameter(Mandatory = $true)][long]$ExpectedBytes,
        [Parameter(Mandatory = $true)][Type]$ExpectedFailure,
        [Parameter(Mandatory = $true)][string]$Scenario
    )

    try {
        [void](Invoke-ResponseValidation `
            -Method $Method `
            -Status $Status `
            -Headers $Headers `
            -Body $Body `
            -ExpectedBytes $ExpectedBytes)
        throw "Updater accepted $Scenario."
    }
    catch {
        $failure = Get-DeepestException -Exception $_.Exception
        if (-not $ExpectedFailure.IsAssignableFrom($failure.GetType())) {
            throw $failure
        }
    }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "C# compiler was not found: $compiler"
    }

    $updateCompilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$updateHarness" `
        /reference:System.dll `
        /reference:System.Core.dll `
        "/reference:$frameworkRoot\System.Xaml.dll" `
        "/reference:$wpfRoot\WindowsBase.dll" `
        "/reference:$wpfRoot\PresentationCore.dll" `
        "/reference:$wpfRoot\PresentationFramework.dll" `
        $brandSource `
        $tokensSource `
        $updateSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Updater transport harness did not compile:`r`n$($updateCompilerOutput -join [Environment]::NewLine)"
    }

    $installerCompilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$installerHarness" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Security.dll `
        $brandSource `
        $installerSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Installer reliability harness did not compile:`r`n$($installerCompilerOutput -join [Environment]::NewLine)"
    }

    $assembly = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($updateHarness))
    $overlayType = $assembly.GetType(
        'Boostix.UpdateFlowOverlay',
        $true,
        $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $nestedFlags = [Reflection.BindingFlags]::NonPublic
    $validateResponse = $overlayType.GetMethod('ValidateResponse', $flags)
    $calculateRemaining = $overlayType.GetMethod(
        'CalculateRemainingInstallerDownloadMilliseconds',
        $flags)
    $captureFailure = $overlayType.GetMethod(
        'CaptureInstallerNetworkFailure',
        $flags)
    $transientFailure = $overlayType.GetMethod(
        'IsTransientInstallerDownloadFailure',
        $flags)
    $networkExceptionType = $overlayType.GetNestedType(
        'InstallerNetworkException',
        $nestedFlags)
    if (-not $validateResponse -or -not $calculateRemaining -or
        -not $captureFailure -or -not $transientFailure -or
        -not $networkExceptionType) {
        throw 'Compiled updater reliability helpers were not found.'
    }

    [void](Invoke-ResponseValidation `
        -Method $validateResponse `
        -Status '200 OK' `
        -Headers "Content-Length: 4`r`n" `
        -Body 'data' `
        -ExpectedBytes 4)
    Assert-ValidationRejects `
        -Method $validateResponse `
        -Status '200 OK' `
        -Headers "Content-Length: 4`r`nContent-Range: bytes 0-3/8`r`n" `
        -Body 'data' `
        -ExpectedBytes 4 `
        -ExpectedFailure ([IO.InvalidDataException]) `
        -Scenario 'a response carrying Content-Range'
    Assert-ValidationRejects `
        -Method $validateResponse `
        -Status '206 Partial Content' `
        -Headers "Content-Length: 4`r`nContent-Range: bytes 0-3/8`r`n" `
        -Body 'data' `
        -ExpectedBytes 4 `
        -ExpectedFailure ([IO.InvalidDataException]) `
        -Scenario 'HTTP 206 partial content'
    Assert-ValidationRejects `
        -Method $validateResponse `
        -Status '200 OK' `
        -Headers "Content-Length: 4`r`n" `
        -Body 'data' `
        -ExpectedBytes 5 `
        -ExpectedFailure ([IO.InvalidDataException]) `
        -Scenario 'a mismatched Content-Length'

    $remainingArguments = New-Object 'object[]' 1
    $remainingArguments[0] = [long]599999
    if ([int](Invoke-Static `
            -Method $calculateRemaining `
            -Arguments $remainingArguments) -ne 1) {
        throw 'The installer download deadline lost its final bounded millisecond.'
    }
    $remainingArguments[0] = [long]600000
    try {
        [void](Invoke-Static `
            -Method $calculateRemaining `
            -Arguments $remainingArguments)
        throw 'The installer download accepted an exhausted total deadline.'
    }
    catch [Net.WebException] {
        if ($_.Exception.Status -ne [Net.WebExceptionStatus]::Timeout) {
            throw
        }
    }

    $trackingResponse = New-Object BoostixTrackingWebResponse
    $webFailure = [BoostixWebFailureFixture]::Create($trackingResponse)
    [void](Invoke-Static `
        -Method $captureFailure `
        -Arguments ([object[]]@('captured failure', $webFailure)))
    if (-not $trackingResponse.WasClosed) {
        throw 'A failed HTTP response was retained instead of being closed.'
    }

    $networkConstructor = @(
        $networkExceptionType.GetConstructors(
            [Reflection.BindingFlags]::Public -bor
            [Reflection.BindingFlags]::NonPublic -bor
            [Reflection.BindingFlags]::Instance)
    )[0]
    $serverFailure = $networkConstructor.Invoke([object[]]@(
        'service unavailable',
        $null,
        [Net.WebExceptionStatus]::ProtocolError,
        503))
    if (-not [bool](Invoke-Static `
            -Method $transientFailure `
            -Arguments ([object[]]@($serverFailure)))) {
        throw 'A captured HTTP 503 response no longer receives a bounded retry.'
    }
    $proxyFailure = $networkConstructor.Invoke([object[]]@(
        'proxy authentication required',
        $null,
        [Net.WebExceptionStatus]::ProtocolError,
        407))
    if ([bool](Invoke-Static `
            -Method $transientFailure `
            -Arguments ([object[]]@($proxyFailure)))) {
        throw 'HTTP 407 proxy authentication was incorrectly retried as transient.'
    }

    $installerText = [IO.File]::ReadAllText($installerSource)
    $normalizedInstaller = [regex]::Replace($installerText, '\s+', ' ')
    foreach ($contract in @(
        'bool legacyRollbackEligible = !boostixRollbackEligible && legacyInstalled && IsInstalledExecutableRollbackEligible( LegacyInstalledExe, true);',
        'bool rollbackEligible = boostixRollbackEligible || legacyRollbackEligible;',
        'TryLegacyCleanup(delegate { DeleteIfExists(Path.Combine(InstallDirectory, "Game-Boost.ps1")); });',
        'foreach (string candidate in new[] { InstalledExe, LegacyInstalledExe })'
    )) {
        if (-not $normalizedInstaller.Contains($contract)) {
            throw "Installer compatibility reliability contract is missing: $contract"
        }
    }

    Write-Output 'Update transport and legacy-install reliability tests passed.'
}
finally {
    if ((Test-Path -LiteralPath $temporaryRoot -PathType Container) -and
        (Split-Path -Leaf $temporaryRoot) -like 'Boostix-UpdateTransport-*') {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
