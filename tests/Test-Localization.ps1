[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# English interface: every L.T text has an English translation, the language
# is fixed before any text is read, and an English launch shows no Russian.

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
        throw "Missing localization contract: $Description"
    }
}

function Convert-CSharpLiteral {
    param([string]$Literal)
    return [regex]::Replace($Literal, '\\(.)', {
        param($match)
        switch -CaseSensitive ($match.Groups[1].Value) {
            'r' { return "`r" }
            'n' { return "`n" }
            't' { return "`t" }
            default { return $match.Groups[1].Value }
        }
    })
}

$cyrillic = [regex]'[\u0400-\u04FF]'
$placeholder = [regex]'\{\d+[^}]*\}'

# --- Every L.T text in the app and the installer has English. ---
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Boostix') -Filter '*.cs') +
    @(Get-Item -LiteralPath (Join-Path $projectRoot 'BoostixInstaller\Program.cs'))
$keys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($file in $sourceFiles) {
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($text, 'L\.T\("((?:[^"\\]|\\.)*)"\)')) {
        [void]$keys.Add((Convert-CSharpLiteral $match.Groups[1].Value))
    }
    # A static text cached before Main sets the language would stay Russian.
    if ([regex]::IsMatch($text, 'static\s+readonly\s+string\s+\w+\s*=\s*L\.T\(')) {
        throw "$($file.Name) caches a translated text in a static field."
    }
}
if ($keys.Count -lt 800) {
    throw "Only $($keys.Count) L.T texts were found; the source scan is broken."
}

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
$assembly = [Reflection.Assembly]::LoadFrom($ApplicationPath)
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$localization = $assembly.GetType('Boostix.L', $true)
$table = $localization.GetField('EnglishText', $flags).GetValue($null)

$problems = New-Object 'System.Collections.Generic.List[string]'
foreach ($key in $keys) {
    $english = $null
    if (-not $table.TryGetValue($key, [ref]$english)) {
        $problems.Add("no English for: $key")
        continue
    }
    if ($cyrillic.IsMatch($english)) {
        $problems.Add("Russian left in: $english")
    }
    $keyHoles = @($placeholder.Matches($key) | ForEach-Object { $_.Value } | Sort-Object)
    $englishHoles = @($placeholder.Matches($english) | ForEach-Object { $_.Value } | Sort-Object)
    if (($keyHoles -join '|') -cne ($englishHoles -join '|')) {
        $problems.Add("placeholders differ in: $english")
    }
}
if ($problems.Count -gt 0) {
    throw ("Localization table problems:`n" + ($problems -join "`n"))
}

# --- The switch itself. ---
$setLanguage = $localization.GetMethod('SetLanguage', $flags)
$translate = $localization.GetMethod('T', $flags)
$fromArguments = $localization.GetMethod('FromArguments', $flags)
$homeText = Convert-UiName '0JjQs9GA0Ys='
try {
    [void]$setLanguage.Invoke($null, @('en'))
    if ([string]$translate.Invoke($null, @($homeText)) -cne 'Games') {
        throw 'English did not translate the Games section.'
    }
    if ([string]$translate.Invoke($null, @('Boostix 9.9')) -cne 'Boostix 9.9') {
        throw 'A text without a translation must pass through unchanged.'
    }
    [void]$setLanguage.Invoke($null, @('ru'))
    if ([string]$translate.Invoke($null, @($homeText)) -cne $homeText) {
        throw 'Russian must show the source text.'
    }
}
finally {
    [void]$setLanguage.Invoke($null, @('ru'))
}
$requested = [string]$fromArguments.Invoke($null, @(, [string[]]@('--demo', '--lang=EN')))
if ($requested -cne 'en') {
    throw "--lang=EN was read as '$requested'."
}
if ($null -ne $fromArguments.Invoke($null, @(, [string[]]@('--lang=de')))) {
    throw 'An unsupported language must be ignored.'
}
# Many Russian speakers run English Windows with Russian regional formats.
$systemDefault = $localization.GetMethod('SystemDefault', $flags, $null, [Type[]]@([string], [string]), $null)
foreach ($case in @(
        @('en', 'ru', 'ru'), @('ru', 'en', 'ru'), @('uk', 'uk', 'ru'),
        @('en', 'en', 'en'), @('de', 'de', 'en'))) {
    $actual = [string]$systemDefault.Invoke($null, @($case[0], $case[1]))
    if ($actual -cne $case[2]) {
        throw "Display $($case[0]) with $($case[1]) formats chose $actual, expected $($case[2])."
    }
}
$choose = $assembly.GetType('Boostix.Program', $true).GetMethod('ChooseStartupLanguage', $flags)
if ([string]$choose.Invoke($null, @(, [string[]]@('--demo'))) -cne 'ru') {
    throw 'Test launches must stay Russian.'
}
if ([string]$choose.Invoke($null, @(, [string[]]@('--demo', '--lang=en'))) -cne 'en') {
    throw '--lang must win over every other source.'
}

