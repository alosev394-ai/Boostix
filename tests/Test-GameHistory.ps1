[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# One game across all its sessions: what keeps happening, who keeps holding
# the memory, and that an anti-cheat is never offered for closing. The numbers
# come from seven real Counter-Strike 2 sessions on a 16 GB computer.

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

$reportType = $assembly.GetType('Boostix.BoostSessionReport', $true)
$hogType = $assembly.GetType('Boostix.BoostMemoryHog', $true)
$hogListType = [Collections.Generic.List``1].MakeGenericType($hogType)
$reportListType = [Collections.Generic.List``1].MakeGenericType($reportType)
$steamGame = 'D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe'

function New-HogList {
    param([object[]]$Hogs)
    $list = [Activator]::CreateInstance($hogListType)
    foreach ($hog in $Hogs) {
        $item = [Activator]::CreateInstance($hogType, $true)
        $hogType.GetField('Name').SetValue($item, [string]$hog[0])
        $hogType.GetField('ProcessName').SetValue($item, [string]$hog[1])
        $hogType.GetField('Bytes').SetValue($item, [long]$hog[2] * $mib)
        $list.Add($item)
    }
    return , $list
}

function New-Session {
    param(
        [int]$DaysAgo,
        [double]$LowestGb,
        [object[]]$Hogs = @(),
        [string]$Pressure = 'Critical',
        [string]$Crash = '',
        [bool]$Ended = $true,
        [string]$Path = $steamGame
    )
    $report = [Activator]::CreateInstance($reportType, $true)
    $started = [DateTime]::UtcNow.AddDays(-$DaysAgo)
    $reportType.GetField('SessionId', $instance).SetValue($report, [Guid]::NewGuid().ToString('N'))
    $reportType.GetField('StartedUtc', $instance).SetValue($report, $started)
    if ($Ended) {
        $reportType.GetField('EndedUtc', $instance).SetValue($report, [Nullable[DateTime]]$started.AddMinutes(90))
    }
    $reportType.GetField('TargetName', $instance).SetValue($report, 'cs2')
    $reportType.GetField('TargetExecutablePath', $instance).SetValue($report, $Path)
    $reportType.GetField('WorstResourcePressure', $instance).SetValue($report, $Pressure)
    $reportType.GetField('TargetCrashCode', $instance).SetValue($report, $Crash)
    $reportType.GetField('PhysicalMemoryTotalBytes', $instance).SetValue($report, [long](15.84 * $gib))
    $reportType.GetField('MinimumAvailableMemoryBytes', $instance).SetValue($report, [long]($LowestGb * $gib))
    if ($Hogs.Count -gt 0) {
        $reportType.GetField('MemoryHogs', $instance).SetValue($report, (New-HogList $Hogs))
    }
    return $report
}

function New-Sessions {
    param([object[]]$Reports)
    $list = [Activator]::CreateInstance($reportListType)
    foreach ($report in $Reports) {
        $list.Add($report)
    }
    return , $list
}

$summarize = $assembly.GetType('Boostix.GameHistory', $true).GetMethod('Summarize', $flags)
function Get-Summary {
    param($Sessions)
    return $summarize.Invoke($null, [object[]]@(, $Sessions))
}
function Get-Field {
    param($Object, [string]$Name)
    return $Object.GetType().GetField($Name, $instance).GetValue($Object)
}

# --- Seven real CS2 sessions, 5 to 7 November. ---
$helper = 'Steam Client WebHelper'
$real = New-Sessions @(
    (New-Session 1 0.42 @(@('Discord', 'Discord', 1614), @('FACEIT', 'FACEIT', 1285), @($helper, 'steamwebhelper', 1203))),
    (New-Session 2 0.01 @(@('Firefox', 'firefox', 2119), @('FACEIT', 'FACEIT', 1990), @('Claude', 'claude', 1782))),
    (New-Session 2 0.15 @(@('FACEIT', 'FACEIT', 1642), @('Discord', 'Discord', 1544), @($helper, 'steamwebhelper', 1137))),
    (New-Session 2 0.78 @(@($helper, 'steamwebhelper', 1589), @('Discord', 'Discord', 1325), @('Discord Clips', 'Discord Clips', 228))),
    (New-Session 2 1.00 @(@($helper, 'steamwebhelper', 1777), @('Discord', 'Discord', 1354), @('Wallpaper Engine', 'wallpaper64', 398))),
    (New-Session 3 1.20),
    (New-Session 3 0.43),
    (New-Session 0 0.2 @() 'Critical' '' $false)
)
$summary = Get-Summary $real

$sessions = @(Get-Field $summary 'Sessions')
if ($sessions.Count -ne 7) {
    throw "A game still running must not count: $($sessions.Count) sessions."
}
$newest = [DateTime](Get-Field $sessions[0] 'StartedUtc')
$oldest = [DateTime](Get-Field $sessions[6] 'StartedUtc')
if ($newest -le $oldest) {
    throw 'Sessions must come newest first.'
}
$stats = Get-Field $summary 'Stats'
if ([int](Get-Field $stats 'TightSessions') -ne 7 -or [int](Get-Field $stats 'Crashes') -ne 0) {
    throw 'The totals do not match the seven sessions.'
}
if ([int](Get-Field $summary 'ProgramSessions') -ne 5) {
    throw 'Only five sessions recorded who held the memory.'
}
if ([long](Get-Field $summary 'LowestFreeBytes') -ne [long](0.01 * $gib) -or
    [long](Get-Field $summary 'HighestLowestFreeBytes') -ne [long](1.2 * $gib)) {
    throw 'The range of the lowest free memory is wrong.'
}

$programs = @(Get-Field $summary 'Programs')
$names = @($programs | ForEach-Object { [string](Get-Field $_ 'Name') })
# Discord and Steam both came up four times; Discord held more in total.
if ($names[0] -cne 'Discord' -or $names[1] -cne $helper -or $names[2] -cne 'FACEIT') {
    throw "Programs are in the wrong order: $($names -join ', ')"
}
if ([int](Get-Field $programs[0] 'Sessions') -ne 4 -or
    [long](Get-Field $programs[0] 'LargestBytes') -ne 1614 * $mib) {
    throw 'Discord was seen in four sessions, at most 1614 MB.'
}
$needed = @{}
foreach ($program in $programs) {
    $needed[[string](Get-Field $program 'Name')] = [bool](Get-Field $program 'GameNeedsIt')
}
if ($needed['Discord'] -or -not $needed[$helper] -or -not $needed['FACEIT']) {
    throw 'Steam and FACEIT must stay open for a FACEIT game on Steam; Discord can close.'
}

$headline = Get-Field $summary 'Headline'
$title = [string](Get-Field $headline 'Title')
$detail = [string](Get-Field $headline 'Detail')
if ($title -cne (Convert-UiName '0J/QsNC80Y/RgtC4INC90LUg0YXQstCw0YLQsNC10YIg0L/QvtGH0YLQuCDQsiDQutCw0LbQtNC+0Lkg0LjQs9GA0LU=')) {
    throw "Seven tight games out of seven read as: $title"
}
if (-not $detail.Contains('Discord') -or $detail -notmatch '7' -or $detail -notmatch '1[.,]2') {
    throw "The headline detail misses the facts: $detail"
}

# Two crashes in three games come before memory.
$crashing = Get-Summary (New-Sessions @(
    (New-Session 1 0.4 @() 'Critical' '0xc0000005'),
    (New-Session 2 3.0 @() 'Normal' '0xc0000409'),
    (New-Session 3 3.0 @() 'Normal')))
$crashTitle = [string](Get-Field (Get-Field $crashing 'Headline') 'Title')
if ($crashTitle -cne (Convert-UiName '0JjQs9GA0LAg0YfQsNGB0YLQviDQstGL0LvQtdGC0LDQtdGC')) {
    throw "Two crashes in three games read as: $crashTitle"
}

# Some tight games, not most.
$sometimes = Get-Summary (New-Sessions @(
    (New-Session 1 0.4 @(, @('Discord', 'Discord', 1500))),
    (New-Session 2 4.0 @() 'Normal'),
    (New-Session 3 4.0 @() 'Normal')))
if ([string](Get-Field (Get-Field $sometimes 'Headline') 'Title') -cne
    (Convert-UiName '0J/QsNC80Y/RgtC4INC40L3QvtCz0LTQsCDQvdC1INGF0LLQsNGC0LDQtdGC')) {
    throw 'One tight game in three is not "sometimes".'
}

# A calm game says so and lists nobody.
$calm = Get-Summary (New-Sessions @(
    (New-Session 1 5.0 @() 'Normal'),
    (New-Session 2 4.0 @() 'Normal')))
if ([string](Get-Field (Get-Field $calm 'Headline') 'Title') -cne (Convert-UiName '0J/QsNC80Y/RgtC4INGF0LLQsNGC0LDQtdGC') -or
    @(Get-Field $calm 'Programs').Count -ne 0) {
    throw 'Two calm games must read as calm and list no programs.'
}
$empty = Get-Summary (New-Sessions @())
if (@(Get-Field $empty 'Sessions').Count -ne 0) {
    throw 'No sessions must give an empty history.'
}

# --- An anti-cheat is never offered for closing, whichever game it is. ---
$closable = $assembly.GetType('Boostix.SessionVerdicts', $true).GetMethod('BiggestClosable', $flags)
$faceitFirst = New-HogList @(@('FACEIT', 'FACEIT', 2000), @('Anti-cheat', 'faceitclient', 1500), @('Discord', 'Discord', 1000))
$offered = $closable.Invoke($null, [object[]]@($faceitFirst, 'C:\Games\Other\game.exe'))
if ([string]$hogType.GetField('Name').GetValue($offered) -cne 'Discord') {
    throw 'FACEIT was offered for closing.'
}
$format = $assembly.GetType('Boostix.SessionCloseList', $true).GetMethod('Format', $flags)
$saved = [string[]]@('FACEIT', 'Discord', 'faceitclient', 'vgtray')
$formatted = [string]$format.Invoke($null, [object[]]@($saved, 'cs2'))
if ($formatted -cne 'Discord') {
    throw "An anti-cheat would be closed for the game: $formatted"
}

# --- The history on screen. ---
# The history is longer than the window: rows below the fold are offscreen
# but still there, and Invoke works on them.
function Find-Element {
    param($Root, [string]$AutomationId, [int]$TimeoutSeconds = 12, [switch]$Visible)
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element -and -not ($Visible -and $element.Current.IsOffscreen)) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    return $null
}

