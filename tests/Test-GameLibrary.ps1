[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# Installed games are found in Steam, Epic Games and GOG without starting
# them, and each one points at the executable that actually runs the game.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Description)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing game library contract: $Description"
    }
}

$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
$center = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'))
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
Assert-Contains $build '"$projectRoot\Boostix\GameLibraryScanner.cs"' 'scanner compiled into Boostix.exe'
Assert-Contains $program 'GameLibraryScanner.Scan()' 'scan runs from the app'
Assert-Contains $program 'Task.Run' 'scan stays off the UI thread'
Assert-Contains $center (Convert-UiName '0J3QsNC50LTQtdC90L4g0L3QsCDQutC+0LzQv9GM0Y7RgtC10YDQtQ==') 'found games section'
Assert-Contains $center '"Boostix.Center.FoundGame."' 'found games are reachable for screen readers and tests'

Add-Type -AssemblyName PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom($ApplicationPath)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$scanner = $assembly.GetType('Boostix.GameLibraryScanner', $true)
function Get-Method {
    param([string]$Name)
    $method = $scanner.GetMethod($Name, $flags)
    if (-not $method) {
        throw "GameLibraryScanner.$Name is missing."
    }
    return $method
}

# Steam library list: every "path", with escaped backslashes.
$vdf = @'
"libraryfolders"
{
	"0"
	{
		"path"		"C:\\Program Files (x86)\\Steam"
		"apps" { "228980" "1" }
	}
	"1"
	{
		"path"		"D:\\SteamLibrary"
	}
}
'@
$paths = @((Get-Method 'ParseLibraryFolders').Invoke($null, @($vdf)))
if ($paths.Count -ne 2 -or $paths[0] -cne 'C:\Program Files (x86)\Steam' -or $paths[1] -cne 'D:\SteamLibrary') {
    throw "Steam libraries were: $($paths -join ' | ')"
}

# Steam app manifest: display name and install folder.
$acf = @'
"AppState"
{
	"appid"		"1091500"
	"name"		"Cyberpunk 2077"
	"installdir"		"Cyberpunk 2077"
}
'@
$manifest = $scanner.GetMethod('ParseAppManifest', $flags)
$manifestArguments = New-Object 'object[]' 3
$manifestArguments[0] = $acf
[void]$manifest.Invoke($null, $manifestArguments)
if ($manifestArguments[1] -cne 'Cyberpunk 2077' -or $manifestArguments[2] -cne 'Cyberpunk 2077') {
    throw "Steam manifest read as $($manifestArguments[1]) / $($manifestArguments[2])"
}

# Epic manifest: games only, executable from the launch entry.
$epic = @'
{
	"FormatVersion": 0,
	"DisplayName": "Fortnite",
	"InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite",
	"LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
	"AppCategories": [ "public", "games", "applications" ]
}
'@
$epicMethod = $scanner.GetMethod('ParseEpicManifest', $flags)
$epicArguments = New-Object 'object[]' 3
$epicArguments[0] = $epic
$isGame = [bool]$epicMethod.Invoke($null, $epicArguments)
if (-not $isGame -or $epicArguments[1] -cne 'Fortnite' -or
    $epicArguments[2] -cne 'C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteLauncher.exe') {
    throw "Epic manifest read as $isGame / $($epicArguments[1]) / $($epicArguments[2])"
}
$plugin = $epic.Replace('"games", ', '')
$epicArguments = New-Object 'object[]' 3
$epicArguments[0] = $plugin
if ([bool]$epicMethod.Invoke($null, $epicArguments)) {
    throw 'An Epic manifest without the games category was treated as a game.'
}