# --- Start-up, restart and update wiring. ---
# A Windows checkout (core.autocrlf, as on CI) has CRLF; the multi-line
# contracts below are written with `n.
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs')).Replace("`r`n", "`n")
$update = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\UpdateFlow.cs'))
$installer = [IO.File]::ReadAllText((Join-Path $projectRoot 'BoostixInstaller\Program.cs'))
$overlay = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'))
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
$setAt = $program.IndexOf('L.SetLanguage(ChooseStartupLanguage(args));', [StringComparison]::Ordinal)
$applicationAt = $program.IndexOf('new Application()', [StringComparison]::Ordinal)
if ($setAt -lt 0 -or $applicationAt -lt 0 -or $setAt -gt $applicationAt) {
    throw 'The language must be chosen before the WPF application is created.'
}
# Types are initialised when the JIT compiles the method that uses them.
Assert-Contains $program "L.SetLanguage(ChooseStartupLanguage(args));`n            Run(args);" 'app body compiled after the language is set'
Assert-Contains $program "[MethodImpl(MethodImplOptions.NoInlining)]`n        private static void Run(string[] args)" 'app body never inlined into Main'
Assert-Contains $program '"Language=" + (centerSettings.Language ?? L.Language)' 'language choice saved'
Assert-Contains $program 'HasArgument(args, RestartArgument) ? RestartMutexWaitMilliseconds : 0' 'restarted instance waits for the old one'
Assert-Contains $program 'languageRestartPending = false;' 'cancelled close cancels the restart'
Assert-Contains $update '"/updateui --lang=" + L.Language' 'update installer follows the app language'
Assert-Contains $installer 'L.SetLanguage(L.FromArguments(args) ?? L.SystemDefault());' 'installer picks its language first'
Assert-Contains $overlay '"Boostix.Center.Language." + language' 'language choice in Settings'
Assert-Contains $build '"$projectRoot\Boostix\Localization.cs"' 'localization compiled'
Assert-Contains $build '"$projectRoot\Boostix\LocalizationEnglish.cs"' 'English table compiled'
Assert-Contains $build 'CHANGELOG.en.md,Boostix.Changelog.en.md' 'English What''s new embedded'

$whatsNew = $assembly.GetType('Boostix.WhatsNewContent', $true)
$brand = $assembly.GetType('Boostix.Branding.ProductBrand', $true)
$version = [string]$brand.GetField('ProductVersion', $flags).GetValue($null)
try {
    [void]$setLanguage.Invoke($null, @('en'))
    $highlights = @($whatsNew.GetMethod('LoadHighlights', $flags, $null, [Type[]]@([string]), $null).Invoke($null, @($version)))
}
finally {
    [void]$setLanguage.Invoke($null, @('ru'))
}
# A patch can carry a single fix; what matters is that the English text is found.
if ($highlights.Count -lt 1) {
    throw "English What's new for $version has $($highlights.Count) items."
}
foreach ($item in $highlights) {
    if ($cyrillic.IsMatch([string]$item)) {
        throw "English What's new shows Russian: $item"
    }
}

# --- Session reports follow the interface language. ---
# A fragment of a Russian message stops matching once both sides are
# translated, so runtime texts are never searched for a translated piece.
foreach ($file in $sourceFiles) {
    if ([regex]::IsMatch([IO.File]::ReadAllText($file.FullName), '\.(IndexOf|Contains|StartsWith|EndsWith)\(\s*L\.T\(')) {
        throw "$($file.Name) searches a runtime text for a translated fragment."
    }
}
# Page-file advice comes from SessionGuard in English; the report shows it in
# the interface language.
$describePagefile = $assembly.GetType('Boostix.BoostWindow', $true).GetMethod('DescribePagefileRecommendation', $flags)
if (-not $describePagefile) {
    throw 'Page-file advice has no translated description for the session report.'
}
$pagefileCodes = [Enum]::GetValues($assembly.GetType('Boostix.PagefileRecommendationCode', $true))
foreach ($language in 'ru', 'en') {
    try {
        [void]$setLanguage.Invoke($null, @($language))
        foreach ($code in $pagefileCodes) {
            $advice = [string]$describePagefile.Invoke($null, @($code))
            if (-not $advice -or $cyrillic.IsMatch($advice) -ne ($language -eq 'ru')) {
                throw "Page-file advice for $code is not in '$language': $advice"
            }
        }
    }
    finally {
        [void]$setLanguage.Invoke($null, @('ru'))
    }
}
if ($program -notmatch 'RecordSessionAction\(\s*L\.T\("[^"]+"\),\s*DescribePagefileRecommendation\(assessment\.Recommendation\)') {
    throw 'The page-file session action does not use the translated advice.'
}
# GameTargetService explains a lost target in English for the diagnostics log.
if ($program.Contains('NotifySelectedTargetEnded(generation, targetError);')) {
    throw 'The session report shows the internal English target error.'
}

