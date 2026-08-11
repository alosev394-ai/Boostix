[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'Boostix\SessionGuard.cs'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Session Guard source was not found: $sourcePath"
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The .NET Framework compiler was not found: $compiler"
}

$source = [IO.File]::ReadAllText($sourcePath)
foreach ($forbidden in @(
    'NtSetSystemInformation',
    'MemoryPurgeStandbyList',
    'SetSystemFileCacheSize',
    'EmptyWorkingSet',
    'OpenProcess(',
    'AdjustTokenPrivileges',
    'SeDebugPrivilege'
)) {
    if ($source.Contains($forbidden)) {
        throw "Session Guard contains a forbidden memory-mutation API: $forbidden"
    }
}

foreach ($required in @(
    'internal sealed class BoundedRingBuffer<T>',
    'internal sealed class SessionGuardTargetIdentity',
    'internal sealed class SessionGuardPressurePolicy',
    'internal static class PagefileAdvisor',
    'internal interface ISessionGuardMetricsSource',
    'internal interface ISessionGuardClock',
    'cheapInterval < TimeSpan.FromSeconds(1)',
    'heavyInterval < TimeSpan.FromSeconds(10)',
    'TimeSpan.FromSeconds(60)',
    'processStartUtc != target.ProcessStartTimeUtc',
    'Task.Delay(delay, cancellationToken)',
    'WmiEnumerationTimeout',
    'searcher.Options.Timeout = WmiEnumerationTimeout',
    'using (ManagementObjectCollection systems = searcher.Get())',
    'using (Process process = Process.GetProcessById(target.ProcessId))'
)) {
    if (-not $source.Contains($required)) {
        throw "Session Guard contract is missing: $required"
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-SessionGuard-Test-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temporaryRoot -Force)
try {
    $harnessPath = Join-Path $temporaryRoot 'SessionGuardHarness.cs'
    $assemblyPath = Join-Path $temporaryRoot 'SessionGuardHarness.dll'
    $harness = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Boostix
{
    internal static class SessionGuardRegressionHarness
    {
        private const long GiB = 1024L * 1024L * 1024L;

        public static string Run()
        {
            TestRingBuffer();
            TestSingleSpikeIsIgnored();
            TestSustainedCriticalAlert();
            TestZeroCommitHeadroom();
            TestRecoveryHysteresisAndCooldown();
            TestPagefileRecommendations();
            TestExactTargetIdentity();
            TestWindowsSourceRejectsIdentityMismatch();
            TestSamplingCadenceAndDisposal();
            TestBlockingHeavyStopAndDeferredDisposal();
            TestDefensiveValidation();
            return "Session Guard regression harness passed.";
        }

        private static void TestRingBuffer()
        {
            var buffer = new BoundedRingBuffer<int>(3);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4);
            buffer.Add(5);
            int[] snapshot = buffer.Snapshot();
            Assert(snapshot.Length == 3, "Ring-buffer bound was not enforced.");
            Assert(
                snapshot[0] == 3 && snapshot[1] == 4 && snapshot[2] == 5,
                "Ring-buffer order is not oldest-to-newest.");
            buffer.Clear();
            Assert(buffer.Count == 0, "Ring-buffer clear did not reset count.");
        }

        private static void TestSingleSpikeIsIgnored()
        {
            var policy = CreatePolicy();
            var state = new SessionGuardPressureState();
            SessionGuardPressureEvaluation spike = policy.Evaluate(
                CreateCriticalSample(1), state, Seconds(1));
            Assert(
                spike.Decision == SessionGuardPressureDecision.ObservingPressure,
                "A single spike raised an alert.");
            SessionGuardPressureEvaluation healthy = policy.Evaluate(
                CreateHealthySample(2), spike.NextState, Seconds(2));
            Assert(
                healthy.Decision == SessionGuardPressureDecision.Healthy &&
                healthy.NextState.ConsecutiveCriticalSamples == 0 &&
                !healthy.NextState.CriticalAlertActive,
                "A healthy sample did not clear the unconfirmed spike.");
        }

        private static void TestSustainedCriticalAlert()
        {
            var policy = CreatePolicy();
            var state = new SessionGuardPressureState();
            SessionGuardPressureEvaluation first = policy.Evaluate(
                CreateCriticalSample(1), state, Seconds(1));
            SessionGuardPressureEvaluation second = policy.Evaluate(
                CreateCriticalSample(2), first.NextState, Seconds(2));
            SessionGuardPressureEvaluation third = policy.Evaluate(
                CreateCriticalSample(3), second.NextState, Seconds(3));
            Assert(
                first.Decision == SessionGuardPressureDecision.ObservingPressure &&
                second.Decision == SessionGuardPressureDecision.ObservingPressure,
                "Pressure was not held for the required sustained samples.");
            Assert(
                third.Decision == SessionGuardPressureDecision.CriticalAlertRaised &&
                third.NextState.CriticalAlertActive,
                "Sustained critical pressure did not raise an alert.");
        }

        private static void TestZeroCommitHeadroom()
        {
            var policy = CreatePolicy();
            var state = new SessionGuardPressureState();
            SessionGuardPressureEvaluation evaluation = null;
            for (int index = 1; index <= 3; index++)
            {
                SessionGuardSample sample = CreateSample(
                    index,
                    4 * GiB,
                    0);
                Assert(
                    sample.CommitHeadroomBytes == 0,
                    "Zero commit headroom was not preserved as a valid metric.");
                evaluation = policy.Evaluate(
                    sample,
                    state,
                    Seconds(index));
                state = evaluation.NextState;
            }
            Assert(
                evaluation.Decision ==
                    SessionGuardPressureDecision.CriticalAlertRaised,
                "Sustained zero commit headroom did not raise a critical alert.");
        }

        private static void TestRecoveryHysteresisAndCooldown()
        {
            var policy = CreatePolicy();
            var state = new SessionGuardPressureState();
            SessionGuardPressureEvaluation evaluation = null;
            for (int index = 1; index <= 3; index++)
            {
                evaluation = policy.Evaluate(
                    CreateCriticalSample(index),
                    state,
                    Seconds(index));
                state = evaluation.NextState;
            }

            SessionGuardPressureEvaluation oneRecovery = policy.Evaluate(
                CreateHealthySample(4), state, Seconds(4));
            Assert(
                oneRecovery.Decision == SessionGuardPressureDecision.RecoveryPending,
                "One recovery sample incorrectly cleared the alert.");

            SessionGuardPressureEvaluation hysteresis = policy.Evaluate(
                CreateHysteresisBandSample(5),
                oneRecovery.NextState,
                Seconds(5));
            Assert(
                hysteresis.Decision ==
                    SessionGuardPressureDecision.CriticalPressureActive &&
                hysteresis.NextState.ConsecutiveRecoverySamples == 0,
                "The hysteresis band incorrectly counted as a recovery.");

            SessionGuardPressureEvaluation recoveryOne = policy.Evaluate(
                CreateHealthySample(6), hysteresis.NextState, Seconds(6));
            SessionGuardPressureEvaluation recovered = policy.Evaluate(
                CreateHealthySample(7), recoveryOne.NextState, Seconds(7));
            Assert(
                recovered.Decision == SessionGuardPressureDecision.Recovered &&
                !recovered.NextState.CriticalAlertActive,
                "Sustained recovery did not clear the alert.");

            state = recovered.NextState;
            for (int index = 8; index <= 10; index++)
            {
                evaluation = policy.Evaluate(
                    CreateCriticalSample(index),
                    state,
                    Seconds(index));
                state = evaluation.NextState;
            }
            Assert(
                evaluation.Decision == SessionGuardPressureDecision.Cooldown &&
                !evaluation.NextState.CriticalAlertActive,
                "A duplicate alert bypassed cooldown.");

            SessionGuardPressureEvaluation afterCooldown = policy.Evaluate(
                CreateCriticalSample(40), state, Seconds(40));
            Assert(
                afterCooldown.Decision ==
                    SessionGuardPressureDecision.CriticalAlertRaised,
                "A confirmed alert was not permitted after cooldown.");
        }

        private static void TestPagefileRecommendations()
        {
            PagefileAssessment healthy = PagefileAdvisor.Assess(
                CreatePagefile(PagefileConfigurationKind.SystemManaged, 40 * GiB));
            Assert(
                healthy.Recommendation == PagefileRecommendationCode.None &&
                !healthy.RequiresAttention,
                "A healthy system-managed page file was flagged.");

            PagefileAssessment disabled = PagefileAdvisor.Assess(
                CreatePagefile(PagefileConfigurationKind.Disabled, 40 * GiB));
            Assert(
                disabled.Recommendation ==
                    PagefileRecommendationCode.EnableSystemManaged &&
                disabled.RequiresAttention,
                "A disabled page file did not receive an actionable recommendation.");

            PagefileAssessment fixedSize = PagefileAdvisor.Assess(
                CreatePagefile(PagefileConfigurationKind.Fixed, 40 * GiB));
            Assert(
                fixedSize.Recommendation ==
                    PagefileRecommendationCode.PreferSystemManaged,
                "A fixed page file did not receive system-managed guidance.");

            PagefileAssessment lowDisk = PagefileAdvisor.Assess(
                CreatePagefile(PagefileConfigurationKind.SystemManaged, 2 * GiB));
            Assert(
                lowDisk.Recommendation ==
                    PagefileRecommendationCode.FreeSystemDriveSpace,
                "Low system-drive space was not detected.");

            PagefileAssessment unknown = PagefileAdvisor.Assess(
                CreatePagefile(PagefileConfigurationKind.Unknown, 40 * GiB));
            Assert(
                unknown.Recommendation ==
                    PagefileRecommendationCode.UnableToAssess &&
                !unknown.RequiresAttention,
                "Unknown state was presented as a confirmed fault.");
        }

        private static void TestExactTargetIdentity()
        {
            DateTime startedUtc = new DateTime(
                2026,
                1,
                2,
                3,
                4,
                5,
                DateTimeKind.Utc);
            var target = new SessionGuardTargetIdentity(
                42,
                startedUtc,
                @"C:\Games\BoostixTest.exe");
            Assert(
                target.Matches(
                    42,
                    startedUtc,
                    @"c:\games\BOOSTIXTEST.exe"),
                "Equivalent executable paths did not match.");
            Assert(
                !target.Matches(
                    42,
                    startedUtc.AddTicks(1),
                    @"C:\Games\BoostixTest.exe"),
                "A reused PID with a different start time was accepted.");
            Assert(
                !target.Matches(
                    42,
                    startedUtc,
                    @"C:\Games\Other.exe"),
                "A different executable path was accepted.");
        }

        private static void TestWindowsSourceRejectsIdentityMismatch()
        {
            using (Process process = Process.GetCurrentProcess())
            using (var source = new WindowsSessionGuardMetricsSource())
            {
                DateTime startedUtc = process.StartTime.ToUniversalTime();
                string executablePath = process.MainModule.FileName;
                var exact = new SessionGuardTargetIdentity(
                    process.Id,
                    startedUtc,
                    executablePath);
                SessionGuardSample exactSample = source.CaptureCheap(exact);
                Assert(
                    exactSample.TargetMetricsAvailable &&
                    exactSample.TargetProcessId == process.Id,
                    "The exact current-process identity was not sampled.");

                var wrongPath = new SessionGuardTargetIdentity(
                    process.Id,
                    startedUtc,
                    @"C:\Definitely-Not-The-Current-Process.exe");
                SessionGuardSample wrongPathSample = source.CaptureCheap(wrongPath);
                Assert(
                    !wrongPathSample.TargetMetricsAvailable &&
                    wrongPathSample.UnavailableReason.IndexOf(
                        "executable path",
                        StringComparison.OrdinalIgnoreCase) >= 0,
                    "An executable-path mismatch was not rejected.");

                var reusedPid = new SessionGuardTargetIdentity(
                    process.Id,
                    startedUtc.AddSeconds(-1),
                    executablePath);
                SessionGuardSample reusedSample = source.CaptureCheap(reusedPid);
                Assert(
                    !reusedSample.TargetMetricsAvailable &&
                    reusedSample.UnavailableReason.IndexOf(
                        "reused",
                        StringComparison.OrdinalIgnoreCase) >= 0,
                    "A PID/start-time mismatch was not rejected.");
            }
        }

        private static void TestSamplingCadenceAndDisposal()
        {
            var cancellation = new CancellationTokenSource();
            var clock = new FakeClock(cancellation, 31);
            var source = new FakeMetricsSource(clock);
            var sampler = new SessionGuardSampler(
                source,
                clock,
                new SessionGuardSamplerOptions(
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(60),
                    3),
                true);
            var target = new SessionGuardTargetIdentity(
                42,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                @"C:\Games\Fake.exe");
            Task task = sampler.StartAsync(target, cancellation.Token);
            task.Wait(TimeSpan.FromSeconds(2));

            Assert(source.CheapCaptures == 31, "Cheap cadence loop count is unexpected.");
            Assert(
                SpinWait.SpinUntil(
                    delegate { return source.GetHeavyTimestamps().Length == 2; },
                    TimeSpan.FromSeconds(1)),
                "Heavy cadence work did not finish deterministically.");
            long[] heavyTimestamps = source.GetHeavyTimestamps();
            Assert(heavyTimestamps.Length == 2, "Heavy work ran too often.");
            Assert(
                heavyTimestamps[1] - heavyTimestamps[0] >=
                    Seconds(60),
                "Heavy work ran more often than once per minute.");
            Assert(
                sampler.GetHistorySnapshot().Length == 3,
                "Sampler history exceeded or missed its configured bound.");
            sampler.Dispose();
            cancellation.Dispose();
            Assert(source.Disposed, "Owned metrics source was not disposed.");

            SessionGuardSamplerOptions defaults =
                SessionGuardSamplerOptions.CreateDefault();
            Assert(
                defaults.HeavyInterval == TimeSpan.FromSeconds(60),
                "The production heavy cadence is not one minute.");
        }

        private static void TestBlockingHeavyStopAndDeferredDisposal()
        {
            var clock = new YieldingClock();
            var source = new BlockingMetricsSource(clock);
            var sampler = new SessionGuardSampler(
                source,
                clock,
                new SessionGuardSamplerOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(10),
                    32),
                true);
            var target = new SessionGuardTargetIdentity(
                77,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                @"C:\Games\BlockingFake.exe");
            int cheapEvents = 0;
            int heavyEvents = 0;
            int faultEvents = 0;
            sampler.SampleCaptured += delegate
            {
                Interlocked.Increment(ref cheapEvents);
            };
            sampler.HeavySampleCaptured += delegate
            {
                Interlocked.Increment(ref heavyEvents);
            };
            sampler.SamplingFaulted += delegate
            {
                Interlocked.Increment(ref faultEvents);
            };

            Task running = sampler.StartAsync(target, CancellationToken.None);
            Assert(
                source.HeavyEntered.Wait(TimeSpan.FromSeconds(2)),
                "The deterministic blocking heavy source was not entered.");
            Assert(
                SpinWait.SpinUntil(
                    delegate { return source.CheapCaptures >= 15; },
                    TimeSpan.FromSeconds(2)),
                "The cadence loop did not advance while heavy work was blocked.");
            Assert(
                source.HeavyCaptures == 1 && source.MaximumConcurrentHeavy == 1,
                "Blocked heavy work overlapped another expensive capture.");

            var stopTimer = Stopwatch.StartNew();
            Task stopped = sampler.StopAsync();
            Assert(
                stopped.Wait(TimeSpan.FromSeconds(1)),
                "StopAsync waited for blocked heavy work.");
            stopTimer.Stop();
            Assert(
                stopTimer.Elapsed < TimeSpan.FromSeconds(1),
                "StopAsync exceeded its bounded shutdown budget.");
            int cheapAfterStop = Volatile.Read(ref cheapEvents);
            int heavyAfterStop = Volatile.Read(ref heavyEvents);
            int faultsAfterStop = Volatile.Read(ref faultEvents);

            var disposeTimer = Stopwatch.StartNew();
            sampler.Dispose();
            disposeTimer.Stop();
            Assert(
                disposeTimer.Elapsed < TimeSpan.FromSeconds(1),
                "Dispose waited for blocked heavy work.");
            Assert(
                !source.Disposed && !source.DisposedWhileHeavy,
                "The metrics source was disposed while heavy work still owned it.");

            source.ReleaseHeavy.Set();
            Assert(
                source.DisposedSignal.Wait(TimeSpan.FromSeconds(2)),
                "Owned source disposal was not completed after heavy work ended.");
            Assert(
                source.Disposed && !source.DisposedWhileHeavy,
                "Deferred source disposal violated the heavy-work lifetime.");
            Assert(
                Volatile.Read(ref cheapEvents) == cheapAfterStop &&
                Volatile.Read(ref heavyEvents) == heavyAfterStop &&
                Volatile.Read(ref faultEvents) == faultsAfterStop,
                "A sample or event was published after Stop/Dispose.");
            Assert(
                source.HeavyCaptures == 1 && source.MaximumConcurrentHeavy == 1,
                "Heavy sampling was not single-flight.");
        }

        private static void TestDefensiveValidation()
        {
            AssertThrows(delegate { new BoundedRingBuffer<int>(0); });
            AssertThrows(delegate
            {
                new SessionGuardSamplerOptions(
                    TimeSpan.FromMilliseconds(999),
                    TimeSpan.FromSeconds(10),
                    10);
            });
            AssertThrows(delegate
            {
                new SessionGuardTargetIdentity(
                    42,
                    DateTime.UtcNow,
                    "relative-game.exe");
            });
            AssertThrows(delegate
            {
                new SessionGuardSamplerOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(9),
                    10);
            });
            AssertThrows(delegate
            {
                new SessionGuardSample(
                    DateTime.UtcNow,
                    1,
                    true,
                    8 * GiB,
                    9 * GiB,
                    2 * GiB,
                    10 * GiB,
                    false,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    String.Empty);
            });
        }

        private static SessionGuardPressurePolicy CreatePolicy()
        {
            return new SessionGuardPressurePolicy(
                new SessionGuardPressurePolicyOptions(
                    3,
                    2,
                    TimeSpan.FromSeconds(30)));
        }

        private static SessionGuardSample CreateCriticalSample(int second)
        {
            return CreateSample(
                second,
                400L * 1024L * 1024L,
                10 * GiB);
        }

        private static SessionGuardSample CreateHealthySample(int second)
        {
            return CreateSample(second, 4 * GiB, 12 * GiB);
        }

        private static SessionGuardSample CreateHysteresisBandSample(int second)
        {
            return CreateSample(second, 1200L * 1024L * 1024L, 3 * GiB);
        }

        private static SessionGuardSample CreateSample(
            int second,
            long availablePhysical,
            long commitHeadroom)
        {
            long commitLimit = 32 * GiB;
            return new SessionGuardSample(
                DateTime.UtcNow.AddSeconds(second),
                Seconds(second),
                true,
                16 * GiB,
                availablePhysical,
                commitLimit - commitHeadroom,
                commitLimit,
                true,
                42,
                2 * GiB,
                3 * GiB,
                25.0,
                1024,
                2048,
                String.Empty);
        }

        private static PagefileConfigurationSnapshot CreatePagefile(
            PagefileConfigurationKind kind,
            long freeBytes)
        {
            return new PagefileConfigurationSnapshot(
                DateTime.UtcNow,
                kind,
                8 * GiB,
                8 * GiB,
                16 * GiB,
                freeBytes,
                200 * GiB,
                kind == PagefileConfigurationKind.Unknown ? "query failed" : "");
        }

        private static long Seconds(int seconds)
        {
            return Stopwatch.Frequency * (long)seconds;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertThrows(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                return;
            }

            throw new InvalidOperationException(
                "Expected argument validation did not reject invalid input.");
        }

        private sealed class FakeClock : ISessionGuardClock
        {
            private readonly CancellationTokenSource cancellation;
            private readonly int cancelAfterDelayCount;
            private long timestamp;
            private int delayCount;

            public FakeClock(
                CancellationTokenSource cancellation,
                int cancelAfterDelayCount)
            {
                this.cancellation = cancellation;
                this.cancelAfterDelayCount = cancelAfterDelayCount;
            }

            public DateTime UtcNow
            {
                get
                {
                    return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(timestamp / (double)Stopwatch.Frequency);
                }
            }

            public long Timestamp
            {
                get { return timestamp; }
            }

            public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                timestamp += (long)Math.Ceiling(
                    delay.TotalSeconds * Stopwatch.Frequency);
                delayCount++;
                if (delayCount >= cancelAfterDelayCount)
                {
                    cancellation.Cancel();
                }
                return Task.Delay(1, cancellationToken);
            }
        }

        private sealed class FakeMetricsSource : ISessionGuardMetricsSource
        {
            private readonly FakeClock clock;

            public FakeMetricsSource(FakeClock clock)
            {
                this.clock = clock;
                HeavyTimestamps = new List<long>();
            }

            public int CheapCaptures { get; private set; }
            public List<long> HeavyTimestamps { get; private set; }
            public bool Disposed { get; private set; }

            public long[] GetHeavyTimestamps()
            {
                lock (HeavyTimestamps)
                {
                    return HeavyTimestamps.ToArray();
                }
            }

            public SessionGuardSample CaptureCheap(SessionGuardTargetIdentity target)
            {
                CheapCaptures++;
                return new SessionGuardSample(
                    clock.UtcNow,
                    clock.Timestamp,
                    true,
                    16 * GiB,
                    4 * GiB,
                    20 * GiB,
                    32 * GiB,
                    true,
                    target.ProcessId,
                    2 * GiB,
                    3 * GiB,
                    10.0,
                    100,
                    100,
                    String.Empty);
            }

            public SessionGuardHeavySample CaptureHeavy()
            {
                lock (HeavyTimestamps)
                {
                    HeavyTimestamps.Add(clock.Timestamp);
                }
                return new SessionGuardHeavySample(
                    clock.UtcNow,
                    clock.Timestamp,
                    CreatePagefile(
                        PagefileConfigurationKind.SystemManaged,
                        40 * GiB));
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private sealed class YieldingClock : ISessionGuardClock
        {
            private long timestamp;

            public DateTime UtcNow
            {
                get
                {
                    return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(Timestamp / (double)Stopwatch.Frequency);
                }
            }

            public long Timestamp
            {
                get { return Interlocked.Read(ref timestamp); }
            }

            public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Add(
                    ref timestamp,
                    (long)Math.Ceiling(
                        delay.TotalSeconds * Stopwatch.Frequency));
                return Task.Delay(1, cancellationToken);
            }
        }

        private sealed class BlockingMetricsSource : ISessionGuardMetricsSource
        {
            private readonly YieldingClock clock;
            private int cheapCaptures;
            private int heavyCaptures;
            private int concurrentHeavy;
            private int maximumConcurrentHeavy;
            private int disposed;
            private int disposedWhileHeavy;

            public BlockingMetricsSource(YieldingClock clock)
            {
                this.clock = clock;
                HeavyEntered = new ManualResetEventSlim(false);
                ReleaseHeavy = new ManualResetEventSlim(false);
                DisposedSignal = new ManualResetEventSlim(false);
            }

            public ManualResetEventSlim HeavyEntered { get; private set; }
            public ManualResetEventSlim ReleaseHeavy { get; private set; }
            public ManualResetEventSlim DisposedSignal { get; private set; }
            public int CheapCaptures { get { return Volatile.Read(ref cheapCaptures); } }
            public int HeavyCaptures { get { return Volatile.Read(ref heavyCaptures); } }
            public int MaximumConcurrentHeavy
            {
                get { return Volatile.Read(ref maximumConcurrentHeavy); }
            }
            public bool Disposed { get { return Volatile.Read(ref disposed) != 0; } }
            public bool DisposedWhileHeavy
            {
                get { return Volatile.Read(ref disposedWhileHeavy) != 0; }
            }

            public SessionGuardSample CaptureCheap(SessionGuardTargetIdentity target)
            {
                Interlocked.Increment(ref cheapCaptures);
                return new SessionGuardSample(
                    clock.UtcNow,
                    clock.Timestamp,
                    true,
                    16 * GiB,
                    4 * GiB,
                    20 * GiB,
                    32 * GiB,
                    true,
                    target.ProcessId,
                    2 * GiB,
                    3 * GiB,
                    10.0,
                    100,
                    100,
                    String.Empty);
            }

            public SessionGuardHeavySample CaptureHeavy()
            {
                Interlocked.Increment(ref heavyCaptures);
                int concurrent = Interlocked.Increment(ref concurrentHeavy);
                UpdateMaximum(ref maximumConcurrentHeavy, concurrent);
                HeavyEntered.Set();
                ReleaseHeavy.Wait();
                Interlocked.Decrement(ref concurrentHeavy);
                return new SessionGuardHeavySample(
                    clock.UtcNow,
                    clock.Timestamp,
                    CreatePagefile(
                        PagefileConfigurationKind.SystemManaged,
                        40 * GiB));
            }

            public void Dispose()
            {
                if (Volatile.Read(ref concurrentHeavy) != 0)
                {
                    Interlocked.Exchange(ref disposedWhileHeavy, 1);
                }
                Interlocked.Exchange(ref disposed, 1);
                DisposedSignal.Set();
            }

            private static void UpdateMaximum(ref int target, int candidate)
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref target);
                    if (observed >= candidate)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    observed) != observed);
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
        '/reference:System.Management.dll',
        "/out:$assemblyPath",
        $sourcePath,
        $harnessPath
    )
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Session Guard regression harness compilation failed with exit code $LASTEXITCODE."
    }

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($assemblyPath))
    $type = $assembly.GetType('Boostix.SessionGuardRegressionHarness', $true)
    $method = $type.GetMethod(
        'Run',
        [Reflection.BindingFlags]::Public -bor
            [Reflection.BindingFlags]::NonPublic -bor
            [Reflection.BindingFlags]::Static)
    if (-not $method) {
        throw 'The Session Guard regression entry point was not compiled.'
    }
    $result = [string]$method.Invoke($null, @())
    Write-Host $result
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
