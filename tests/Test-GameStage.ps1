[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# The game stage: colours taken from a game's art, per-game totals, switching
# games on the rail, the live session, the launch state and the full-screen
# debrief. The background update path is checked at the end.

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
$palette = $assembly.GetType('Boostix.ArtPalette', $true)
$pickAccent = $palette.GetMethod('PickAccent', $flags)
$readable = $palette.GetMethod('Readable', $flags)
$floorOf = $palette.GetMethod('Floor', $flags)
$contrast = $palette.GetMethod('Contrast', $flags)
$isDarkLogo = $palette.GetMethod('IsDarkLogo', $flags)

function New-Picture {
    param([byte]$Red, [byte]$Green, [byte]$Blue, [byte]$Alpha = 255, [int]$Pixels = 400)
    $bytes = New-Object byte[] ($Pixels * 4)
    for ($index = 0; $index -lt $Pixels; $index++) {
        $bytes[$index * 4] = $Blue
        $bytes[$index * 4 + 1] = $Green
        $bytes[$index * 4 + 2] = $Red
        $bytes[$index * 4 + 3] = $Alpha
    }
    return , $bytes
}

function Get-Accent {
    param([byte[]]$Picture, [Windows.Media.Color]$Fallback)
    $arguments = New-Object 'object[]' 2
    $arguments[0] = $Picture
    $arguments[1] = $Fallback
    return $pickAccent.Invoke($null, $arguments)
}

function Test-DarkLogo {
    param([byte[]]$Picture)
    $arguments = New-Object 'object[]' 1
    $arguments[0] = $Picture
    return [bool]$isDarkLogo.Invoke($null, $arguments)
}

$fallback = [Windows.Media.Color]::FromRgb(1, 2, 3)
$darkInk = [Windows.Media.Color]::FromRgb(8, 17, 29)
$white = [Windows.Media.Colors]::White

# --- Colours from art. ---
$orange = Get-Accent (New-Picture 230 120 20) $fallback
if ($orange -eq $fallback -or $orange.R -le $orange.B) {
    throw "An orange picture gave $orange."
}
$blue = Get-Accent (New-Picture 20 60 220) $fallback
if ($blue -eq $fallback -or $blue.B -le $blue.R) {
    throw "A blue picture gave $blue."
}
foreach ($dull in @(@(120, 120, 120), @(12, 12, 14), @(240, 240, 238))) {
    $result = Get-Accent (New-Picture $dull[0] $dull[1] $dull[2]) $fallback
    if ($result -ne $fallback) {
        throw "A picture without a vivid colour ($($dull -join ',')) must keep the theme accent, got $result."
    }
}
if ((Get-Accent ([byte[]]@()) $fallback) -ne $fallback) {
    throw 'An empty picture must keep the theme accent.'
}
# Whatever the art, dark text stays readable on the accent and white text on
# the floor.
foreach ($sample in @(@(10, 10, 160), @(120, 0, 0), @(0, 90, 30), @(90, 0, 120), @(250, 250, 40), @(0, 200, 255))) {
    $color = [Windows.Media.Color]::FromRgb($sample[0], $sample[1], $sample[2])
    $accent = $readable.Invoke($null, [object[]]@([Windows.Media.Color]$color))
    $inkContrast = [double]$contrast.Invoke($null, [object[]]@([Windows.Media.Color]$accent, [Windows.Media.Color]$darkInk))
    if ($inkContrast -lt 5.4) {
        throw "Dark text on the accent $accent has contrast $inkContrast."
    }
    $floor = $floorOf.Invoke($null, [object[]]@([Windows.Media.Color]$accent))
    $floorContrast = [double]$contrast.Invoke($null, [object[]]@([Windows.Media.Color]$floor, [Windows.Media.Color]$white))
    if ($floorContrast -lt 12) {
        throw "White text on the floor $floor has contrast $floorContrast."
    }
}

# A dark logo is painted white; a light one and the transparent part do not count.
if (-not (Test-DarkLogo (New-Picture 10 20 60))) {
    throw 'A navy logo was not recognised as dark.'
}
if (Test-DarkLogo (New-Picture 240 240 240)) {
    throw 'A white logo was recognised as dark.'
}
if (Test-DarkLogo (New-Picture 0 0 0 0)) {
    throw 'A fully transparent image was recognised as dark.'
}

# --- Per-game totals. ---
$reportType = $assembly.GetType('Boostix.BoostSessionReport', $true)
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
function New-Report {
    param([int]$Minutes, [string]$Crash, [string]$Pressure, [bool]$Ended = $true)
    $report = [Activator]::CreateInstance($reportType, $true)
    $started = [DateTime]::UtcNow.AddHours(-3)
    $reportType.GetField('StartedUtc', $instance).SetValue($report, $started)
    if ($Ended) {
        $reportType.GetField('EndedUtc', $instance).SetValue($report, [Nullable[DateTime]]$started.AddMinutes($Minutes))
    }
    $reportType.GetField('TargetCrashCode', $instance).SetValue($report, $Crash)
    $reportType.GetField('WorstResourcePressure', $instance).SetValue($report, $Pressure)
    return $report
}
$listType = [Collections.Generic.List``1].MakeGenericType($reportType)
$reports = [Activator]::CreateInstance($listType)
$reports.Add((New-Report 30 '' 'Normal'))
$reports.Add((New-Report 45 '0xc0000005' 'Critical'))
$reports.Add((New-Report 15 '' 'Critical'))
$reports.Add((New-Report 99 '' 'Normal' $false))
$stats = $assembly.GetType('Boostix.GameStats', $true).GetMethod('For', $flags).Invoke($null, @(, $reports))
$statsType = $stats.GetType()
if ([int]$statsType.GetField('Sessions').GetValue($stats) -ne 3) {
    throw 'An unfinished session must not count as a game.'
}
if ([TimeSpan]$statsType.GetField('Total').GetValue($stats) -ne [TimeSpan]::FromMinutes(90)) {
    throw 'Total play time is wrong.'
}
if ([int]$statsType.GetField('Crashes').GetValue($stats) -ne 1 -or
    [int]$statsType.GetField('TightSessions').GetValue($stats) -ne 2) {
    throw 'Crash or low-memory counts are wrong.'
}

# --- What ran out, and who held the memory. ---
function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

$gib = 1073741824L
$describe = $assembly.GetType('Boostix.SessionVerdicts', $true).GetMethod('Describe', $flags)
function New-MemoryReport {
    param([long]$Lowest, [long]$GpuPeak, [long]$CommitLeft = (20 * $gib))
    $report = New-Report 60 '' 'Critical'
    $reportType.GetField('SessionId', $instance).SetValue($report, 'd3a0ff0000000000000000000000000a')
    $reportType.GetField('MinimumAvailableMemoryBytes', $instance).SetValue($report, $Lowest)
    $reportType.GetField('PhysicalMemoryTotalBytes', $instance).SetValue($report, 16 * $gib)
    $reportType.GetField('MinimumCommitHeadroomBytes', $instance).SetValue($report, $CommitLeft)
    $reportType.GetField('CommitLimitBytes', $instance).SetValue($report, 48 * $gib)
    $reportType.GetField('GpuDedicatedTotalBytes', $instance).SetValue($report, 8 * $gib)
    $reportType.GetField('PeakGpuDedicatedUsageBytes', $instance).SetValue($report, $GpuPeak)
    return $report
}
function Get-Verdicts {
    param($Report)
    return @($describe.Invoke($null, [object[]]@($Report)))
}
function Get-VerdictText {
    param($Verdict, [string]$Field)
    return [string]$Verdict.GetType().GetField($Field).GetValue($Verdict)
}

$memoryTitle = Convert-UiName '0J/QsNC80Y/RgtC4INC/0L7Rh9GC0Lgg0L3QtSDQvtGB0YLQsNC70L7RgdGM'
$videoTitle = Convert-UiName '0JLQuNC00LXQvtC/0LDQvNGP0YLRjCDQv9C+0YfRgtC4INC30LDQutC+0L3Rh9C40LvQsNGB0Yw='
# Critical because of the graphics card alone: there was plenty of ordinary
# memory, and the debrief must not say otherwise.
$videoOnly = Get-Verdicts (New-MemoryReport (6 * $gib) ([long](7.9 * $gib)))
if ((Get-VerdictText $videoOnly[0] 'Title') -cne $videoTitle) {
    throw "Full video memory alone was reported as '$(Get-VerdictText $videoOnly[0] 'Title')'."
}
$both = Get-Verdicts (New-MemoryReport ([long](0.4 * $gib)) ([long](7.9 * $gib)))
if ((Get-VerdictText $both[0] 'Title') -cne $memoryTitle) {
    throw 'Low memory must lead when both ran out.'
}
$oldReport = New-MemoryReport (6 * $gib) ([long](7.9 * $gib))
$reportType.GetField('PhysicalMemoryTotalBytes', $instance).SetValue($oldReport, 0L)
if ((Get-VerdictText (Get-Verdicts $oldReport)[0] 'Title') -cne $memoryTitle) {
    throw 'A report without the memory size cannot blame the graphics card.'
}

# The ranking groups a program's processes and leaves out the game, Windows
# and anything small.
$pairType = [Collections.Generic.KeyValuePair[string, long]]
$pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,long]]'
foreach ($sample in @(
    @('firefox', 1000), @('firefox', 1200), @('Firefox', 800), @('discord', 600),
    @('cs2', 8000), @('dwm', 500), @('svchost', 900), @('Memory Compression', 3000),
    @('tiny', 50), @('spotify', 300), @('steamwebhelper', 250), @('Boostix', 400))) {
    $pairs.Add((New-Object $pairType.FullName ([string]$sample[0], ([long]$sample[1] * 1048576L))))
}
$rank = $assembly.GetType('Boostix.MemoryHogRanking', $true).GetMethod('Rank', $flags)
$rankArguments = New-Object 'object[]' 2
$rankArguments[0] = $pairs.PSObject.BaseObject
$rankArguments[1] = 'cs2'
$hogs = @($rank.Invoke($null, $rankArguments))
$hogType = $assembly.GetType('Boostix.BoostMemoryHog', $true)
$hogNames = @($hogs | ForEach-Object { [string]$hogType.GetField('Name').GetValue($_) })
if (($hogNames -join ',') -cne 'firefox,discord,spotify') {
    throw "Memory ranking gave: $($hogNames -join ',')."
}
if ([long]$hogType.GetField('Bytes').GetValue($hogs[0]) -ne 3000L * 1048576L) {
    throw 'A program must count all of its processes.'
}

