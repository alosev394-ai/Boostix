[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This test must run in Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$proofSource = Join-Path $projectRoot 'Boostix\PerformanceProof.cs'
$crashSource = Join-Path $projectRoot 'Boostix\CrashCorrelation.cs'
foreach ($sourcePath in @($proofSource, $crashSource)) {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required source was not found: $sourcePath"
    }
}

$frameworkFolders = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319')
)
$compiler = $frameworkFolders |
    ForEach-Object { Join-Path $_ 'csc.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($compiler)) {
    throw '.NET Framework C# compiler was not found.'
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-ProofCrash-' + [Guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $tempRoot 'ProofCrashHarness.cs'
$outputPath = Join-Path $tempRoot 'ProofCrashHarness.exe'

$harnessSource = @'
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Boostix
{
    internal static class ProofCrashHarness
    {
        private sealed class FakeProvider : ICrashEventProvider
        {
            internal IList<CrashEventRecord> Records;
            internal CrashEventQuery LastQuery;
            internal bool ThrowOnRead;

            public IList<CrashEventRecord> Read(CrashEventQuery query)
            {
                LastQuery = query;
                if (ThrowOnRead)
                {
                    throw new InvalidOperationException("provider details must not escape");
                }
                return Records;
            }
        }

        public static int Main()
        {
            TestContextIdentity();
            TestFrameMetrics();
            TestPairedEffectAndSmallSampleUncertainty();
            TestPositiveAndNegativeProof();
            TestProofRefusalsAndNoise();
            TestCrashSelectionAndSanitization();
            TestCrashRefusalsAndProvider();
            TestWindowsEventProcessIdParsing();
            Console.WriteLine("PASS: performance proof and crash correlation contracts are valid.");
            return 0;
        }

        private static void TestContextIdentity()
        {
            PerformanceProofContext first = CreateContext(@"C:\Games\Sample\game.exe");
            PerformanceProofContext normalized = CreateContext(@"c:/games/sample/./GAME.EXE");
            string target = first.BuildExecutableIdentity();
            string key = first.BuildContextKey();
            Assert(target.StartsWith("target-v1:", StringComparison.Ordinal), "Target identity version is missing.");
            Assert(key.StartsWith("proof-v1:", StringComparison.Ordinal), "Context key version is missing.");
            Assert(target == normalized.BuildExecutableIdentity(), "Executable path normalization is not stable.");
            Assert(key == normalized.BuildContextKey(), "Equivalent display/scenario metadata changed the key.");
            Assert(key.IndexOf("GAMES", StringComparison.OrdinalIgnoreCase) < 0, "Context key leaked the executable path.");

            normalized.DisplayWidth = 2560;
            Assert(key != normalized.BuildContextKey(), "Resolution must be part of the exact context.");
            normalized = CreateContext(@"C:\Games\Sample\game.exe");
            normalized.ScenarioId = "another route";
            Assert(key != normalized.BuildContextKey(), "Scenario must be part of the exact context.");
            normalized = CreateContext(@"C:\Games\Sample\game.exe");
            normalized.ExecutableSha256 = "not-a-hash";
            Assert(normalized.BuildContextKey() == string.Empty, "Invalid executable hash must invalidate context.");
        }

        private static PerformanceProofContext CreateContext(string path)
        {
            return new PerformanceProofContext
            {
                ExecutablePath = path,
                ExecutableSha256 = new string('A', 64),
                ExecutableVersion = "2.0.0",
                ExecutableLength = 123456,
                ScenarioId = "  Repeatable   Route  ",
                DisplayWidth = 1920,
                DisplayHeight = 1080,
                RefreshRateHz = 143.9996,
                DisplayMode = "Borderless",
                GraphicsPreset = "High",
                GraphicsAdapterId = "GPU-001",
                VSyncEnabled = false,
                FrameLimit = 144
            };
        }

        private static void TestFrameMetrics()
        {
            var frames = new List<double>();
            for (int index = 0; index < 990; index++)
            {
                frames.Add(10.0);
            }
            for (int index = 0; index < 10; index++)
            {
                frames.Add(100.0);
            }
            frames.Add(double.NaN);
            frames.Add(-1.0);

            PerformanceFrameMetrics metrics = PerformanceProofService.CalculateMetrics(frames);
            Assert(metrics.FrameCount == 1000, "Valid frame count is wrong.");
            Assert(metrics.RejectedFrameCount == 2, "Invalid frame count is wrong.");
            AssertNear(metrics.OnePercentLowFps, 10.0, 0.0001, "1% low must use the slowest one percent.");
            Assert(metrics.FramesOver33Ms == 10, "Frames >33 ms are wrong.");
            Assert(metrics.FramesOver50Ms == 10, "Frames >50 ms are wrong.");
            Assert(metrics.FramesOver100Ms == 0, "The >100 ms threshold must be strict.");
            AssertNear(metrics.P95FrameTimeMs, 10.0, 0.0001, "P95 is wrong.");
            AssertNear(metrics.P99FrameTimeMs, 10.0, 0.0001, "P99 is wrong.");
        }

        private static void TestPositiveAndNegativeProof()
        {
            List<PerformanceProofPair> positive = new List<PerformanceProofPair>
            {
                MakePair("p1", true, 16.0, 52.0, 13.0, 36.0),
                MakePair("p2", false, 17.0, 55.0, 13.5, 37.0)
            };
            PerformanceProofResult positiveResult = PerformanceProofService.ComparePairs(positive);
            Assert(positiveResult.Status == PerformanceProofStatus.Completed, "Positive proof did not complete.");
            Assert(positiveResult.Available && positiveResult.Conclusive, "Positive proof was not conclusive.");
            Assert(positiveResult.Verdict == PerformanceProofVerdict.Positive, "Positive proof verdict is wrong.");
            Assert(positiveResult.AverageFpsDelta > 0.0, "Positive FPS delta is missing.");
            Assert(positiveResult.P95FrameTimeReductionMs > 0.0, "Positive P95 reduction is missing.");
            Assert(positiveResult.PairCount == 2, "Pair count is wrong.");

            List<PerformanceProofPair> negative = new List<PerformanceProofPair>
            {
                MakePair("n1", true, 13.0, 36.0, 16.0, 52.0),
                MakePair("n2", false, 13.5, 37.0, 17.0, 55.0)
            };
            PerformanceProofResult negativeResult = PerformanceProofService.ComparePairs(negative);
            Assert(negativeResult.Status == PerformanceProofStatus.Completed, "Negative proof did not complete.");
            Assert(negativeResult.Conclusive, "Negative proof was not conclusive.");
            Assert(negativeResult.Verdict == PerformanceProofVerdict.Negative, "Negative proof verdict is wrong.");
            Assert(negativeResult.AverageFpsDelta < 0.0, "Negative FPS delta is missing.");
        }

        private static void TestPairedEffectAndSmallSampleUncertainty()
        {
            var unequalBaselines = new List<PerformanceProofPair>
            {
                MakeConstantFpsPair("paired-1", true, 50.0, 60.0),
                MakeConstantFpsPair("paired-2", false, 100.0, 110.0)
            };
            PerformanceProofResult result =
                PerformanceProofService.ComparePairs(unequalBaselines);

            Assert(result.Status == PerformanceProofStatus.Completed,
                "Paired regression fixture did not complete.");
            Assert(result.PairAverageFpsDeltasPercent.Count == 2,
                "Paired effects were not retained.");
            AssertNear(
                result.PairAverageFpsDeltasPercent[0],
                20.0,
                0.000001,
                "First paired percentage is wrong.");
            AssertNear(
                result.PairAverageFpsDeltasPercent[1],
                10.0,
                0.000001,
                "Second paired percentage is wrong.");
            AssertNear(
                result.AverageFpsDeltaPercent,
                15.0,
                0.000001,
                "Reported percentage must be the mean of paired percentages.");

            double ratioOfAggregates =
                (result.Boost.AverageFps - result.Baseline.AverageFps) *
                100.0 /
                result.Baseline.AverageFps;
            AssertNear(
                ratioOfAggregates,
                13.3333333333,
                0.000001,
                "Regression fixture no longer distinguishes paired and aggregate ratios.");
            Assert(Math.Abs(
                result.AverageFpsDeltaPercent - ratioOfAggregates) > 1.0,
                "Aggregate-ratio bias leaked into the paired effect.");

            AssertNear(
                result.VariabilityPercent,
                63.53,
                0.000001,
                "Two-pair uncertainty must use t(0.975, df=1) = 12.706.");
            AssertNear(
                result.RequiredEffectPercent,
                64.53,
                0.000001,
                "Practical noise floor must be added above uncertainty.");
            Assert(!result.Conclusive &&
                   result.Verdict == PerformanceProofVerdict.Neutral,
                "Two inconsistent positive pairs produced false confidence.");

            var repeatable = new List<PerformanceProofPair>
            {
                MakeConstantFpsPair("repeat-1", true, 50.0, 60.0),
                MakeConstantFpsPair("repeat-2", false, 50.0, 60.0)
            };
            PerformanceProofResult repeatableResult =
                PerformanceProofService.ComparePairs(repeatable);
            AssertNear(
                repeatableResult.VariabilityPercent,
                0.0,
                0.000001,
                "Identical paired effects must have zero observed spread.");
            AssertNear(
                repeatableResult.RequiredEffectPercent,
                PerformanceProofService.MinimumPracticalEffectPercent,
                0.000001,
                "Zero spread must not remove the practical noise floor.");
            Assert(repeatableResult.Conclusive &&
                   repeatableResult.Verdict == PerformanceProofVerdict.Positive,
                "A large repeatable effect should remain conclusive.");

            var belowFloor = new List<PerformanceProofPair>
            {
                MakeConstantFpsPair("floor-1", true, 50.0, 50.4),
                MakeConstantFpsPair("floor-2", false, 50.0, 50.4)
            };
            PerformanceProofResult belowFloorResult =
                PerformanceProofService.ComparePairs(belowFloor);
            AssertNear(
                belowFloorResult.AverageFpsDeltaPercent,
                0.8,
                0.000001,
                "Noise-floor fixture effect is wrong.");
            Assert(!belowFloorResult.Conclusive &&
                   belowFloorResult.Verdict == PerformanceProofVerdict.Neutral,
                "Sub-one-percent effect must stay neutral even with zero spread.");
        }

        private static void TestProofRefusalsAndNoise()
        {
            List<PerformanceProofPair> onePair = new List<PerformanceProofPair>
            {
                MakePair("one", true, 16.0, 50.0, 14.0, 40.0)
            };
            PerformanceProofResult insufficientPairs = PerformanceProofService.ComparePairs(onePair);
            Assert(insufficientPairs.Status == PerformanceProofStatus.InsufficientPairs, "Single pair was accepted.");
            Assert(insufficientPairs.Verdict == PerformanceProofVerdict.Neutral, "Refusal must stay neutral.");

            List<PerformanceProofPair> mismatch = new List<PerformanceProofPair>
            {
                MakePair("m1", true, 16.0, 50.0, 14.0, 40.0),
                MakePair("m2", false, 16.0, 50.0, 14.0, 40.0)
            };
            mismatch[0].Second.ContextKey = "proof-v1:different";
            PerformanceProofResult contextMismatch = PerformanceProofService.ComparePairs(mismatch);
            Assert(contextMismatch.Status == PerformanceProofStatus.ContextMismatch, "Context mismatch was accepted.");
            Assert(!contextMismatch.Available && !contextMismatch.Conclusive, "Mismatch exposed a conclusion.");

            mismatch = new List<PerformanceProofPair>
            {
                MakePair("t1", true, 16.0, 50.0, 14.0, 40.0),
                MakePair("t2", false, 16.0, 50.0, 14.0, 40.0)
            };
            mismatch[0].Second.TargetIdentity = "target-v1:different";
            PerformanceProofResult targetMismatch = PerformanceProofService.ComparePairs(mismatch);
            Assert(targetMismatch.Status == PerformanceProofStatus.TargetMismatch, "Target mismatch was accepted.");

            List<PerformanceProofPair> shortFrames = new List<PerformanceProofPair>
            {
                MakePair("s1", true, 16.0, 50.0, 14.0, 40.0),
                MakePair("s2", false, 16.0, 50.0, 14.0, 40.0)
            };
            shortFrames[0].First.FrameTimesMs.RemoveRange(100, 500);
            PerformanceProofResult insufficientFrames = PerformanceProofService.ComparePairs(shortFrames);
            Assert(insufficientFrames.Status == PerformanceProofStatus.InsufficientFrames, "Short capture was accepted.");

            List<PerformanceProofPair> noise = new List<PerformanceProofPair>
            {
                MakePair("v1", true, 16.0, 45.0, 15.0, 44.0),
                MakePair("v2", false, 16.0, 45.0, 17.0, 46.0)
            };
            PerformanceProofResult noiseResult = PerformanceProofService.ComparePairs(noise);
            Assert(noiseResult.Status == PerformanceProofStatus.Completed && noiseResult.Available, "Noise comparison did not complete.");
            Assert(!noiseResult.Conclusive && noiseResult.Verdict == PerformanceProofVerdict.Neutral, "Noise produced a fake conclusion.");
            Assert(noiseResult.RequiredEffectPercent >= noiseResult.VariabilityPercent, "Variability threshold was not applied.");
        }

        private static PerformanceProofPair MakePair(
            string id,
            bool baselineFirst,
            double baselineRegular,
            double baselineSlow,
            double boostRegular,
            double boostSlow)
        {
            PerformanceProofRun baseline = MakeRun(
                id + "-a",
                PerformanceProofVariant.Baseline,
                baselineRegular,
                baselineSlow);
            PerformanceProofRun boost = MakeRun(
                id + "-b",
                PerformanceProofVariant.Boost,
                boostRegular,
                boostSlow);
            return new PerformanceProofPair
            {
                PairId = id,
                First = baselineFirst ? baseline : boost,
                Second = baselineFirst ? boost : baseline
            };
        }

        private static PerformanceProofPair MakeConstantFpsPair(
            string id,
            bool baselineFirst,
            double baselineFps,
            double boostFps)
        {
            return MakePair(
                id,
                baselineFirst,
                1000.0 / baselineFps,
                1000.0 / baselineFps,
                1000.0 / boostFps,
                1000.0 / boostFps);
        }

        private static PerformanceProofRun MakeRun(
            string id,
            PerformanceProofVariant variant,
            double regular,
            double slow)
        {
            var run = new PerformanceProofRun
            {
                RunId = id,
                TargetIdentity = "target-v1:fixture",
                ContextKey = "proof-v1:fixture",
                Variant = variant,
                CapturedUtc = DateTime.UtcNow,
                CaptureDurationSeconds = 60.0
            };
            for (int index = 0; index < 600; index++)
            {
                run.FrameTimesMs.Add(index % 100 == 0 ? slow : regular);
            }
            return run;
        }

        private static void TestCrashSelectionAndSanitization()
        {
            DateTime exit = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
            CrashCorrelationTarget target = CreateCrashTarget(exit);
            var records = new List<CrashEventRecord>
            {
                new CrashEventRecord
                {
                    Kind = CrashEventKind.ApplicationCrash,
                    TimeCreatedUtc = exit.AddSeconds(1),
                    ProcessId = 9999,
                    ProcessName = "game.exe",
                    ExceptionCode = "DEADBEEF",
                    FaultingModule = "wrong.dll"
                },
                new CrashEventRecord
                {
                    Kind = CrashEventKind.ApplicationCrash,
                    TimeCreatedUtc = exit,
                    ProcessId = 4242,
                    ProcessName = "other.exe",
                    ExceptionCode = "DEADBEEF",
                    FaultingModule = "wrong.dll"
                },
                new CrashEventRecord
                {
                    Kind = CrashEventKind.ApplicationCrash,
                    TimeCreatedUtc = exit.AddMinutes(-2),
                    ProcessId = 4242,
                    ProcessName = "game.exe",
                    ExceptionCode = "DEADBEEF",
                    FaultingModule = "old.dll"
                },
                new CrashEventRecord
                {
                    Kind = CrashEventKind.WindowsErrorReporting,
                    TimeCreatedUtc = exit.AddSeconds(1),
                    ProcessName = "game.exe",
                    EventSource = "Windows Error Reporting",
                    EventId = 1001,
                    Message = "generic"
                },
                new CrashEventRecord
                {
                    Kind = CrashEventKind.ApplicationCrash,
                    TimeCreatedUtc = exit.AddSeconds(2),
                    ProcessId = 4242,
                    ProcessName = @"C:\Games\game.exe",
                    EventSource = @"C:\Users\alice\source.bin Application Error " +
                        "alice@example.com " + new string('s', 5000),
                    EventId = 1000,
                    ExceptionCode = "c0000005",
                    FaultingModule = @"C:\Users\alice\Mods\fault.dll",
                    FaultOffset = "0000aBcD",
                    Message = "Failure at \"C:\\Users\\alice\\private.bin\" " +
                        @"and C:\Users\Alice Smith\Private Files\secret.bin; marker " +
                        "for alice@example.com\r\n\u202E\0" + new string('x', 10000)
                }
            };

            CrashCorrelationResult result = CrashEventCorrelator.Correlate(target, records);
            Assert(result.Status == CrashCorrelationStatus.Correlated, "Exact crash event was not correlated.");
            Assert(result.ExceptionCode == "0XC0000005", "Exception code was not normalized.");
            Assert(result.FaultOffset == "0X0000ABCD", "Fault offset was not normalized.");
            Assert(result.FaultingModule == "fault.dll", "Fault module path was not reduced to a file name.");
            Assert(result.Message.Length <= CrashEventCorrelator.MaximumMessageLength, "Crash message was not bounded.");
            Assert(result.Message.IndexOf("alice", StringComparison.OrdinalIgnoreCase) < 0, "Private path or email leaked.");
            Assert(result.Message.IndexOf('@') < 0, "Email was not sanitized.");
            Assert(result.Message.IndexOf("secret", StringComparison.OrdinalIgnoreCase) < 0, "Unquoted path with spaces leaked.");
            Assert(result.Message.IndexOf('\u202E') < 0 && result.Message.IndexOf('\0') < 0, "Format or NUL characters were not removed.");
            Assert(result.Message.IndexOf('\r') < 0 && result.Message.IndexOf('\n') < 0, "Control characters were not collapsed.");
            Assert(result.EventSource.Length <= CrashEventCorrelator.MaximumEventSourceLength, "Event source was not bounded.");
            Assert(result.EventSource.IndexOf("alice", StringComparison.OrdinalIgnoreCase) < 0, "Event source leaked a private path or email.");
            Assert(result.EventSource.IndexOf('@') < 0, "Event source leaked an email.");
            Assert(result.EvidenceOnly, "Evidence was presented as an automatic diagnosis.");
        }

        private static void TestWindowsEventProcessIdParsing()
        {
            MethodInfo method = typeof(WindowsCrashEventProvider).GetMethod(
                "TryParseProcessId",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert(method != null, "Windows PID parser was not found.");

            AssertParsedProcessId(method, "4242", true, 4242);
            AssertParsedProcessId(method, "0x4242", true, 0x4242);
            AssertParsedProcessId(method, "0X00001092", true, 4242);
            AssertParsedProcessId(method, "DEAD", false, 0);
            AssertParsedProcessId(method, "-1", false, 0);
            AssertParsedProcessId(method, "0", false, 0);
            AssertParsedProcessId(method, "0x", false, 0);
        }

        private static void AssertParsedProcessId(
            MethodInfo method,
            string value,
            bool expectedSuccess,
            int expectedProcessId)
        {
            object[] arguments = { value, 0 };
            bool success = (bool)method.Invoke(null, arguments);
            int processId = (int)arguments[1];
            Assert(success == expectedSuccess, "PID parser success state is wrong for " + value + ".");
            Assert(processId == expectedProcessId, "PID parser value is wrong for " + value + ".");
        }

        private static void TestCrashRefusalsAndProvider()
        {
            DateTime exit = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
            CrashCorrelationTarget target = CreateCrashTarget(exit);
            CrashCorrelationResult noEvidence = CrashEventCorrelator.Correlate(
                target,
                new List<CrashEventRecord>
                {
                    new CrashEventRecord
                    {
                        Kind = CrashEventKind.Unknown,
                        TimeCreatedUtc = exit,
                        ProcessId = 4242,
                        ProcessName = "game.exe",
                        EventSource = "Unrelated source",
                        EventId = 99
                    },
                    new CrashEventRecord
                    {
                        Kind = CrashEventKind.ApplicationCrash,
                        TimeCreatedUtc = exit.AddSeconds(6),
                        ProcessName = "game.exe",
                        EventId = 1000,
                        EventSource = "Application Error"
                    }
                });
            Assert(noEvidence.Status == CrashCorrelationStatus.NoEvidence, "Noise event was correlated.");

            CrashCorrelationTarget expected = CreateCrashTarget(exit);
            expected.ExpectedExit = true;
            Assert(
                CrashEventCorrelator.Correlate(expected, null).Status == CrashCorrelationStatus.NotApplicable,
                "Expected exit was treated as a crash.");
            expected.ExpectedExit = false;
            expected.ExitCode = 0;
            Assert(
                CrashEventCorrelator.Correlate(expected, null).Status == CrashCorrelationStatus.NotApplicable,
                "Zero exit code was treated as a crash.");

            var provider = new FakeProvider
            {
                Records = new List<CrashEventRecord>
                {
                    new CrashEventRecord
                    {
                        Kind = CrashEventKind.ApplicationCrash,
                        TimeCreatedUtc = exit.AddSeconds(3),
                        ProcessName = "game.exe",
                        EventSource = "Application Error",
                        EventId = 1000,
                        ExceptionCode = "not-hex",
                        FaultingModule = "safe.dll"
                    }
                }
            };
            var service = new CrashCorrelationService(provider);
            CrashCorrelationResult serviceResult = service.Correlate(target);
            Assert(serviceResult.Status == CrashCorrelationStatus.Correlated, "Injected provider result was not used.");
            Assert(serviceResult.ExceptionCode == string.Empty, "Invalid crash code was exposed.");
            Assert(provider.LastQuery != null, "Provider did not receive a query.");
            Assert(provider.LastQuery.FromUtc >= target.ExitedUtc.AddSeconds(-15), "Provider query was too broad before exit.");
            Assert(provider.LastQuery.ToUtc <= target.ExitedUtc.AddSeconds(30), "Provider query was too broad after exit.");

            provider.ThrowOnRead = true;
            CrashCorrelationResult failed = service.Correlate(target);
            Assert(failed.Status == CrashCorrelationStatus.ProviderUnavailable, "Provider failure was not contained.");
            Assert(failed.Summary.IndexOf("provider details", StringComparison.OrdinalIgnoreCase) < 0, "Provider exception leaked.");
        }

        private static CrashCorrelationTarget CreateCrashTarget(DateTime exit)
        {
            return new CrashCorrelationTarget
            {
                ProcessId = 4242,
                ProcessName = @"C:\Games\game.exe",
                StartedUtc = exit.AddMinutes(-30),
                ExitedUtc = exit,
                ExitCode = -1073741819,
                ExpectedExit = false
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertNear(double actual, double expected, double tolerance, string message)
        {
            if (Math.Abs(actual - expected) > tolerance)
            {
                throw new InvalidOperationException(
                    message + " Expected " + expected + ", actual " + actual + ".");
            }
        }
    }
}
'@

try {
    [void](New-Item -ItemType Directory -Path $tempRoot -Force)
    [IO.File]::WriteAllText(
        $harnessPath,
        $harnessSource,
        (New-Object Text.UTF8Encoding($false)))

    $compilerOutput = & $compiler @(
        '/nologo',
        '/warn:0',
        '/target:exe',
        "/out:$outputPath",
        $proofSource,
        $crashSource,
        $harnessPath
    ) 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Proof/crash harness compilation failed:`n$($compilerOutput -join [Environment]::NewLine)"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $testOutput = & $outputPath 2>&1
    $testExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    if ($testExitCode -ne 0) {
        throw "Proof/crash harness failed:`n$($testOutput -join [Environment]::NewLine)"
    }
    $passLine = @($testOutput | Where-Object { $_ -like 'PASS:*' })
    if ($passLine.Count -ne 1) {
        throw "Proof/crash harness did not report exactly one PASS line:`n$($testOutput -join [Environment]::NewLine)"
    }
    $passLine[0]
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTarget = [IO.Path]::GetFullPath($tempRoot)
    if ($resolvedTarget.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTarget) -like 'Boostix-ProofCrash-*' -and
        (Test-Path -LiteralPath $resolvedTarget)) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