function Wait-Gone {
    param($Root, [string]$AutomationId)
    $until = [DateTime]::UtcNow.AddSeconds(5)
    while ((Find-Element $Root $AutomationId 0) -and [DateTime]::UtcNow -lt $until) {
        Start-Sleep -Milliseconds 100
    }
    return -not (Find-Element $Root $AutomationId 0)
}

function Invoke-Element {
    param($Element)
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$process = $null
try {
    $process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList @(
        '--skip-setup', '--demo', '--demo-shelf', '--lang=en',
        ('--test-instance=' + [Guid]::NewGuid().ToString('N')))
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Boostix did not open its window.'
    }
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    # The stage opens the history of a game played more than once.
    $open = Find-Element $window 'Boostix.Games.OpenHistory'
    if (-not $open) {
        throw 'The stage offers no history for a game played several times.'
    }
    Invoke-Element $open
    foreach ($id in 'Boostix.Center.GameHistory.Headline', 'Boostix.Center.GameHistory.Chart',
        'Boostix.Center.GameHistory.Program.0', 'Boostix.Center.GameHistory.Session.0') {
        if (-not (Find-Element $window $id)) {
            throw "The game history has no $id."
        }
    }
    # Every program can be closed except the ones the game needs.
    $programRows = 0
    $closeButtons = 0
    for ($index = 0; $index -lt 8; $index++) {
        if (Find-Element $window "Boostix.Center.GameHistory.Program.$index" 0) { $programRows++ }
        if (Find-Element $window "Boostix.Center.GameHistory.Close.$index" 0) { $closeButtons++ }
    }
    if ($closeButtons -lt 1 -or $closeButtons -ge $programRows) {
        throw "$closeButtons of $programRows programs can be closed; FACEIT and Steam must not."
    }

    # A session opens its debrief over the history, and back returns to it.
    Invoke-Element (Find-Element $window 'Boostix.Center.GameHistory.Session.0')
    if (-not (Find-Element $window 'Boostix.Center.Debrief.Headline')) {
        throw 'A session in the history did not open its debrief.'
    }
    Invoke-Element (Find-Element $window 'Boostix.Center.Debrief.Back')
    if (-not (Wait-Gone $window 'Boostix.Center.Debrief.Headline') -or
        -not (Find-Element $window 'Boostix.Center.GameHistory.Headline')) {
        throw 'Closing the debrief did not return to the history.'
    }

    # The debrief links to the history too.
    Invoke-Element (Find-Element $window 'Boostix.Center.GameHistory.Back')
    if (-not (Wait-Gone $window 'Boostix.Center.GameHistory.Headline')) {
        throw 'The history did not close.'
    }
    Invoke-Element (Find-Element $window 'Boostix.Games.OpenDebrief')
    $link = Find-Element $window 'Boostix.Center.Debrief.History'
    if (-not $link) {
        throw 'The debrief does not link to the game history.'
    }
    Invoke-Element $link
    if (-not (Wait-Gone $window 'Boostix.Center.Debrief.Headline') -or
        -not (Find-Element $window 'Boostix.Center.GameHistory.Headline')) {
        throw 'The link in the debrief did not open the history.'
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

'Game history test passed: totals, programs, headlines, anti-cheat and the page.'
