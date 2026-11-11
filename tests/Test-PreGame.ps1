[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# Before the game: when a game is known to need more memory than is free,
# Play first shows who holds it. Programs the game needs are never offered
# for closing, programs without a window cannot be closed politely, and
# Wallpaper Engine is stopped through its own command line instead.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This UI regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$assembly = [Reflection.Assembly]::LoadFrom($ApplicationPath)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$gib = 1073741824L
$mib = 1048576L

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}
function Get-Field {
    param($Object, [string]$Name)
    return $Object.GetType().GetField($Name, $instance).GetValue($Object)
}

$check = $assembly.GetType('Boostix.PreGameCheck', $true)

# --- When to stop before the game. ---
$isShort = $check.GetMethod('IsShort', $flags)
if (-not [bool]$isShort.Invoke($null, [object[]]@([long](7.6 * $gib), [long](6.1 * $gib)))) {
    throw 'CS2 needs 7.6 GB with 6.1 GB free: that is short.'
}
if ([bool]$isShort.Invoke($null, [object[]]@([long](5 * $gib), [long](6.1 * $gib)))) {
    throw 'A game that fits must start at once.'
}
if ([bool]$isShort.Invoke($null, [object[]]@([long]0, [long](1 * $gib)))) {
    throw 'A game never played has nothing to compare with.'
}

# --- Who may be closed. Numbers from the real CS2 sessions. ---
$hogType = $assembly.GetType('Boostix.BoostMemoryHog', $true)
$hogList = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($hogType))
foreach ($hog in @(
        @('Firefox', 'firefox', 2119),
        @('FACEIT', 'FACEIT', 2051),
        @('Discord', 'Discord', 1614),
        @('Steam Client WebHelper', 'steamwebhelper', 1203),
        @('Wallpaper Engine', 'wallpaper64', 398))) {
    $item = [Activator]::CreateInstance($hogType, $true)
    $hogType.GetField('Name').SetValue($item, [string]$hog[0])
    $hogType.GetField('ProcessName').SetValue($item, [string]$hog[1])
    $hogType.GetField('Bytes').SetValue($item, [long]$hog[2] * $mib)
    $hogList.Add($item)
}
$steamGame = 'D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe'
$chosen = [string[]]@('Firefox')
$windowed = [Func[string, bool]] { param($name) $name -in @('firefox', 'Discord', 'FACEIT', 'steamwebhelper') }
$programs = @($check.GetMethod('Classify', $flags).Invoke($null, [object[]]@($hogList, $steamGame, $chosen, $windowed)))
$kinds = @{}
foreach ($program in $programs) {
    $kinds[[string](Get-Field $program 'ProcessName')] = [string](Get-Field $program 'Kind')
}
$expected = @{
    'firefox' = 'ClosesForGame'
    'FACEIT' = 'GameNeedsIt'
    'Discord' = 'Closable'
    'steamwebhelper' = 'GameNeedsIt'
    'wallpaper64' = 'NoWindow'
}
foreach ($name in $expected.Keys) {
    if ($kinds[$name] -cne $expected[$name]) {
        throw "$name is $($kinds[$name]), expected $($expected[$name])."
    }
}
if ([string](Get-Field $programs[0] 'ProcessName') -cne 'firefox') {
    throw 'Programs must come biggest first.'
}

# --- What the screen says. ---
$planType = $assembly.GetType('Boostix.PreGamePlan', $true)
function New-Plan {
    param([long]$Typical, [long]$Available)
    $plan = [Activator]::CreateInstance($planType, $true)
    $planType.GetField('GameName', $instance).SetValue($plan, 'Counter-Strike 2')
    $planType.GetField('TypicalBytes', $instance).SetValue($plan, $Typical)
    $planType.GetField('AvailableBytes', $instance).SetValue($plan, $Available)
    $planType.GetField('TotalBytes', $instance).SetValue($plan, [long](15.84 * $gib))
    return $plan
}
$describe = $check.GetMethod('Describe', $flags)
$short = $describe.Invoke($null, [object[]]@((New-Plan ([long](7.6 * $gib)) ([long](6.1 * $gib)))))
if ([string](Get-Field $short 'Title') -cne (Convert-UiName '0J/QsNC80Y/RgtC4INC80L7QttC10YIg0L3QtSDRhdCy0LDRgtC40YLRjA==')) {
    throw "A short plan reads: $([string](Get-Field $short 'Title'))"
}
$detail = [string](Get-Field $short 'Detail')
if (-not $detail.Contains('Counter-Strike 2') -or $detail -notmatch '7[.,]6' -or $detail -notmatch '6[.,]1') {
    throw "The detail misses the numbers: $detail"
}
$enough = $describe.Invoke($null, [object[]]@((New-Plan ([long](7.6 * $gib)) ([long](8 * $gib)))))
if ([string](Get-Field $enough 'Title') -cne (Convert-UiName '0J/QsNC80Y/RgtC4INGF0LLQsNGC0LjRgg==')) {
    throw "A plan that fits reads: $([string](Get-Field $enough 'Title'))"
}

