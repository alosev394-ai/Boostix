using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Boostix;

internal static class PerformanceCaptureParserHarness
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Fixture path is required.");
            return 2;
        }

        PerformanceCaptureAttemptResult result =
            PerformanceCaptureService.ParseCaptureCsvForTesting(
                args[0],
                4242,
                "GTA5",
                DateTime.UtcNow);

        if (result.Status != PerformanceCaptureStatus.Completed ||
            result.Performance == null ||
            !result.Performance.Available)
        {
            Console.Error.WriteLine(
                "Parser failed: " + result.Status + " / " + result.Message);
            return 3;
        }

        BoostPerformanceResult metrics = result.Performance;
        AssertEqual("Frames", metrics.Frames, 650);
        AssertNear("AverageFps", metrics.AverageFps, 57.4204946996, 0.0001);
        // The slowest 1% is seven frames for this 650-frame fixture:
        // one 60 ms frame and six 120 ms frames. This deliberately differs
        // from 1000 / P99 (16.67 FPS).
        AssertNear("OnePercentLowFps", metrics.OnePercentLowFps, 8.9743589744, 0.0001);
        AssertNear("P95FrameTimeMs", metrics.P95FrameTimeMs, 16.0, 0.0001);
        AssertNear("P99FrameTimeMs", metrics.P99FrameTimeMs, 60.0, 0.0001);
        AssertEqual("FramesOver50Ms", metrics.FramesOver50Ms, 10);
        AssertEqual("FramesOver100Ms", metrics.FramesOver100Ms, 6);
        AssertComparisonRequiresExplicitContext();
        AssertGenericReportSchema();
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "PASS frames={0} avg={1:0.000} 1%low={2:0.000}",
                metrics.Frames,
                metrics.AverageFps,
                metrics.OnePercentLowFps));
        return 0;
    }

    private static void AssertGenericReportSchema()
    {
        var report = new BoostSessionReport
        {
            SessionId = Guid.NewGuid().ToString("N"),
            StartedUtc = DateTime.UtcNow,
            Status = "Completed",
            TargetName = "SampleApp",
            PeakTargetWorkingSetBytes = 123,
            PeakTargetPrivateBytes = 456,
            TargetCrashCode = "0xC0000005",
            TargetCrashModule = "sample.dll"
        };
        MethodInfo serialize = typeof(BoostSessionReportStore).GetMethod(
            "Serialize",
            BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo deserialize = typeof(BoostSessionReportStore).GetMethod(
            "Deserialize",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (serialize == null || deserialize == null)
        {
            throw new InvalidOperationException(
                "Session serialization methods were not found.");
        }

        string content = (string)serialize.Invoke(null, new object[] { report });
        foreach (string legacyKey in new[]
        {
            "PeakGameWorkingSetBytes=",
            "PeakGamePrivateBytes=",
            "GameCrashCode=",
            "GameCrashModule=",
            "GameCrashOffset=",
            "GameCrashUtc=",
            "GameName="
        })
        {
            if (content.IndexOf(
                    Environment.NewLine + legacyKey,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(
                    "A new report wrote legacy key " + legacyKey);
            }
        }

        string legacy = content
            .Replace("Version=4", "Version=3")
            .Replace(
                "PeakTargetWorkingSetBytes=",
                "PeakGameWorkingSetBytes=")
            .Replace(
                "PeakTargetPrivateBytes=",
                "PeakGamePrivateBytes=")
            .Replace("TargetCrashCode=", "GameCrashCode=")
            .Replace("TargetCrashModule=", "GameCrashModule=")
            .Replace("TargetCrashOffset=", "GameCrashOffset=")
            .Replace("TargetCrashUtc=", "GameCrashUtc=")
            .Replace("TargetName=", "GameName=");
        legacy = ReplaceLine(
            legacy,
            "Status=",
            "Status=" + Convert.ToBase64String(
                Encoding.UTF8.GetBytes("GameCrashed")));
        var migrated = (BoostSessionReport)deserialize.Invoke(
            null,
            new object[] { legacy.Split(new[] { "\r\n", "\n" },
                StringSplitOptions.None) });
        if (migrated == null ||
            migrated.TargetName != "SampleApp" ||
            migrated.PeakTargetPrivateBytes != 456 ||
            migrated.TargetCrashModule != "sample.dll" ||
            migrated.Status != "TargetCrashed")
        {
            throw new InvalidOperationException(
                "Legacy report keys were not migrated to Target fields.");
        }

        string diagnostic = DiagnosticExportBuilder.BuildSafeReport(
            null,
            new[] { migrated },
            null);
        if (!diagnostic.StartsWith(
                "BOOSTIX SAFE DIAGNOSTIC REPORT",
                StringComparison.Ordinal) ||
            diagnostic.IndexOf(
                "  TargetName=SampleApp",
                StringComparison.Ordinal) < 0 ||
            diagnostic.IndexOf(
                "GameCrash",
                StringComparison.OrdinalIgnoreCase) >= 0 ||
            diagnostic.IndexOf(
                "PeakGame",
                StringComparison.OrdinalIgnoreCase) >= 0 ||
            diagnostic.IndexOf(
                "MAJESTIC",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidOperationException(
                "The diagnostic export did not use the generic Boostix schema.");
        }
    }

    private static string ReplaceLine(
        string content,
        string prefix,
        string replacement)
    {
        string[] lines = content.Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.None);
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = replacement;
                break;
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void AssertComparisonRequiresExplicitContext()
    {
        DateTime now = DateTime.UtcNow;
        var previous = new BoostSessionReport
        {
            SessionId = Guid.NewGuid().ToString("N"),
            StartedUtc = now.AddMinutes(-2),
            Performance = NewPerformance("SampleGame", 90.0, null)
        };
        var current = new BoostSessionReport
        {
            SessionId = Guid.NewGuid().ToString("N"),
            StartedUtc = now.AddMinutes(-1),
            Performance = NewPerformance("samplegame.exe", 100.0, null)
        };
        var reports = new List<BoostSessionReport> { current, previous };

        BoostPerformanceComparison comparison =
            BoostSessionComparison.Compare(current, reports);
        if (comparison.Available)
        {
            throw new InvalidOperationException(
                "Process-name equality alone produced an FPS delta claim.");
        }

        previous.Performance.ComparisonContextKey = "repeatable-scene-v1";
        current.Performance.ComparisonContextKey = "repeatable-scene-v1";
        comparison = BoostSessionComparison.Compare(current, reports);
        if (!comparison.Available ||
            Math.Abs(comparison.AverageFpsDelta - 10.0) > 0.0001)
        {
            throw new InvalidOperationException(
                "Equal explicit comparison contexts were not matched.");
        }

        previous.Performance.ComparisonContextKey = "different-scene";
        if (BoostSessionComparison.Compare(current, reports).Available)
        {
            throw new InvalidOperationException(
                "Different comparison contexts produced an FPS delta claim.");
        }
    }

    private static BoostPerformanceResult NewPerformance(
        string processName,
        double averageFps,
        string comparisonContext)
    {
        return new BoostPerformanceResult
        {
            Available = true,
            Frames = 1000,
            ProcessName = processName,
            AverageFps = averageFps,
            ComparisonContextKey = comparisonContext
        };
    }

    private static void AssertEqual(string name, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                name + ": expected " + expected + ", actual " + actual + ".");
        }
    }

    private static void AssertNear(
        string name,
        double actual,
        double expected,
        double tolerance)
    {
        if (Math.Abs(actual - expected) > tolerance)
        {
            throw new InvalidOperationException(
                name + ": expected " +
                expected.ToString("R", CultureInfo.InvariantCulture) +
                ", actual " +
                actual.ToString("R", CultureInfo.InvariantCulture) +
                ".");
        }
    }
}