# The debrief names them, and they survive saving and reading the report.
$hogReport = New-MemoryReport ([long](0.4 * $gib)) (4 * $gib)
$hogList = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($hogType))
foreach ($named in @(@('Firefox', 3000), @('Discord', 600))) {
    $hog = [Activator]::CreateInstance($hogType, $true)
    $hogType.GetField('Name').SetValue($hog, [string]$named[0])
    $hogType.GetField('Bytes').SetValue($hog, [long]$named[1] * 1048576L)
    $hogList.Add($hog)
}
$reportType.GetField('MemoryHogs', $instance).SetValue($hogReport, $hogList)
$hogVerdicts = Get-Verdicts $hogReport
if (-not (Get-VerdictText $hogVerdicts[0] 'Detail').Contains('Firefox')) {
    throw 'The debrief does not say who held the memory.'
}
if (-not (Get-VerdictText $hogVerdicts[2] 'Title').Contains('Firefox')) {
    throw 'The advice does not name the program to close.'
}
$store = $assembly.GetType('Boostix.BoostSessionReportStore', $true)
$saved = [string]$store.GetMethod('Serialize', $flags).Invoke($null, [object[]]@($hogReport))
$readArguments = New-Object 'object[]' 1
$readArguments[0] = [string[]]($saved -split "`r?`n")
$readBack = $store.GetMethod('Deserialize', $flags).Invoke($null, $readArguments)
$savedHogs = $reportType.GetField('MemoryHogs', $instance).GetValue($readBack)
if (-not $savedHogs -or $savedHogs.Count -ne 2 -or
    [string]$hogType.GetField('Name').GetValue($savedHogs[0]) -cne 'Firefox') {
    throw 'The memory hogs did not survive saving the report.'
}