# --- An English launch shows English. ---
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$allowedRussian = @(Convert-UiName '0KDRg9GB0YHQutC40Lk=')

function Wait-ForElement {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$ProcessId,
        [int]$TimeoutMilliseconds = 20000
    )
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($element in $Root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($element.Current.ProcessId -eq $ProcessId -and -not $element.Current.IsOffscreen) {
                return $element
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Element did not appear: $AutomationId"
}

function Get-RussianNames {
    param([Windows.Automation.AutomationElement]$Window)
    $found = New-Object 'System.Collections.Generic.List[string]'
    $all = $Window.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        $name = $element.Current.Name
        if ($name -and $cyrillic.IsMatch($name) -and $allowedRussian -notcontains $name) {
            $found.Add($name)
        }
    }
    return , $found
}

$process = $null
try {
    $testInstance = '--test-instance=' + [Guid]::NewGuid().ToString('N')
    $process = Start-Process `
        -FilePath $ApplicationPath `
        -ArgumentList '--skip-setup', '--demo', '--demo-center', '--demo-shelf', '--lang=en', $testInstance `
        -PassThru
    $root = [Windows.Automation.AutomationElement]::RootElement
    $readiness = Wait-ForElement $root 'Boostix.Center.Tab.Readiness' $process.Id
    if ($readiness.Current.Name -ne 'PC') {
        throw "The PC tab was named '$($readiness.Current.Name)'."
    }
    $process.Refresh()
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    $settingsTab = Wait-ForElement $window 'Boostix.Center.Tab.Settings' $process.Id
    $settingsTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $englishOption = Wait-ForElement $window 'Boostix.Center.Language.en' $process.Id
    if ($englishOption.Current.Name -cne 'English, selected') {
        throw "The English option was named '$($englishOption.Current.Name)'."
    }
    [void](Wait-ForElement $window 'Boostix.Center.Language.ru' $process.Id)
    Start-Sleep -Milliseconds 600
    $russian = Get-RussianNames $window
    if ($russian.Count -gt 0) {
        throw ("English Settings still show Russian:`n" + ($russian -join "`n"))
    }

    $statusPill = Wait-ForElement $window 'Boostix.StatusPill' $process.Id
    $statusPill.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    [void](Wait-ForElement $window 'Boostix.Activate' $process.Id)
    Start-Sleep -Milliseconds 600
    $russian = Get-RussianNames $window
    if ($russian.Count -gt 0) {
        throw ("English Home still shows Russian:`n" + ($russian -join "`n"))
    }
    $gamesTab = Wait-ForElement $window 'Boostix.Center.Tab.Games' $process.Id
    $gamesTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    [void](Wait-ForElement $window 'Boostix.Games.Play' $process.Id)
    Start-Sleep -Milliseconds 600
    $russian = Get-RussianNames $window
    if ($russian.Count -gt 0) {
        throw ("English Games still show Russian:`n" + ($russian -join "`n"))
    }
    $reportTab = Wait-ForElement $window 'Boostix.Center.Tab.Report' $process.Id
    $reportTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $journalRow = Wait-ForElement $window 'Boostix.Center.History.0' $process.Id
    Start-Sleep -Milliseconds 600
    $russian = Get-RussianNames $window
    if ($russian.Count -gt 0) {
        throw ("English journal still shows Russian:`n" + ($russian -join "`n"))
    }
    $journalRow.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    [void](Wait-ForElement $window 'Boostix.Center.Debrief.Headline' $process.Id)
    Start-Sleep -Milliseconds 400
    $russian = Get-RussianNames $window
    if ($russian.Count -gt 0) {
        throw ("English debrief still shows Russian:`n" + ($russian -join "`n"))
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

'Localization checks passed.'