# --- Wallpaper Engine: its own command line, nothing else. ---
$wallpaper = $assembly.GetType('Boostix.WallpaperEngine', $true)
$build = $wallpaper.GetMethod('BuildControl', $flags)
$enginePath = 'C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe'
$stop = $build.Invoke($null, [object[]]@($enginePath, 'stop'))
if (-not $stop -or $stop.FileName -cne $enginePath -or $stop.Arguments -cne '-control stop' -or $stop.UseShellExecute) {
    throw 'Wallpaper Engine is not stopped with "-control stop".'
}
foreach ($bad in @(@('C:\Windows\System32\cmd.exe', 'stop'), @($enginePath, 'quit'), @('wallpaper64.exe', 'play'))) {
    if ($build.Invoke($null, [object[]]$bad)) {
        throw "A wallpaper command was built for $($bad -join ' ')."
    }
}

# A stopped wallpaper is played again even if Boostix falls during the game.
$pendingType = $assembly.GetType('Boostix.PendingAfterGame', $true)
$pending = [Activator]::CreateInstance($pendingType, $true)
$pendingType.GetField('GameProcessName').SetValue($pending, 'cs2')
$pendingType.GetField('WallpaperPath').SetValue($pending, $enginePath)
if ([bool]$pendingType.GetProperty('IsEmpty', $instance).GetValue($pending)) {
    throw 'A stopped wallpaper alone must keep the after-game file.'
}
$text = [string]$pendingType.GetMethod('Format', $instance).Invoke($pending, @())
$read = $pendingType.GetMethod('Parse', $flags).Invoke($null, @($text))
if ([string]$pendingType.GetField('WallpaperPath').GetValue($read) -cne $enginePath) {
    throw 'The stopped wallpaper did not survive the after-game file.'
}
$forged = $pendingType.GetMethod('Parse', $flags).Invoke($null, @("Game=cs2`nWallpaper=C:\Windows\System32\cmd.exe`n"))
if ([string]$pendingType.GetField('WallpaperPath').GetValue($forged)) {
    throw 'The after-game file accepted another program as the wallpaper.'
}
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
if ($program.Contains('" -CloseWallpaper"')) {
    throw 'The preparation script is still asked to close Wallpaper Engine.'
}

# --- The screen. ---
function Get-FreshRoot {
    param($Root)
    try {
        $handle = $Root.Current.NativeWindowHandle
        if ($handle -ne 0) {
            return [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$handle)
        }
    }
    catch { }
    return $Root
}
function Find-Element {
    param($Root, [string]$AutomationId, [int]$TimeoutSeconds = 12)
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = (Get-FreshRoot $Root).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    return $null
}
function Invoke-Element {
    param($Element)
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$process = $null
try {
    $process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList @(
        '--skip-setup', '--demo', '--demo-shelf', '--lang=en',
        ('--test-instance=' + [Guid]::NewGuid().ToString('N')), '--demo-pregame')
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Boostix did not open its window.'
    }
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    foreach ($id in 'Boostix.Center.PreGame.Headline', 'Boostix.Center.PreGame.Meter',
        'Boostix.Center.PreGame.Program.0', 'Boostix.Center.PreGame.Play', 'Boostix.Center.PreGame.Back') {
        if (-not (Find-Element $window $id)) {
            throw "Before the game has no $id."
        }
    }
    $headline = (Find-Element $window 'Boostix.Center.PreGame.Headline').Current.Name
    $rows = 0
    $close = $null
    for ($index = 0; $index -lt 8; $index++) {
        if (Find-Element $window "Boostix.Center.PreGame.Program.$index" 0) { $rows++ }
        if (-not $close) { $close = Find-Element $window "Boostix.Center.PreGame.Close.$index" 0 }
    }
    if (-not $close -or $rows -lt 3) {
        throw "Before the game lists $rows programs and offers no close button."
    }

    # Closing the biggest closable program frees its memory and the headline
    # follows.
    Invoke-Element $close
    $until = [DateTime]::UtcNow.AddSeconds(6)
    $changed = $false
    while (-not $changed -and [DateTime]::UtcNow -lt $until) {
        Start-Sleep -Milliseconds 200
        $now = Find-Element $window 'Boostix.Center.PreGame.Headline' 1
        $changed = $now -and $now.Current.Name -cne $headline
    }
    if (-not $changed) {
        throw 'Closing a program did not change what the screen says.'
    }

    Invoke-Element (Find-Element $window 'Boostix.Center.PreGame.Back')
    $until = [DateTime]::UtcNow.AddSeconds(5)
    while ((Find-Element $window 'Boostix.Center.PreGame.Headline' 0) -and [DateTime]::UtcNow -lt $until) {
        Start-Sleep -Milliseconds 100
    }
    if (Find-Element $window 'Boostix.Center.PreGame.Headline' 0) {
        throw 'Back did not close the screen.'
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

'Before-the-game test passed: when to stop, who may close, wallpaper control and the screen.'