# The page file is only worth a word when the commit limit is actually close.
$window = $assembly.GetType('Boostix.BoostWindow', $true)
$pressureText = $window.GetMethod('DescribeLongMemoryPressure', $flags)
$pageFileWord = Convert-UiName '0L/QvtC00LrQsNGH0LrQuA=='
if (([string]$pressureText.Invoke($null, [object[]]@([long](18 * $gib), [long](48 * $gib)))).Contains($pageFileWord)) {
    throw 'With 18 GB of commit room the advice must not mention the page file.'
}
if (-not ([string]$pressureText.Invoke($null, [object[]]@([long](1 * $gib), [long](24 * $gib)))).Contains($pageFileWord)) {
    throw 'Near the commit limit the advice must mention the page file.'
}

# One name for one switch: the hints say Auto, so the switch does too.
$stageSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\GameStage.cs'))
if (-not $stageSource.Contains((Convert-UiName 'TC5UKCLQkNCy0YLQviBCb29zdCIp'))) {
    throw 'The Auto switch on the stage has another name than the hints.'
}

# --- The stage on screen. ---
function Find-Element {
    param($Root, [string]$AutomationId, [int]$TimeoutSeconds = 12)
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element -and -not $element.Current.IsOffscreen) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    return $null
}

function Start-Stage {
    param([string[]]$Arguments)
    $process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList (@(
        '--skip-setup', '--demo', '--demo-shelf', '--lang=en',
        ('--test-instance=' + [Guid]::NewGuid().ToString('N'))) + $Arguments)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Boostix did not open its window.'
    }
    return $process
}

