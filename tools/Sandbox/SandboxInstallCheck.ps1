[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerName,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [string]$InputDirectory = 'C:\BoostixInput',

    [string]$ResultsDirectory = 'C:\BoostixResults',

    [switch]$ShutDownWhenDone
)

# Runs inside Windows Sandbox: a Windows that has never seen Boostix.
# Installs silently, starts the app, uninstalls, and writes result.json and
# screenshots to the folder the host mapped in. Nothing here runs on the host.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$steps = New-Object 'System.Collections.Generic.List[object]'
$logPath = Join-Path $ResultsDirectory 'sandbox.log'

function Write-Log {
    param([string]$Message)
    $line = (Get-Date).ToString('HH:mm:ss') + '  ' + $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Add-Step {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $steps.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    Write-Log (($(if ($Passed) { 'PASS' } else { 'FAIL' })) + '  ' + $Name + ': ' + $Detail)
}

function Save-Screenshot {
    param([string]$Name)
    try {
        Add-Type -AssemblyName System.Drawing
        Add-Type -AssemblyName System.Windows.Forms
        $bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object Drawing.Bitmap $bounds.Width, $bounds.Height
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size)
            $bitmap.Save((Join-Path $ResultsDirectory ($Name + '.png')), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
    catch {
        Write-Log ('Screenshot ' + $Name + ' failed: ' + $_.Exception.Message)
    }
}

function Wait-ForMainWindow {
    param([Diagnostics.Process]$Process, [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            return $false
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $true
        }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

[void](New-Item -ItemType Directory -Path $ResultsDirectory -Force)
Write-Log ('Windows ' + [Environment]::OSVersion.Version + ', UI ' +
    [Globalization.CultureInfo]::CurrentUICulture.Name + ', formats ' +
    [Globalization.CultureInfo]::CurrentCulture.Name)

$installDirectory = Join-Path ${env:ProgramFiles} 'Boostix'
$installedExe = Join-Path $installDirectory 'Boostix.exe'
$uninstallerExe = Join-Path $installDirectory 'Uninstall.exe'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Boostix'
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Boostix\Boostix.lnk'

try {
    $netRelease = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release
    Add-Step '.NET Framework 4.8' ($netRelease -ge 528040) ('release key ' + $netRelease)

    $installer = Join-Path $InputDirectory $InstallerName
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    Add-Step 'Installer signature' $true ([string]$signature.Status + ' ' +
        $(if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(unsigned)' }))

    # Copy out of the read-only mapping so Windows treats it like a download.
    $localInstaller = Join-Path $env:TEMP $InstallerName
    Copy-Item -LiteralPath $installer -Destination $localInstaller -Force
    $install = Start-Process -FilePath $localInstaller -ArgumentList '/quiet' -PassThru -Wait
    Add-Step 'Silent install' ($install.ExitCode -eq 0) ('exit code ' + $install.ExitCode)

    $version = if (Test-Path -LiteralPath $installedExe) {
        (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
    } else { '(missing)' }
    Add-Step 'Installed version' ($version -eq $ExpectedVersion) $version
    Add-Step 'Uninstaller present' (Test-Path -LiteralPath $uninstallerExe) $uninstallerExe
    Add-Step 'Start menu shortcut' (Test-Path -LiteralPath $startMenuShortcut) $startMenuShortcut
    $registered = if (Test-Path $uninstallKey) {
        [string](Get-ItemProperty $uninstallKey).DisplayVersion
    } else { '(no key)' }
    Add-Step 'Apps and features entry' ($registered -eq $ExpectedVersion) $registered

    # First start on a clean Windows: the window must appear and stay up.
    $app = Start-Process -FilePath $installedExe -PassThru
    $shown = Wait-ForMainWindow -Process $app -Seconds 45
    Start-Sleep -Seconds 4
    Save-Screenshot 'first-start'
    $app.Refresh()
    Add-Step 'First start' ($shown -and -not $app.HasExited) ('window shown: ' + $shown)
    if (-not $app.HasExited) {
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(10000)) {
            $app.Kill()
        }
    }

    $english = Start-Process -FilePath $installedExe -ArgumentList '--lang=en' -PassThru
    $shown = Wait-ForMainWindow -Process $english -Seconds 45
    Start-Sleep -Seconds 4
    Save-Screenshot 'english-start'
    Add-Step 'English start' $shown ('window shown: ' + $shown)
    if (-not $english.HasExited) {
        [void]$english.CloseMainWindow()
        if (-not $english.WaitForExit(10000)) {
            $english.Kill()
        }
    }

    $uninstall = Start-Process -FilePath $uninstallerExe -ArgumentList '/uninstall', '/quiet' -PassThru -Wait
    Start-Sleep -Seconds 3
    Add-Step 'Silent uninstall' ($uninstall.ExitCode -eq 0) ('exit code ' + $uninstall.ExitCode)
    Add-Step 'Program files removed' (-not (Test-Path -LiteralPath $installedExe)) $installDirectory
    Add-Step 'Apps and features entry removed' (-not (Test-Path $uninstallKey)) $uninstallKey
    Add-Step 'Start menu shortcut removed' (-not (Test-Path -LiteralPath $startMenuShortcut)) $startMenuShortcut
}
catch {
    Add-Step 'Unexpected error' $false $_.Exception.Message
}
finally {
    $failed = @($steps | Where-Object { -not $_.passed })
    $result = [pscustomobject]@{
        version = $ExpectedVersion
        passed = ($steps.Count -gt 0 -and $failed.Count -eq 0)
        steps = $steps
    }
    # Written last: the host waits for this file.
    $json = $result | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        (Join-Path $ResultsDirectory 'result.json'),
        $json,
        (New-Object Text.UTF8Encoding($false)))
    Write-Log ('Done, passed: ' + $result.passed)
    if ($ShutDownWhenDone) {
        & shutdown.exe /s /t 5
    }
}
