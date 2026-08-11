[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5 -or
    [IntPtr]::Size -ne 8) {
    throw 'This regression test requires 64-bit Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $projectRoot 'Boostix\Program.cs'
if (-not (Test-Path -LiteralPath $programPath -PathType Leaf)) {
    throw "Boostix program source was not found: $programPath"
}
$program = [IO.File]::ReadAllText($programPath)

function Get-SourceSlice {
    param(
        [Parameter(Mandatory = $true)][string]$Start,
        [Parameter(Mandatory = $true)][string]$End
    )

    $startIndex = $program.IndexOf($Start, [StringComparison]::Ordinal)
    if ($startIndex -lt 0) {
        throw "Start boundary was not found: $Start"
    }
    $endIndex = $program.IndexOf(
        $End,
        $startIndex + $Start.Length,
        [StringComparison]::Ordinal)
    if ($endIndex -lt 0) {
        throw "End boundary was not found after '$Start': $End"
    }
    return $program.Substring($startIndex, $endIndex - $startIndex)
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Text.Contains($Expected)) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Forbidden,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if ($Text.Contains($Forbidden)) {
        throw $Message
    }
}

$budgetMatch = [regex]::Match(
    $program,
    'SessionPowerPlanShutdownGraceMilliseconds\s*=\s*(\d+)')
if (-not $budgetMatch.Success) {
    throw 'The shutdown grace budget is not an explicit source constant.'
}
$shutdownBudget = [int]$budgetMatch.Groups[1].Value
if ($shutdownBudget -lt 1 -or $shutdownBudget -gt 750) {
    throw "The shutdown grace budget must be in 1..750 ms; actual: $shutdownBudget"
}

$startMethod = Get-SourceSlice `
    'private void StartSessionPowerPlan()' `
    'private void RecordSessionPowerPlanStartResult('
Assert-Contains $startMethod 'sessionPowerPlanStopRequest != null' `
    'A new plan can start before the previous exact stop operation is separated.'
Assert-Contains $startMethod 'activePowerPlanSessionId = sessionId;' `
    'Power-plan ownership is not bound to the exact current session ID.'

$stopMethod = Get-SourceSlice `
    'private void StopSessionPowerPlan()' `
    'private BoostSessionReport FindSessionReportForPowerPlan('
foreach ($required in @(
    'sessionPowerPlanStopRequest != null',
    'SessionId = sessionId',
    'StartTask = sessionPowerPlanStartTask',
    'CompletedReport = FindSessionReportForPowerPlan(sessionId)',
    'sessionPowerPlanStopRequest = request;',
    'StopSessionPowerPlanWorkerAsync(',
    'TaskScheduler.Default',
    'Dispatcher.BeginInvoke'
)) {
    Assert-Contains $stopMethod $required `
        "The asynchronous exact-session stop contract is missing: $required"
}
foreach ($forbidden in @(
    '.GetAwaiter().GetResult()',
    '.Wait(',
    'manager.Stop(',
    'sessionPowerPlanManager.Stop('
)) {
    Assert-NotContains $stopMethod $forbidden `
        "StopSessionPowerPlan performs synchronous dispatcher work: $forbidden"
}

$workerMethod = Get-SourceSlice `
    'private static async Task<SessionPowerPlanStopCompletion>' `
    'private void CompleteSessionPowerPlanStop('
