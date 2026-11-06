[CmdletBinding()]
param(
    # Retained so existing build/test callers do not need to change.
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $projectRoot 'outputs\Boost-Session.ps1'
$testRoot = Join-Path $env:TEMP ('MajesticBoost-OneShot-Test-' + [Guid]::NewGuid().ToString('N'))

try {
    [void](New-Item -ItemType Directory -Path $testRoot)

    $scriptSource = [IO.File]::ReadAllText($scriptPath)
    foreach ($requiredText in @(
        '[switch]$CloseDiscord',
        '[switch]$CloseEpic',
        '[switch]$CloseSteam',
        '[switch]$CloseOneDrive',
        '[switch]$CloseTeams',
        '[switch]$CloseWallpaper',
        '[switch]$CloseNvidiaOverlay',
        '[string]$ReadySignalPath',
        '[string]$ResultPath',
        'Status=Completed'
    )) {
        if (-not $scriptSource.Contains($requiredText)) {
            throw "One-shot Active Boost contract is missing: $requiredText"
        }
    }

    foreach ($forbiddenPattern in @(
        '(?i)\bChatGPT\b',
        '(?i)\bPriorityClass\b',
        '(?i)\bProcessPriorityClass\b',
        '(?i)\bSet-BoostGamePriority\b',
        '(?i)\bHigh\b',
        '(?i)\bRealTime\b',
        '(?i)\bActiveSessionPath\b',
        '(?i)\bStart-Sleep\b'
    )) {
        if ($scriptSource -match $forbiddenPattern) {
            throw "The one-shot preparation script contains forbidden behavior: $forbiddenPattern"
        }
    }

    $defaultRequestedNames = New-Object Collections.Generic.List[string]
    $defaultReadySignalPath = Join-Path $testRoot 'default\ready.flag'
    $defaultResultPath = Join-Path $testRoot 'default\result.ini'
    & {
        param($ProductionScript, $FixtureRoot, $ReadyPath, $ReportPath, $RequestedNames)

        $env:LOCALAPPDATA = $FixtureRoot

        function Get-Process {
            [CmdletBinding()]
            param([string[]]$Name)
            foreach ($processName in $Name) {
                $RequestedNames.Add($processName)
            }
            return @()
        }

        function Get-CimInstance {
            [CmdletBinding()]
            param([string]$ClassName, [string]$Filter)
            throw 'NVIDIA discovery must not run unless CloseNvidiaOverlay is selected.'
        }

        function Start-Process {
            [CmdletBinding()]
            param(
                [string]$FilePath,
                [object[]]$ArgumentList,
                [string]$WindowStyle
            )
            throw "Unexpected process launch during fixture: $FilePath"
        }

        & $ProductionScript `
            -ReadySignalPath $ReadyPath `
            -ResultPath $ReportPath
    } $scriptPath (Join-Path $testRoot 'default') $defaultReadySignalPath $defaultResultPath $defaultRequestedNames

    if (-not (Test-Path -LiteralPath $defaultReadySignalPath)) {
        throw 'The one-shot preparation script did not publish its readiness signal.'
    }
    if (-not (Test-Path -LiteralPath $defaultResultPath)) {
        throw 'The one-shot preparation script did not publish its structured result.'
    }
    $defaultResult = [IO.File]::ReadAllText($defaultResultPath)
    foreach ($requiredResultText in @('[BoostSession]', 'FormatVersion=1', 'Status=Completed')) {
        if (-not $defaultResult.Contains($requiredResultText)) {
            throw "The structured result is missing: $requiredResultText"
        }
    }

    if ($defaultRequestedNames.Count -ne 0) {
        throw (
            'The safe default preparation must not inspect or close third-party ' +
            'processes: ' + [string]::Join(', ', $defaultRequestedNames))
    }

    foreach ($optInProcess in @(
        'Discord',
        'EpicGamesLauncher',
        'steam',
        'OneDrive',
        'ms-teams',
        'Teams',
        'wallpaper32',
        'wallpaper64',
        'NVIDIA Overlay',
        'nvsphelper64'
    )) {
        if ($defaultRequestedNames.Contains($optInProcess)) {
            throw "An opt-in process is closed by default: $optInProcess"
        }
    }

    $optInRequestedNames = New-Object Collections.Generic.List[string]
    $cimDiscoveryCount = New-Object Collections.Generic.List[int]
    $cimDiscoveryCount.Add(0)
    $optInReadySignalPath = Join-Path $testRoot 'opt-in\ready.flag'
    $optInResultPath = Join-Path $testRoot 'opt-in\result.ini'
    & {
        param($ProductionScript, $FixtureRoot, $ReadyPath, $ReportPath, $RequestedNames, $CimCounter)

        $env:LOCALAPPDATA = $FixtureRoot

        function Get-Process {
            [CmdletBinding()]
            param([string[]]$Name)
            foreach ($processName in $Name) {
                $RequestedNames.Add($processName)
            }
            return @()
        }

        function Get-CimInstance {
            [CmdletBinding()]
            param([string]$ClassName, [string]$Filter)
            $CimCounter[0] = $CimCounter[0] + 1
            throw 'Opt-in process selection must not trigger undeclared CIM discovery.'
        }

        function Start-Process {
            [CmdletBinding()]
            param(
                [string]$FilePath,
                [object[]]$ArgumentList,
                [string]$WindowStyle
            )
            throw "Unexpected process launch during fixture: $FilePath"
        }

        & $ProductionScript `
            -CloseDiscord `
            -CloseEpic `
            -CloseSteam `
            -CloseOneDrive `
            -CloseTeams `
            -CloseWallpaper `
            -CloseNvidiaOverlay `
            -CloseProcessNames 'firefox, bad;name,Spotify' `
            -ReadySignalPath $ReadyPath `
            -ResultPath $ReportPath
    } $scriptPath (Join-Path $testRoot 'opt-in') $optInReadySignalPath $optInResultPath $optInRequestedNames $cimDiscoveryCount

    foreach ($requiredOptInProcess in @(
        'Discord',
        'EpicGamesLauncher',
        'EpicWebHelper',
        'EpicOnlineServicesUserHelper',
        'steam',
        'steamwebhelper',
        'GameOverlayUI',
        'OneDrive',
        'ms-teams',
        'Teams',
        'wallpaper32',
        'wallpaper64',
        'NVIDIA Overlay',
        'nvsphelper64',
        'firefox',
        'Spotify'
    )) {
        if (-not $optInRequestedNames.Contains($requiredOptInProcess)) {
            throw "An opt-in process group is missing: $requiredOptInProcess"
        }
    }
    if ($optInRequestedNames.Contains('bad;name')) {
        throw 'A malformed process name reached Get-Process.'
    }
    if ($cimDiscoveryCount[0] -ne 0) {
        throw 'Opt-in process selection triggered unexpected CIM discovery.'
    }

    # A program leaves as one: helpers go with the main window, it counts once
    # and its path is kept so it can come back after the game. Nothing that
    # ignores the request is ever forced.
    $groupReadySignalPath = Join-Path $testRoot 'group\ready.flag'
    $groupResultPath = Join-Path $testRoot 'group\result.ini'
    & {
        param($ProductionScript, $FixtureRoot, $ReadyPath, $ReportPath)

        $env:LOCALAPPDATA = $FixtureRoot

        function New-FakeProcess {
            param([string]$Name, [int]$Id, [bool]$Windowed, [bool]$Leaves, [hashtable]$State)
            $handle = if ($Windowed) { [IntPtr]1 } else { [IntPtr]::Zero }
            $fake = [pscustomobject]@{
                Name = $Name
                Id = $Id
                Path = "C:\Fake\$Name.exe"
                MainWindowHandle = $handle
                Leaves = $Leaves
                State = $State
            }
            $fake | Add-Member -MemberType ScriptProperty -Name HasExited -Value { $this.Leaves -and $this.State.Closed }
            $fake | Add-Member -MemberType ScriptMethod -Name CloseMainWindow -Value { $this.State.Closed = $true; return $true }
            $fake | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($Milliseconds) return $this.HasExited }
            $fake | Add-Member -MemberType ScriptMethod -Name Kill -Value { throw 'A program was forced to exit.' }
            return $fake
        }

        $leaving = @{ Closed = $false }
        $staying = @{ Closed = $false }
        $fakes = @{
            fakeapp = @(
                (New-FakeProcess 'fakeapp' 102 $false $true $leaving),
                (New-FakeProcess 'fakeapp' 101 $true $true $leaving))
            stubborn = @(, (New-FakeProcess 'stubborn' 201 $true $false $staying))
        }

        function Get-Process {
            [CmdletBinding()]
            param([string[]]$Name)
            foreach ($processName in $Name) {
                if ($fakes.ContainsKey($processName)) { $fakes[$processName] }
            }
        }

        & $ProductionScript `
            -CloseProcessNames 'fakeapp,stubborn' `
            -ReadySignalPath $ReadyPath `
            -ResultPath $ReportPath
    } $scriptPath (Join-Path $testRoot 'group') $groupReadySignalPath $groupResultPath

    $groupResult = [IO.File]::ReadAllText($groupResultPath)
    if (-not $groupResult.Contains('Process1=fakeapp|101|C:\Fake\fakeapp.exe')) {
        throw "A closed program was not reported with its path: $groupResult"
    }
    if ($groupResult.Contains('Process2=')) {
        throw 'A program with helpers was reported more than once.'
    }
    if (-not $groupResult.Contains('WarningCount=1') -or -not $groupResult.Contains('stubborn')) {
        throw 'A program that stayed open was not reported.'
    }

    'Active Boost one-shot preparation regression test passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
