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
$sourcePath = Join-Path $projectRoot 'Boostix\SessionPowerPlan.cs'
$programPath = Join-Path $projectRoot 'Boostix\Program.cs'
$buildPath = Join-Path $projectRoot 'build.ps1'
$compiler = Join-Path $env:WINDIR (
    'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Session power-plan source was not found: $sourcePath"
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The .NET Framework compiler was not found: $compiler"
}

$source = [IO.File]::ReadAllText($sourcePath)
$program = [IO.File]::ReadAllText($programPath)
$build = [IO.File]::ReadAllText($buildPath)
foreach ($required in @(
    'internal sealed class SessionPowerPlanManager',
    'public SessionPowerPlanOperationResult Start(Guid sessionId)',
    'public SessionPowerPlanOperationResult Stop(Guid sessionId)',
    'public SessionPowerPlanOperationResult RecoverOnStartup()',
    'internal interface ISessionPowerPlanCommandRunner',
    'internal interface ISessionPowerPlanPlatform',
    'internal interface ISessionPowerPlanStateStore',
    'SessionPowerPlanConfiguration.RequiredPlanName',
    'Boostix Performance',
    'FileAttributes.ReparsePoint',
    'AccessControlSections.Access | AccessControlSections.Owner',
    'IsProcessInstanceAlive(',
    'OwnerProcessStartTimeUtc',
    'ExternalOverridePreserved',
    'TimeSpan.FromSeconds(10)',
    'FileOptions.WriteThrough',
    'output.Flush(true)'
)) {
    if (-not $source.Contains($required)) {
        throw "The session power-plan safety contract is missing: $required"
    }
}

foreach ($integrationContract in @(
    'new SessionPowerPlanManager(',
    'sessionPowerPlanManager.RecoverOnStartup()',
    'StartSessionPowerPlan();',
    'StopSessionPowerPlan();',
    'sessionPowerPlanManager.Start(sessionId)',
    'manager.Stop(request.SessionId)'
)) {
    if (-not $program.Contains($integrationContract)) {
        throw "Session power-plan production integration is missing: $integrationContract"
    }
}
if (-not $build.Contains('Boostix\SessionPowerPlan.cs')) {
    throw 'SessionPowerPlan.cs is not compiled into Boostix.'
}