function Stop-Stage {
    param($Process)
    if ($Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Element {
    param($Element)
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Assert-Title {
    param($Window, [string]$Expected)
    $until = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $title = Find-Element $Window 'Boostix.Games.Title' 2
        if ($title -and $title.Current.Name -ceq $Expected) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "The stage did not switch to $Expected."
}

$process = $null
try {
    $process = Start-Stage @()
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Assert-Title $window 'Counter-Strike 2'
    foreach ($id in 'Boostix.Games.Play', 'Boostix.Games.Auto', 'Boostix.Games.Card.0', 'Boostix.Games.Card.3', 'Boostix.Games.Manual', 'Boostix.Games.OpenDebrief') {
        if (-not (Find-Element $window $id)) {
            throw "The stage has no $id."
        }
    }
    if (Find-Element $window 'Boostix.Center.Tab.Home' 1) {
        throw 'The manual Boost page must not have a tab of its own.'
    }

    Invoke-Element (Find-Element $window 'Boostix.Games.Card.1')
    Assert-Title $window 'Dota 2'
    $cardStatus = (Find-Element $window 'Boostix.Games.Card.1').Current.ItemStatus
    if (-not $cardStatus) {
        throw 'The selected cover does not say so.'
    }
    if ((Find-Element $window 'Boostix.Games.Card.0').Current.ItemStatus) {
        throw 'Only one cover may be selected.'
    }

    # A game without Steam art offers a picture of its own.
    Invoke-Element (Find-Element $window 'Boostix.Games.Card.2')
    Assert-Title $window 'Cyberpunk 2077'
    if (-not (Find-Element $window 'Boostix.Games.Picture')) {
        throw 'A game without Steam art offers no picture of its own.'
    }

    # The debrief covers the stage and Escape brings the stage back.
    Invoke-Element (Find-Element $window 'Boostix.Games.OpenDebrief')
    if (-not (Find-Element $window 'Boostix.Center.Debrief.Headline')) {
        throw 'The debrief did not open.'
    }
    $back = Find-Element $window 'Boostix.Center.Debrief.Back'
    if (-not $back) {
        throw 'The debrief has no way back.'
    }
    Invoke-Element $back
    $until = [DateTime]::UtcNow.AddSeconds(5)
    while ((Find-Element $window 'Boostix.Center.Debrief.Headline' 0) -and [DateTime]::UtcNow -lt $until) {
        Start-Sleep -Milliseconds 100
    }
    if (Find-Element $window 'Boostix.Center.Debrief.Headline' 0) {
        throw 'The debrief did not close.'
    }

    # The journal lists the games and opens the same debrief.
    (Find-Element $window 'Boostix.Center.Tab.Report').GetCurrentPattern(
        [Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $row = Find-Element $window 'Boostix.Center.History.0'
    if (-not $row) {
        throw 'The journal shows no games.'
    }
    Invoke-Element $row
    if (-not (Find-Element $window 'Boostix.Center.Debrief.Headline')) {
        throw 'A journal row did not open its debrief.'
    }
}
finally {
    Stop-Stage $process
}

# The live session replaces the shelf with the running game.
$process = $null
try {
    $process = Start-Stage @('--demo-live')
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $timer = Find-Element $window 'Boostix.Games.Live.Timer'
    if (-not $timer) {
        throw 'The live session did not open.'
    }
    foreach ($id in 'Boostix.Games.Live.Stop', 'Boostix.Games.Live.Memory', 'Boostix.Games.Live.Cpu', 'Boostix.Games.Live.Gpu') {
        if (-not (Find-Element $window $id)) {
            throw "The live session has no $id."
        }
    }
    if ($timer.Current.Name -notmatch '\d\d:\d\d') {
        throw "The live timer shows '$($timer.Current.Name)'."
    }
    if (Find-Element $window 'Boostix.Games.Play' 1) {
        throw 'Play must not be offered while the game runs.'
    }
}
finally {
    Stop-Stage $process
}

# A real Boost run: the stage goes live, Stop ends it and the shelf returns.
$process = $null
try {
    $process = Start-Stage @('--demo-target')
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $activate = Find-Element $window 'Boostix.Activate'
    if (-not $activate) {
        throw 'The manual Boost page did not open for the demo target.'
    }
    $until = [DateTime]::UtcNow.AddSeconds(10)
    while (-not $activate.Current.IsEnabled -and [DateTime]::UtcNow -lt $until) {
        Start-Sleep -Milliseconds 100
    }
    Invoke-Element $activate
    $games = Find-Element $window 'Boostix.Center.Tab.Games'
    $games.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if (-not (Find-Element $window 'Boostix.Games.Live.Timer')) {
        throw 'A running Boost did not take over the stage.'
    }
    $stop = Find-Element $window 'Boostix.Games.Live.Stop'
    if (-not $stop) {
        throw 'The live stage cannot stop Boost.'
    }
    Invoke-Element $stop
    # The shelf comes back under the fading timer and the tree can miss an
    # element for a moment, so one empty look does not count.
    $until = [DateTime]::UtcNow.AddSeconds(10)
    $ended = $false
    while (-not $ended -and [DateTime]::UtcNow -lt $until) {
        if (-not (Find-Element $window 'Boostix.Games.Live.Timer' 0)) {
            Start-Sleep -Milliseconds 600
            $ended = -not (Find-Element $window 'Boostix.Games.Live.Timer' 0)
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ended) {
        throw 'Stop did not end the live stage.'
    }
    if (-not (Find-Element $window 'Boostix.Games.Play')) {
        throw 'The shelf did not come back after Stop.'
    }
}
finally {
    Stop-Stage $process
}

# Play shows the launch until the game appears.
$process = $null
try {
    $process = Start-Stage @('--demo-launch')
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Invoke-Element (Find-Element $window 'Boostix.Games.Play')
    if (-not (Find-Element $window 'Boostix.Games.Launching')) {
        throw 'Play did not show the launch.'
    }
}
finally {
    Stop-Stage $process
}

# --- A copy in the tray keeps working and still hears about updates. ---
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs')).Replace("`r`n", "`n")
$update = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\UpdateFlow.cs'))
foreach ($required in @(
    'StartBackgroundUpdateChecks();',
    'await updateOverlay.IsNewerVersionAvailableAsync()',
    'await updateOverlay.RecheckForUpdatesAsync();'
)) {
    if (-not $program.Contains($required)) {
        throw "The background update contract is missing: $required"
    }
}
# Started with Windows nobody is at the window: the update screen must not stop
# Auto discovery behind it.
if ($program -notmatch 'if \(startInTray\)\s*\{[^}]*updateCheckPending = true;\s*\}\s*else\s*\{\s*canContinue = await updateOverlay\.CheckForUpdatesAsync\(\);') {
    throw 'A copy started with Windows waits for the update screen.'
}
if ($program -notmatch 'if \(boostActive \|\| animationRunning \|\| updateNoticeShown\)') {
    throw 'The background check must not run during a game.'
}
foreach ($required in @('public Task<bool> RecheckForUpdatesAsync()', 'public async Task<bool> IsNewerVersionAvailableAsync()')) {
    if (-not $update.Contains($required)) {
        throw "The update flow is missing: $required"
    }
}

'Game stage test passed: art colours, totals, rail, live session, launch, debrief and background updates.'