# The executable that runs the game: Unreal's -Shipping build over its
# bootstrap, never uninstallers, crash handlers or redistributables.
$pick = $scanner.GetMethod('PickMainExecutable', $flags, $null, [type[]]@([string]), $null)
if (-not $pick) {
    throw 'GameLibraryScanner.PickMainExecutable(directory) is missing.'
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('boostix-library-' + [Guid]::NewGuid().ToString('N'))
function New-Fixture {
    param([string]$Relative, [int]$Bytes)
    $path = Join-Path $root $Relative
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path))
    [IO.File]::WriteAllBytes($path, (New-Object byte[] $Bytes))
}
try {
    New-Fixture 'unreal\Stray.exe' 1024
    New-Fixture 'unreal\unins000.exe' 90000
    New-Fixture 'unreal\Engine\Binaries\Win64\CrashReportClient.exe' 80000
    New-Fixture 'unreal\Hk_project\Binaries\Win64\Stray-Win64-Shipping.exe' 4096
    $picked = [string]$pick.Invoke($null, @([string](Join-Path $root 'unreal')))
    if ($picked -cne (Join-Path $root 'unreal\Hk_project\Binaries\Win64\Stray-Win64-Shipping.exe')) {
        throw "Unreal game picked $picked"
    }

    New-Fixture 'plain\Launcher.exe' 1024
    New-Fixture 'plain\bin\RealGame.exe' 6000
    New-Fixture 'plain\_CommonRedist\vcredist_x64.exe' 90000
    New-Fixture 'plain\UnityCrashHandler64.exe' 70000
    New-Fixture 'plain\setup.exe' 50000
    $picked = [string]$pick.Invoke($null, @([string](Join-Path $root 'plain')))
    if ($picked -cne (Join-Path $root 'plain\bin\RealGame.exe')) {
        throw "Plain game picked $picked"
    }

    # Source 2 games: the game binary, not Valve's developer console, and
    # the name that matches the game wins over a larger tool.
    New-Fixture 'source2\game\bin\win64\vconsole2.exe' 90000
    New-Fixture 'source2\game\bin\win64\dota2.exe' 3000
    New-Fixture 'source2\game\bin\win64\resourcecompiler.exe' 8000
    $hinted = $scanner.GetMethod('PickMainExecutable', $flags, $null, [type[]]@([string], [string]), $null)
    if (-not $hinted) {
        throw 'GameLibraryScanner.PickMainExecutable(directory, nameHint) is missing.'
    }
    $picked = [string]$hinted.Invoke($null, @([string](Join-Path $root 'source2'), 'Dota 2'))
    if ($picked -cne (Join-Path $root 'source2\game\bin\win64\dota2.exe')) {
        throw "Source 2 game picked $picked"
    }

    # Programs sold on Steam are not games.
    $nonGame = $scanner.GetMethod('IsKnownNonGame', $flags)
    foreach ($name in @('Wallpaper Engine', 'Action! - Gameplay Recording and Streaming', 'OBS Studio')) {
        if (-not [bool]$nonGame.Invoke($null, @($name))) {
            throw "$name was treated as a game."
        }
    }
    if ([bool]$nonGame.Invoke($null, @('ELDEN RING'))) {
        throw 'ELDEN RING was treated as a program.'
    }

    New-Fixture 'empty\readme.txt' 10
    $picked = $pick.Invoke($null, @([string](Join-Path $root 'empty')))
    if ($null -ne $picked) {
        throw "A folder without a game executable picked $picked"
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

# A real scan on this machine must finish and return well-formed entries.
$scan = Get-Method 'Scan'
$watch = [Diagnostics.Stopwatch]::StartNew()
$found = @($scan.Invoke($null, @()))
$watch.Stop()
if ($watch.Elapsed.TotalSeconds -gt 20) {
    throw "Scanning installed games took $($watch.Elapsed.TotalSeconds) s"
}
foreach ($game in $found) {
    if ([string]::IsNullOrWhiteSpace($game.Name) -or -not $game.ExecutablePath.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Malformed installed game entry: $($game.Name) $($game.ExecutablePath)"
    }
}

Write-Host "Game library test passed: Steam, Epic and executable choice verified; $($found.Count) installed games found here." -ForegroundColor Green