foreach ($required in @(
    'await request.StartTask',
    '.ConfigureAwait(false)',
    'await Task.Run(delegate',
    'manager.Stop(request.SessionId)',
    'EnsurePowerPlanStartAction(',
    'EnsurePowerPlanResultAction(',
    'BoostSessionReportStore.Save(',
    'completion.CompletedReport'
)) {
    Assert-Contains $workerMethod $required `
        "The worker-side stop/report contract is missing: $required"
}
Assert-NotContains $workerMethod 'Dispatcher.' `
    'The slow power-plan worker unexpectedly depends on the WPF dispatcher.'

$completionMethod = Get-SourceSlice `
    'private void CompleteSessionPowerPlanStop(' `
    'private void GiveSessionPowerPlanStopShutdownGrace()'
foreach ($required in @(
    'object.ReferenceEquals(',
    'completion.SessionId != request.SessionId',
    'ReportMatchesSession(',
    'completion.CompletedReport',
    'BoostSessionReportStore.Save(lastSession.Clone())',
    'StartSessionPowerPlan();'
)) {
    Assert-Contains $completionMethod $required `
        "The exact-session completion fence is missing: $required"
}
Assert-NotContains $completionMethod 'currentSession.AddAction(' `
    'An old power-plan result can be attached to a newer current session.'

$graceMethod = Get-SourceSlice `
    'private void GiveSessionPowerPlanStopShutdownGrace()' `
    'private static void EnsurePowerPlanStartAction('
foreach ($required in @(
    'sessionPowerPlanStopRequest.WorkerTask',
    'Task.WaitAny(',
    'SessionPowerPlanShutdownGraceMilliseconds'
)) {
    Assert-Contains $graceMethod $required `
        "The bounded shutdown grace contract is missing: $required"
}
foreach ($forbidden in @(
    'manager.Stop(',
    'sessionPowerPlanManager.Stop(',
    '.GetAwaiter().GetResult()',
    '.Result'
)) {
    Assert-NotContains $graceMethod $forbidden `
        "Shutdown grace starts or unwraps slow work on the dispatcher: $forbidden"
}

$completeCurrent = Get-SourceSlice `
    'private void CompleteCurrentSession(string status, string reason)' `
    'private static string FormatMemory('
$completeIndex = $completeCurrent.IndexOf(
    'currentSession.Complete(status, reason);',
    [StringComparison]::Ordinal)
$saveIndex = $completeCurrent.IndexOf(
    'BoostSessionReportStore.Save(currentSession)',
    [StringComparison]::Ordinal)
$stopIndex = $completeCurrent.IndexOf(
    'StopSessionPowerPlan();',
    $saveIndex + 1,
    [StringComparison]::Ordinal)
$clearIndex = $completeCurrent.IndexOf(
    'currentSession = null;',
    [StringComparison]::Ordinal)
if ($completeIndex -lt 0 -or $saveIndex -le $completeIndex -or
    $stopIndex -le $saveIndex -or $clearIndex -le $stopIndex) {
    throw 'The completed exact report is not captured before currentSession is cleared.'
}

$windowClosed = Get-SourceSlice `
    'private void WindowClosed(object sender, EventArgs e)' `
    'private void TryDeleteReadinessSignal()'
$closeCompleteIndex = $windowClosed.IndexOf(
    'CompleteCurrentSession(',
    [StringComparison]::Ordinal)
$closeGraceIndex = $windowClosed.IndexOf(
    'GiveSessionPowerPlanStopShutdownGrace();',
    [StringComparison]::Ordinal)
if ($closeCompleteIndex -lt 0 -or $closeGraceIndex -le $closeCompleteIndex) {
    throw 'Window close does not first schedule exact Stop and then grant bounded grace.'
}
Assert-NotContains $windowClosed 'sessionPowerPlanManager.Stop(' `
    'WindowClosed directly invokes the synchronous power-plan manager.'

# Deterministic guard for the chosen wait primitive: a deliberately slow task
# must time out near the explicit production budget, never wait for completion.
$slowTask = [Threading.Tasks.Task]::Delay($shutdownBudget + 2000)
$watch = [Diagnostics.Stopwatch]::StartNew()
$waitResult = [Threading.Tasks.Task]::WaitAny(
    [Threading.Tasks.Task[]]@($slowTask),
    $shutdownBudget)
$watch.Stop()
if ($waitResult -ne -1) {
    throw 'The slow fake worker unexpectedly completed inside the grace budget.'
}
if ($watch.ElapsedMilliseconds -gt ($shutdownBudget + 350)) {
    throw (
        'The bounded shutdown wait exceeded its budget: {0} ms for {1} ms.' -f
        $watch.ElapsedMilliseconds,
        $shutdownBudget)
}

Write-Host (
    'Session power-plan async boundary regression passed ' +
    "(shutdown grace ${shutdownBudget}ms).") -ForegroundColor Green