foreach ($forbiddenSwitch in @(
    '/change',
    '/duplicatescheme',
    '/delete',
    '/setacvalueindex',
    '/setdcvalueindex',
    '/setvalueindex',
    '/hibernate',
    '/import',
    '/restoredefaultschemes'
)) {
    if ($source.IndexOf(
            $forbiddenSwitch,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "A forbidden power-setting mutation is present: $forbiddenSwitch"
    }
}
if ([regex]::Matches(
        $source,
        '"/getactivescheme"',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase).Count -ne 1 -or
    [regex]::Matches(
        $source,
        '"/setactive "',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase).Count -ne 1) {
    throw 'The powercfg surface is not limited to one get-active and one set-active switch.'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-SessionPowerPlan-Test-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temporaryRoot -Force)
try {
    $harnessPath = Join-Path $temporaryRoot 'SessionPowerPlanHarness.cs'
    $assemblyPath = Join-Path $temporaryRoot 'SessionPowerPlanHarness.dll'
    $harness = @'
using System;
using System.Collections.Generic;

namespace Boostix
{
    internal static class SessionPowerPlanRegressionHarness
    {
        private static readonly Guid BoostGuid =
            Guid.Parse("11111111-1111-4111-8111-111111111111");
        private static readonly Guid PreviousGuid =
            Guid.Parse("22222222-2222-4222-8222-222222222222");
        private static readonly Guid ExternalGuid =
            Guid.Parse("33333333-3333-4333-8333-333333333333");
        private static readonly Guid SessionA =
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        private static readonly Guid SessionB =
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        private static readonly DateTime ProcessStart =
            new DateTime(638900000000000000L, DateTimeKind.Utc);
        private static readonly DateTime Now =
            new DateTime(638900001000000000L, DateTimeKind.Utc);

        public static string Run()
        {
            TestAcActivationAndRestore();
            TestDcAndUnknownPowerAreClosed();
            TestMalformedAndUntrustedState();
            TestPidAndSessionMarkerOwnership();
            TestExternalOverrideIsPreserved();
            TestIdempotentStartStop();
            TestCrashRecovery();
            TestTimeoutsAreReportedAndBounded();
            TestAlreadyActiveIsNotClaimed();
            TestStrictOutputParser();
            return "Session power-plan regression harness passed.";
        }

        private static void TestAcActivationAndRestore()
        {
            FakePlatform platform = NewPlatform(SessionPowerSource.AlternatingCurrent);
            var runner = new FakeRunner(PreviousGuid);
            FakeStore store = NewStore();
            SessionPowerPlanManager manager = NewManager(platform, runner, store);

            SessionPowerPlanOperationResult started = manager.Start(SessionA);
            Assert(started.Action == SessionPowerPlanAction.Start,
                "Start action was not reported.");
            Assert(started.Status == SessionPowerPlanStatus.Activated && started.Changed,
                "AC activation did not succeed.");
            Assert(runner.ActiveGuid == BoostGuid && runner.SetCalls == 1,
                "The trusted Boostix GUID was not selected exactly once.");
            Assert(store.Marker != null &&
                store.Marker.PreviousPlanGuid == PreviousGuid &&
                store.Marker.BoostPlanGuid == BoostGuid &&
                store.Marker.SessionId == SessionA &&
                store.Marker.OwnerProcessId == platform.CurrentProcessId &&
                store.Marker.OwnerProcessStartTimeUtc == ProcessStart,
                "The marker did not capture actual scheme/process/session identity.");

            SessionPowerPlanOperationResult stopped = manager.Stop(SessionA);
            Assert(stopped.Action == SessionPowerPlanAction.Stop,
                "Stop action was not reported.");
            Assert(stopped.Status == SessionPowerPlanStatus.Restored && stopped.Changed,
                "The pre-session plan was not restored.");
            Assert(runner.ActiveGuid == PreviousGuid && runner.SetCalls == 2,
                "Stop did not select the exact saved plan.");
            Assert(store.Marker == null, "A successful stop left a marker behind.");
        }

        private static void TestDcAndUnknownPowerAreClosed()
        {
            foreach (SessionPowerSource source in new[]
            {
                SessionPowerSource.Battery,
                SessionPowerSource.Unknown
            })
            {
                FakePlatform platform = NewPlatform(source);
                var runner = new FakeRunner(PreviousGuid);
                FakeStore store = NewStore();
                SessionPowerPlanOperationResult result =
                    NewManager(platform, runner, store).Start(SessionA);
                SessionPowerPlanStatus expected = source == SessionPowerSource.Battery
                    ? SessionPowerPlanStatus.SkippedOnBattery
                    : SessionPowerPlanStatus.PowerSourceUnavailable;
                Assert(result.Status == expected && !result.Changed,
                    "DC/unknown power did not fail closed.");
                Assert(runner.TotalCalls == 0 && store.Marker == null,
                    "A plan or marker was changed without verified AC power.");
            }

            FakePlatform unplugged = NewPlatform(SessionPowerSource.AlternatingCurrent);
            unplugged.PowerReadSequence.Enqueue(SessionPowerSource.AlternatingCurrent);
            unplugged.PowerReadSequence.Enqueue(SessionPowerSource.Battery);
            var unplugRunner = new FakeRunner(PreviousGuid);
            FakeStore unplugStore = NewStore();
            SessionPowerPlanOperationResult unplugResult =
                NewManager(unplugged, unplugRunner, unplugStore).Start(SessionA);
            Assert(unplugResult.Status == SessionPowerPlanStatus.SkippedOnBattery &&
                unplugRunner.SetCalls == 0 && unplugStore.Marker == null,
                "The AC disconnect race did not fail closed.");
        }

        private static void TestMalformedAndUntrustedState()
        {
            FakePlatform platform = NewPlatform(SessionPowerSource.AlternatingCurrent);
            var runner = new FakeRunner(PreviousGuid);
            FakeStore corruptConfiguration = NewStore();
            corruptConfiguration.ConfigurationStatus =
                SessionPowerPlanStateStatus.Corrupt;
            SessionPowerPlanOperationResult corruptResult =
                NewManager(platform, runner, corruptConfiguration).Start(SessionA);
            Assert(corruptResult.Status == SessionPowerPlanStatus.TrustedStateRejected &&
                runner.TotalCalls == 0,
                "Corrupt trusted state was not rejected before powercfg.");

            FakeStore untrustedConfiguration = NewStore();
            untrustedConfiguration.ConfigurationStatus =
                SessionPowerPlanStateStatus.UntrustedOwner;
            SessionPowerPlanOperationResult ownerResult =
                NewManager(platform, new FakeRunner(PreviousGuid),
                    untrustedConfiguration).Start(SessionA);
            Assert(ownerResult.Status == SessionPowerPlanStatus.TrustedStateRejected,
                "Untrusted trusted-state ownership was accepted.");

            FakeStore corruptMarker = NewStore();
            corruptMarker.MarkerStatus = SessionPowerPlanStateStatus.Corrupt;
            SessionPowerPlanOperationResult markerResult =
                NewManager(platform, new FakeRunner(PreviousGuid), corruptMarker)
                    .Start(SessionA);
            Assert(markerResult.Status == SessionPowerPlanStatus.MarkerRejected,
                "A corrupt marker was accepted.");

            FakeStore redirectedMarker = NewStore();
            redirectedMarker.MarkerStatus =
                SessionPowerPlanStateStatus.UntrustedPath;
            SessionPowerPlanOperationResult pathResult =
                NewManager(platform, new FakeRunner(PreviousGuid), redirectedMarker)
                    .Start(SessionA);
            Assert(pathResult.Status == SessionPowerPlanStatus.MarkerRejected,
                "A redirected marker was accepted.");

            SessionPowerPlanConfiguration configuration;
            string error;
            Assert(!SessionPowerPlanStateJson.TryDeserializeConfiguration(
                    "{\"version\":1,\"planName\":\"Boostix Performance\",\"planGuid\":\"not-a-guid\"}",
                    out configuration,
                    out error),
                "An invalid trusted plan GUID was accepted.");
            Assert(!SessionPowerPlanStateJson.TryDeserializeConfiguration(
                    "{\"version\":1,\"planName\":\"Other\",\"planGuid\":\"" +
                        BoostGuid.ToString("D") + "\"}",
                    out configuration,
                    out error),
                "A trusted state with the wrong exact plan name was accepted.");
            Assert(!SessionPowerPlanStateJson.TryDeserializeConfiguration(
                    "{\"version\":1,\"version\":1,\"planName\":\"Boostix Performance\",\"planGuid\":\"" +
                        BoostGuid.ToString("D") + "\"}",
                    out configuration,
                    out error),
                "Duplicate JSON keys were accepted.");

            string roundTrip = SessionPowerPlanStateJson.SerializeConfiguration(
                new SessionPowerPlanConfiguration(
                    BoostGuid,
                    SessionPowerPlanConfiguration.RequiredPlanName));
            Assert(SessionPowerPlanStateJson.TryDeserializeConfiguration(
                    roundTrip, out configuration, out error) &&
                configuration.PlanGuid == BoostGuid,
                "A canonical trusted state did not round-trip.");
        }

        private static void TestPidAndSessionMarkerOwnership()
        {
            DateTime oldStart = ProcessStart.AddHours(-1);
            FakePlatform livePlatform = NewPlatform(
                SessionPowerSource.AlternatingCurrent);
            livePlatform.Alive = delegate(int pid, DateTime start)
            {
                return pid == 777 && start == oldStart;
            };
            FakeStore liveStore = NewStore();
            liveStore.Marker = NewMarker(SessionA, 777, oldStart);
            var liveRunner = new FakeRunner(BoostGuid);
            SessionPowerPlanOperationResult live =
                NewManager(livePlatform, liveRunner, liveStore).RecoverOnStartup();
            Assert(live.Status == SessionPowerPlanStatus.LiveSessionPreserved &&
                liveRunner.TotalCalls == 0 && liveStore.Marker != null,
                "A marker owned by an exact live process was disturbed.");

            FakePlatform reusedPidPlatform = NewPlatform(
                SessionPowerSource.AlternatingCurrent);
            reusedPidPlatform.Alive = delegate(int pid, DateTime start)
            {
                return pid == 777 && start == oldStart.AddSeconds(1);
            };
            FakeStore reusedStore = NewStore();
            reusedStore.Marker = NewMarker(SessionA, 777, oldStart);
            var reusedRunner = new FakeRunner(BoostGuid);
            SessionPowerPlanOperationResult reused =
                NewManager(reusedPidPlatform, reusedRunner, reusedStore)
                    .RecoverOnStartup();
            Assert(reused.Status == SessionPowerPlanStatus.Recovered &&
                reusedRunner.ActiveGuid == PreviousGuid && reusedStore.Marker == null,
                "PID reuse incorrectly preserved a stale marker.");

            FakeStore wrongSessionStore = NewStore();
            wrongSessionStore.Marker = NewMarker(
                SessionA,
                livePlatform.CurrentProcessId,
                ProcessStart);
            SessionPowerPlanOperationResult wrongSession =
                NewManager(livePlatform, new FakeRunner(BoostGuid), wrongSessionStore)
                    .Stop(SessionB);
            Assert(wrongSession.Status == SessionPowerPlanStatus.SessionMismatch &&
                wrongSessionStore.Marker != null,
                "Stop accepted another session's marker.");
        }

        private static void TestExternalOverrideIsPreserved()
        {
            FakePlatform platform = NewPlatform(SessionPowerSource.AlternatingCurrent);
            FakeStore stopStore = NewStore();
            stopStore.Marker = NewMarker(
                SessionA,
                platform.CurrentProcessId,
                ProcessStart);
            var stopRunner = new FakeRunner(ExternalGuid);
            SessionPowerPlanOperationResult stopped =
                NewManager(platform, stopRunner, stopStore).Stop(SessionA);
            Assert(stopped.Status == SessionPowerPlanStatus.ExternalOverridePreserved &&
                stopRunner.ActiveGuid == ExternalGuid && stopRunner.SetCalls == 0 &&
                stopStore.Marker == null,
                "Stop overwrote an external power-plan choice.");

            FakeStore recoveryStore = NewStore();
            recoveryStore.Marker = NewMarker(SessionA, 999, ProcessStart.AddHours(-1));
            var recoveryRunner = new FakeRunner(ExternalGuid);
            SessionPowerPlanOperationResult recovered =
                NewManager(platform, recoveryRunner, recoveryStore).RecoverOnStartup();
            Assert(recovered.Status == SessionPowerPlanStatus.ExternalOverridePreserved &&
                recoveryRunner.ActiveGuid == ExternalGuid &&
                recoveryRunner.SetCalls == 0 && recoveryStore.Marker == null,
                "Crash recovery overwrote an external power-plan choice.");
        }

        private static void TestIdempotentStartStop()
        {
            FakePlatform platform = NewPlatform(SessionPowerSource.AlternatingCurrent);
            var runner = new FakeRunner(PreviousGuid);
            FakeStore store = NewStore();
            SessionPowerPlanManager manager = NewManager(platform, runner, store);

            Assert(manager.Start(SessionA).Status == SessionPowerPlanStatus.Activated,
                "Initial activation failed.");
            Assert(manager.Start(SessionA).Status == SessionPowerPlanStatus.AlreadyActive,
                "Repeated start was not idempotent.");
            Assert(runner.SetCalls == 1,
                "Repeated start executed an extra set-active command.");
            Assert(manager.Stop(SessionA).Status == SessionPowerPlanStatus.Restored,
                "Initial stop failed.");
            Assert(manager.Stop(SessionA).Status == SessionPowerPlanStatus.AlreadyStopped,
                "Repeated stop was not idempotent.");
            Assert(runner.SetCalls == 2,
                "Repeated stop executed an extra set-active command.");
        }

        private static void TestCrashRecovery()
        {
            FakePlatform platform = NewPlatform(SessionPowerSource.Battery);
            platform.Alive = delegate(int pid, DateTime start) { return false; };
            FakeStore store = NewStore();
            store.Marker = NewMarker(SessionA, 888, ProcessStart.AddDays(-1));
            var runner = new FakeRunner(BoostGuid);
            SessionPowerPlanOperationResult result =
                NewManager(platform, runner, store).RecoverOnStartup();
            Assert(result.Action == SessionPowerPlanAction.CrashRecovery &&
                result.Status == SessionPowerPlanStatus.Recovered && result.Changed,
                "Crash recovery did not restore a stale owned plan.");
            Assert(runner.ActiveGuid == PreviousGuid && store.Marker == null,
                "Crash recovery did not restore/delete atomically enough to finish.");
        }

        private static void TestTimeoutsAreReportedAndBounded()
        {
            FakePlatform platform = NewPlatform(SessionPowerSource.AlternatingCurrent);
            var queryTimeoutRunner = new FakeRunner(PreviousGuid);
            queryTimeoutRunner.TimeoutNextGet = true;
            FakeStore queryStore = NewStore();
            SessionPowerPlanOperationResult queryTimeout =
                NewManager(platform, queryTimeoutRunner, queryStore).Start(SessionA);
            Assert(queryTimeout.Status == SessionPowerPlanStatus.CommandTimedOut &&
                queryTimeoutRunner.SetCalls == 0 && queryStore.Marker == null,
                "A get-active timeout was not reported without mutation.");

            var setTimeoutRunner = new FakeRunner(PreviousGuid);
            setTimeoutRunner.TimeoutNextSet = true;
            FakeStore setStore = NewStore();
            SessionPowerPlanOperationResult setTimeout =
                NewManager(platform, setTimeoutRunner, setStore).Start(SessionA);
            Assert(setTimeout.Status == SessionPowerPlanStatus.CommandTimedOut &&
                setTimeoutRunner.ActiveGuid == PreviousGuid && setStore.Marker == null,
                "A set-active timeout did not fail closed/clean up safely.");
            Assert(queryTimeoutRunner.MaximumTimeout <= TimeSpan.FromSeconds(5) &&
                setTimeoutRunner.MaximumTimeout <= TimeSpan.FromSeconds(5),
                "The manager passed an unbounded command timeout.");

            bool rejected = false;
            try
            {
                new SessionPowerPlanManager(
                    platform,
                    new FakeRunner(PreviousGuid),
                    NewStore(),
                    TimeSpan.FromSeconds(11));
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }
            Assert(rejected, "A timeout above the hard ten-second bound was accepted.");
        }

        private static void TestAlreadyActiveIsNotClaimed()
        {
            FakePlatform platform = NewPlatform(SessionPowerSource.AlternatingCurrent);
            var runner = new FakeRunner(BoostGuid);
            FakeStore store = NewStore();
            SessionPowerPlanOperationResult result =
                NewManager(platform, runner, store).Start(SessionA);
            Assert(result.Status == SessionPowerPlanStatus.AlreadyActive &&
                !result.Changed && runner.SetCalls == 0 && store.Marker == null,
                "An already-active pre-existing plan was incorrectly claimed.");
        }

        private static void TestStrictOutputParser()
        {
            Guid parsed;
            Assert(SessionPowerPlanOutputParser.TryParseActiveScheme(
                    "Power Scheme GUID: " + PreviousGuid.ToString("D") +
                        " (Balanced)",
                    out parsed) && parsed == PreviousGuid,
                "A valid powercfg response was not parsed.");
            Assert(!SessionPowerPlanOutputParser.TryParseActiveScheme(
                    PreviousGuid.ToString("D") + " " + ExternalGuid.ToString("D"),
                    out parsed),
                "An ambiguous powercfg response was accepted.");
            Assert(!SessionPowerPlanOutputParser.TryParseActiveScheme(
                    "not a GUID", out parsed),
                "A malformed powercfg response was accepted.");
        }

        private static SessionPowerPlanManager NewManager(
            FakePlatform platform,
            FakeRunner runner,
            FakeStore store)
        {
            return new SessionPowerPlanManager(
                platform,
                runner,
                store,
                TimeSpan.FromSeconds(5));
        }

        private static FakePlatform NewPlatform(SessionPowerSource source)
        {
            return new FakePlatform
            {
                IsWindowsValue = true,
                PowerSource = source,
                CurrentProcessIdValue = 4242,
                CurrentProcessStartTimeUtcValue = ProcessStart,
                UtcNowValue = Now,
                Alive = delegate(int pid, DateTime start) { return false; }
            };
        }

        private static FakeStore NewStore()
        {
            return new FakeStore
            {
                Configuration = new SessionPowerPlanConfiguration(
                    BoostGuid,
                    SessionPowerPlanConfiguration.RequiredPlanName),
                ConfigurationStatus = SessionPowerPlanStateStatus.Valid,
                MarkerStatus = SessionPowerPlanStateStatus.Missing,
                WriteStatus = SessionPowerPlanStateStatus.Valid,
                DeleteStatus = SessionPowerPlanStateStatus.Valid
            };
        }

        private static SessionPowerPlanMarker NewMarker(
            Guid sessionId,
            int processId,
            DateTime processStart)
        {
            return new SessionPowerPlanMarker(
                sessionId,
                processId,
                processStart,
                BoostGuid,
                PreviousGuid,
                Now);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class FakePlatform : ISessionPowerPlanPlatform
        {
            public bool IsWindowsValue;
            public SessionPowerSource PowerSource;
            public int CurrentProcessIdValue;
            public DateTime CurrentProcessStartTimeUtcValue;
            public DateTime UtcNowValue;
            public Func<int, DateTime, bool> Alive;
            public readonly Queue<SessionPowerSource> PowerReadSequence =
                new Queue<SessionPowerSource>();

            public bool IsWindows { get { return IsWindowsValue; } }
            public int CurrentProcessId { get { return CurrentProcessIdValue; } }
            public DateTime CurrentProcessStartTimeUtc
            {
                get { return CurrentProcessStartTimeUtcValue; }
            }
            public DateTime UtcNow { get { return UtcNowValue; } }

            public SessionPowerSource GetPowerSource()
            {
                return PowerReadSequence.Count > 0
                    ? PowerReadSequence.Dequeue()
                    : PowerSource;
            }

            public bool IsProcessInstanceAlive(int processId, DateTime startTimeUtc)
            {
                return Alive != null && Alive(processId, startTimeUtc);
            }
        }

        private sealed class FakeRunner : ISessionPowerPlanCommandRunner
        {
            public FakeRunner(Guid activeGuid)
            {
                ActiveGuid = activeGuid;
            }

            public Guid ActiveGuid;
            public bool TimeoutNextGet;
            public bool TimeoutNextSet;
            public int GetCalls;
            public int SetCalls;
            public TimeSpan MaximumTimeout;
            public int TotalCalls { get { return GetCalls + SetCalls; } }

            public SessionPowerPlanCommandResult Run(
                SessionPowerPlanCommand command,
                Guid? schemeGuid,
                TimeSpan timeout)
            {
                if (timeout > MaximumTimeout)
                {
                    MaximumTimeout = timeout;
                }
                if (command == SessionPowerPlanCommand.GetActiveScheme)
                {
                    GetCalls++;
                    if (TimeoutNextGet)
                    {
                        TimeoutNextGet = false;
                        return SessionPowerPlanCommandResult.Create(
                            SessionPowerPlanCommandStatus.TimedOut,
                            -1,
                            string.Empty,
                            "timeout");
                    }
                    return SessionPowerPlanCommandResult.Create(
                        SessionPowerPlanCommandStatus.Succeeded,
                        0,
                        "Power Scheme GUID: " + ActiveGuid.ToString("D") + " (Fake)",
                        string.Empty);
                }
                SetCalls++;
                if (!schemeGuid.HasValue || schemeGuid.Value == Guid.Empty)
                {
                    return SessionPowerPlanCommandResult.Create(
                        SessionPowerPlanCommandStatus.InvalidRequest,
                        -1,
                        string.Empty,
                        "missing GUID");
                }
                if (TimeoutNextSet)
                {
                    TimeoutNextSet = false;
                    return SessionPowerPlanCommandResult.Create(
                        SessionPowerPlanCommandStatus.TimedOut,
                        -1,
                        string.Empty,
                        "timeout");
                }
                ActiveGuid = schemeGuid.Value;
                return SessionPowerPlanCommandResult.Create(
                    SessionPowerPlanCommandStatus.Succeeded,
                    0,
                    string.Empty,
                    string.Empty);
            }
        }

        private sealed class FakeStore : ISessionPowerPlanStateStore
        {
            public SessionPowerPlanConfiguration Configuration;
            public SessionPowerPlanMarker Marker;
            public SessionPowerPlanStateStatus ConfigurationStatus;
            public SessionPowerPlanStateStatus MarkerStatus;
            public SessionPowerPlanStateStatus WriteStatus;
            public SessionPowerPlanStateStatus DeleteStatus;
            public int WriteCalls;
            public int DeleteCalls;

            public SessionPowerPlanStateRead<SessionPowerPlanConfiguration>
                ReadTrustedConfiguration()
            {
                return SessionPowerPlanStateRead<SessionPowerPlanConfiguration>.Create(
                    ConfigurationStatus,
                    ConfigurationStatus == SessionPowerPlanStateStatus.Valid
                        ? Configuration
                        : null,
                    ConfigurationStatus.ToString());
            }

            public SessionPowerPlanStateRead<SessionPowerPlanMarker> ReadMarker()
            {
                SessionPowerPlanStateStatus status = Marker == null
                    ? MarkerStatus
                    : SessionPowerPlanStateStatus.Valid;
                return SessionPowerPlanStateRead<SessionPowerPlanMarker>.Create(
                    status,
                    status == SessionPowerPlanStateStatus.Valid ? Marker : null,
                    status.ToString());
            }

            public SessionPowerPlanStateStatus WriteMarker(
                SessionPowerPlanMarker marker,
                out string detail)
            {
                WriteCalls++;
                detail = WriteStatus.ToString();
                if (WriteStatus == SessionPowerPlanStateStatus.Valid)
                {
                    Marker = marker;
                    MarkerStatus = SessionPowerPlanStateStatus.Valid;
                }
                return WriteStatus;
            }

            public SessionPowerPlanStateStatus DeleteMarker(out string detail)
            {
                DeleteCalls++;
                detail = DeleteStatus.ToString();
                if (DeleteStatus == SessionPowerPlanStateStatus.Valid ||
                    DeleteStatus == SessionPowerPlanStateStatus.Missing)
                {
                    Marker = null;
                    MarkerStatus = SessionPowerPlanStateStatus.Missing;
                }
                return DeleteStatus;
            }
        }
    }
}
'@
    [IO.File]::WriteAllText(
        $harnessPath,
        $harness,
        (New-Object Text.UTF8Encoding($false)))

    $arguments = @(
        '/nologo',
        '/target:library',
        '/optimize+',
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Security.dll',
        "/out:$assemblyPath",
        $sourcePath,
        $harnessPath
    )
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Session power-plan harness compilation failed with exit code $LASTEXITCODE."
    }

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($assemblyPath))
    $type = $assembly.GetType('Boostix.SessionPowerPlanRegressionHarness', $true)
    $method = $type.GetMethod(
        'Run',
        [Reflection.BindingFlags]::Public -bor
            [Reflection.BindingFlags]::NonPublic -bor
            [Reflection.BindingFlags]::Static)
    if (-not $method) {
        throw 'The session power-plan regression entry point was not compiled.'
    }
    Write-Host ([string]$method.Invoke($null, @())) -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
