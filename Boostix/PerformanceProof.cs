using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Boostix
{
    internal enum PerformanceProofVariant
    {
        Baseline,
        Boost
    }

    internal enum PerformanceProofStatus
    {
        Completed,
        InvalidContext,
        TargetMismatch,
        ContextMismatch,
        InsufficientPairs,
        InsufficientFrames,
        InsufficientDuration,
        InvalidPairOrder
    }

    internal enum PerformanceProofVerdict
    {
        Neutral,
        Positive,
        Negative
    }

    internal sealed class PerformanceProofContext
    {
        public string ExecutablePath;
        public string ExecutableSha256;
        public string ExecutableVersion;
        public long ExecutableLength;
        public string ScenarioId;
        public int DisplayWidth;
        public int DisplayHeight;
        public double RefreshRateHz;
        public string DisplayMode;
        public string GraphicsPreset;
        public string GraphicsAdapterId;
        public bool VSyncEnabled;
        public int FrameLimit;

        public string BuildExecutableIdentity()
        {
            string normalizedPath = NormalizeExecutablePath(ExecutablePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return string.Empty;
            }

            string normalizedHash = NormalizeSha256(ExecutableSha256);
            if (!string.IsNullOrWhiteSpace(ExecutableSha256) &&
                string.IsNullOrEmpty(normalizedHash))
            {
                return string.Empty;
            }

            string canonical =
                "PATH=" + normalizedPath +
                "|SHA256=" + (string.IsNullOrEmpty(normalizedHash) ? "UNKNOWN" : normalizedHash) +
                "|VERSION=" + NormalizeToken(ExecutableVersion, 96) +
                "|LENGTH=" + Math.Max(0L, ExecutableLength).ToString(CultureInfo.InvariantCulture);
            return "target-v1:" + ComputeSha256(canonical);
        }

        public string BuildContextKey()
        {
            string targetIdentity = BuildExecutableIdentity();
            string scenario = NormalizeToken(ScenarioId, 128);
            if (string.IsNullOrEmpty(targetIdentity) ||
                string.IsNullOrEmpty(scenario) ||
                DisplayWidth < 320 ||
                DisplayHeight < 200 ||
                double.IsNaN(RefreshRateHz) ||
                double.IsInfinity(RefreshRateHz) ||
                RefreshRateHz < 10.0 ||
                RefreshRateHz > 1000.0 ||
                FrameLimit < 0 ||
                FrameLimit > 2000)
            {
                return string.Empty;
            }

            string canonical =
                "TARGET=" + targetIdentity +
                "|SCENARIO=" + scenario +
                "|DISPLAY=" + DisplayWidth.ToString(CultureInfo.InvariantCulture) +
                "X" + DisplayHeight.ToString(CultureInfo.InvariantCulture) +
                "@" + RefreshRateHz.ToString("0.###", CultureInfo.InvariantCulture) +
                "|MODE=" + NormalizeToken(DisplayMode, 48) +
                "|PRESET=" + NormalizeToken(GraphicsPreset, 96) +
                "|ADAPTER=" + NormalizeToken(GraphicsAdapterId, 128) +
                "|VSYNC=" + (VSyncEnabled ? "1" : "0") +
                "|LIMIT=" + FrameLimit.ToString(CultureInfo.InvariantCulture);
            return "proof-v1:" + ComputeSha256(canonical);
        }

        private static string NormalizeExecutablePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            try
            {
                string candidate = value.Trim().Trim('"');
                string fullPath = Path.GetFullPath(candidate);
                if (!Path.IsPathRooted(fullPath) ||
                    !string.Equals(
                        Path.GetExtension(fullPath),
                        ".exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return fullPath
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .ToUpperInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeSha256(string value)
        {
            string candidate = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (candidate.Length != 64)
            {
                return string.Empty;
            }

            foreach (char character in candidate)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return string.Empty;
                }
            }

            return candidate;
        }

        private static string NormalizeToken(string value, int maximumLength)
        {
            string candidate = (value ?? string.Empty).Trim();
            if (candidate.Length > maximumLength)
            {
                candidate = candidate.Substring(0, maximumLength);
            }

            var builder = new StringBuilder(candidate.Length);
            bool previousWasSpace = false;
            foreach (char character in candidate)
            {
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                {
                    if (!previousWasSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                        previousWasSpace = true;
                    }
                    continue;
                }

                builder.Append(char.ToUpperInvariant(character));
                previousWasSpace = false;
            }

            return builder.ToString().Trim();
        }

        private static string ComputeSha256(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                byte[] hash = algorithm.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash)
                {
                    builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }
    }

    internal sealed class PerformanceProofRun
    {
        public PerformanceProofRun()
        {
            FrameTimesMs = new List<double>();
        }

        public string RunId;
        public string TargetIdentity;
        public string ContextKey;
        public PerformanceProofVariant Variant;
        public DateTime CapturedUtc;
        public double CaptureDurationSeconds;
        public List<double> FrameTimesMs;
    }

    internal sealed class PerformanceProofPair
    {
        public string PairId;
        public PerformanceProofRun First;
        public PerformanceProofRun Second;
    }

    internal sealed class PerformanceFrameMetrics
    {
        public int FrameCount;
        public int RejectedFrameCount;
        public double AverageFps;
        public double OnePercentLowFps;
        public double P95FrameTimeMs;
        public double P99FrameTimeMs;
        public int FramesOver33Ms;
        public int FramesOver50Ms;
        public int FramesOver100Ms;

        public double FramesOver33PerThousand
        {
            get { return RatePerThousand(FramesOver33Ms); }
        }

        public double FramesOver50PerThousand
        {
            get { return RatePerThousand(FramesOver50Ms); }
        }

        public double FramesOver100PerThousand
        {
            get { return RatePerThousand(FramesOver100Ms); }
        }

        private double RatePerThousand(int count)
        {
            return FrameCount <= 0
                ? 0.0
                : count * 1000.0 / FrameCount;
        }
    }

    internal sealed class PerformanceProofResult
    {
        public PerformanceProofResult()
        {
            Verdict = PerformanceProofVerdict.Neutral;
            Status = PerformanceProofStatus.InsufficientPairs;
            Summary = string.Empty;
            PairAverageFpsDeltasPercent = new List<double>();
        }

        public PerformanceProofStatus Status;
        public PerformanceProofVerdict Verdict;
        public bool Available;
        public bool Conclusive;
        public string Summary;
        public string TargetIdentity;
        public string ContextKey;
        public int PairCount;
        public PerformanceFrameMetrics Baseline;
        public PerformanceFrameMetrics Boost;
        public double AverageFpsDelta;
        public double AverageFpsDeltaPercent;
        public double OnePercentLowFpsDelta;
        public double P95FrameTimeReductionMs;
        public double P99FrameTimeReductionMs;
        public double FramesOver33PerThousandReduction;
        public double FramesOver50PerThousandReduction;
        public double FramesOver100PerThousandReduction;
        public double VariabilityPercent;
        public double RequiredEffectPercent;
        public List<double> PairAverageFpsDeltasPercent;
    }

    internal static class PerformanceProofService
    {
        internal const int MinimumPairs = 2;
        internal const int MinimumFramesPerRun = 300;
        internal const double MinimumDurationSeconds = 50.0;
        internal const double MinimumPracticalEffectPercent = 1.0;

        private static readonly double[] TwoSided95StudentTCritical =
        {
            0.0,
            12.706,
            4.303,
            3.182,
            2.776,
            2.571,
            2.447,
            2.365,
            2.306,
            2.262,
            2.228,
            2.201,
            2.179,
            2.160,
            2.145,
            2.131,
            2.120,
            2.110,
            2.101,
            2.093,
            2.086,
            2.080,
            2.074,
            2.069,
            2.064,
            2.060,
            2.056,
            2.052,
            2.048,
            2.045,
            2.042
        };

        public static PerformanceFrameMetrics CalculateMetrics(IList<double> frameTimesMs)
        {
            var metrics = new PerformanceFrameMetrics();
            if (frameTimesMs == null)
            {
                return metrics;
            }

            var valid = new List<double>(frameTimesMs.Count);
            foreach (double frameTime in frameTimesMs)
            {
                if (double.IsNaN(frameTime) ||
                    double.IsInfinity(frameTime) ||
                    frameTime <= 0.0 ||
                    frameTime > 10000.0)
                {
                    metrics.RejectedFrameCount++;
                    continue;
                }

                valid.Add(frameTime);
            }

            metrics.FrameCount = valid.Count;
            if (valid.Count == 0)
            {
                return metrics;
            }

            double totalFrameTime = 0.0;
            foreach (double frameTime in valid)
            {
                totalFrameTime += frameTime;
                if (frameTime > 33.0)
                {
                    metrics.FramesOver33Ms++;
                }
                if (frameTime > 50.0)
                {
                    metrics.FramesOver50Ms++;
                }
                if (frameTime > 100.0)
                {
                    metrics.FramesOver100Ms++;
                }
            }

            valid.Sort();
            double meanFrameTime = totalFrameTime / valid.Count;
            metrics.AverageFps = 1000.0 / meanFrameTime;
            metrics.P95FrameTimeMs = NearestRankPercentile(valid, 0.95);
            metrics.P99FrameTimeMs = NearestRankPercentile(valid, 0.99);

            int slowFrameCount = Math.Max(1, (int)Math.Ceiling(valid.Count * 0.01));
            double slowFrameTotal = 0.0;
            for (int index = valid.Count - slowFrameCount; index < valid.Count; index++)
            {
                slowFrameTotal += valid[index];
            }
            metrics.OnePercentLowFps = 1000.0 / (slowFrameTotal / slowFrameCount);
            return metrics;
        }

        public static PerformanceProofResult ComparePairs(
            IList<PerformanceProofPair> pairs)
        {
            var result = new PerformanceProofResult();
            if (pairs == null || pairs.Count < MinimumPairs)
            {
                result.Summary = "Недостаточно пар A/B для доказательного сравнения.";
                return result;
            }

            string expectedTarget = string.Empty;
            string expectedContext = string.Empty;
            bool hasBaselineThenBoost = false;
            bool hasBoostThenBaseline = false;
            var baselineMetrics = new List<PerformanceFrameMetrics>();
            var boostMetrics = new List<PerformanceFrameMetrics>();

            foreach (PerformanceProofPair pair in pairs)
            {
                if (pair == null || pair.First == null || pair.Second == null ||
                    pair.First.Variant == pair.Second.Variant)
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.InvalidPairOrder,
                        "Каждая пара должна содержать один замер без Boost и один замер с Boost.");
                }

                if (pair.First.Variant == PerformanceProofVariant.Baseline)
                {
                    hasBaselineThenBoost = true;
                }
                else
                {
                    hasBoostThenBaseline = true;
                }

                PerformanceProofRun baseline = pair.First.Variant == PerformanceProofVariant.Baseline
                    ? pair.First
                    : pair.Second;
                PerformanceProofRun boost = pair.First.Variant == PerformanceProofVariant.Boost
                    ? pair.First
                    : pair.Second;

                if (string.IsNullOrWhiteSpace(baseline.TargetIdentity) ||
                    string.IsNullOrWhiteSpace(boost.TargetIdentity) ||
                    string.IsNullOrWhiteSpace(baseline.ContextKey) ||
                    string.IsNullOrWhiteSpace(boost.ContextKey))
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.InvalidContext,
                        "Не удалось подтвердить исполняемый файл и контекст замера.");
                }

                if (!string.Equals(
                        baseline.TargetIdentity.Trim(),
                        boost.TargetIdentity.Trim(),
                        StringComparison.Ordinal))
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.TargetMismatch,
                        "Замеры относятся к разным исполняемым файлам.");
                }

                if (!string.Equals(
                        baseline.ContextKey.Trim(),
                        boost.ContextKey.Trim(),
                        StringComparison.Ordinal))
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.ContextMismatch,
                        "Разрешение, сценарий или графические параметры замеров различаются.");
                }

                if (string.IsNullOrEmpty(expectedTarget))
                {
                    expectedTarget = baseline.TargetIdentity.Trim();
                    expectedContext = baseline.ContextKey.Trim();
                }
                else if (!string.Equals(expectedTarget, baseline.TargetIdentity.Trim(), StringComparison.Ordinal))
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.TargetMismatch,
                        "Пары относятся к разным исполняемым файлам.");
                }
                else if (!string.Equals(expectedContext, baseline.ContextKey.Trim(), StringComparison.Ordinal))
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.ContextMismatch,
                        "Пары были записаны в разных сценариях.");
                }

                if (baseline.CaptureDurationSeconds < MinimumDurationSeconds ||
                    boost.CaptureDurationSeconds < MinimumDurationSeconds)
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.InsufficientDuration,
                        "Каждый этап должен длиться не менее 50 секунд.");
                }

                PerformanceFrameMetrics baselineValue = CalculateMetrics(baseline.FrameTimesMs);
                PerformanceFrameMetrics boostValue = CalculateMetrics(boost.FrameTimesMs);
                if (baselineValue.FrameCount < MinimumFramesPerRun ||
                    boostValue.FrameCount < MinimumFramesPerRun)
                {
                    return Refuse(
                        result,
                        PerformanceProofStatus.InsufficientFrames,
                        "В одном из этапов недостаточно корректных кадров.");
                }

                baselineMetrics.Add(baselineValue);
                boostMetrics.Add(boostValue);
                result.PairAverageFpsDeltasPercent.Add(
                    RelativeDelta(boostValue.AverageFps, baselineValue.AverageFps));
            }

            if (!hasBaselineThenBoost || !hasBoostThenBaseline)
            {
                return Refuse(
                    result,
                    PerformanceProofStatus.InvalidPairOrder,
                    "Для защиты от прогрева нужны обе последовательности: A→B и B→A.");
            }

            result.Status = PerformanceProofStatus.Completed;
            result.Available = true;
            result.TargetIdentity = expectedTarget;
            result.ContextKey = expectedContext;
            result.PairCount = pairs.Count;
            result.Baseline = AverageMetrics(baselineMetrics);
            result.Boost = AverageMetrics(boostMetrics);
            result.AverageFpsDelta = result.Boost.AverageFps - result.Baseline.AverageFps;
            // Preserve the paired design: a ratio of aggregate FPS values can
            // overweight a pair solely because its baseline FPS is different.
            result.AverageFpsDeltaPercent = Mean(
                result.PairAverageFpsDeltasPercent);
            result.OnePercentLowFpsDelta =
                result.Boost.OnePercentLowFps - result.Baseline.OnePercentLowFps;
            result.P95FrameTimeReductionMs =
                result.Baseline.P95FrameTimeMs - result.Boost.P95FrameTimeMs;
            result.P99FrameTimeReductionMs =
                result.Baseline.P99FrameTimeMs - result.Boost.P99FrameTimeMs;
            result.FramesOver33PerThousandReduction =
                result.Baseline.FramesOver33PerThousand - result.Boost.FramesOver33PerThousand;
            result.FramesOver50PerThousandReduction =
                result.Baseline.FramesOver50PerThousand - result.Boost.FramesOver50PerThousand;
            result.FramesOver100PerThousandReduction =
                result.Baseline.FramesOver100PerThousand - result.Boost.FramesOver100PerThousand;

            double pairedStandardError = StandardDeviation(
                result.PairAverageFpsDeltasPercent) /
                Math.Sqrt(result.PairAverageFpsDeltasPercent.Count);
            result.VariabilityPercent = StudentTCritical95(
                result.PairAverageFpsDeltasPercent.Count) *
                pairedStandardError;
            // A conclusion requires the entire 95% interval to clear the
            // minimum practically meaningful effect, not merely zero.
            result.RequiredEffectPercent =
                result.VariabilityPercent +
                MinimumPracticalEffectPercent;

            double agreement = SignAgreement(
                result.PairAverageFpsDeltasPercent,
                result.AverageFpsDeltaPercent);
            double[] tailSignals =
            {
                result.OnePercentLowFpsDelta,
                result.P95FrameTimeReductionMs,
                result.P99FrameTimeReductionMs,
                result.FramesOver33PerThousandReduction,
                result.FramesOver50PerThousandReduction,
                result.FramesOver100PerThousandReduction
            };
            int positiveTailSignals = CountSigns(tailSignals, true);
            int negativeTailSignals = CountSigns(tailSignals, false);
            bool positiveTailSignal =
                positiveTailSignals >= 2 && positiveTailSignals > negativeTailSignals;
            bool negativeTailSignal =
                negativeTailSignals >= 2 && negativeTailSignals > positiveTailSignals;

            if (result.AverageFpsDeltaPercent > result.RequiredEffectPercent &&
                agreement >= 0.75 &&
                positiveTailSignal)
            {
                result.Verdict = PerformanceProofVerdict.Positive;
                result.Conclusive = true;
                result.Summary =
                    "В этом повторяемом сценарии замеры с Boost были устойчиво лучше базовых.";
            }
            else if (result.AverageFpsDeltaPercent < -result.RequiredEffectPercent &&
                     agreement >= 0.75 &&
                     negativeTailSignal)
            {
                result.Verdict = PerformanceProofVerdict.Negative;
                result.Conclusive = true;
                result.Summary =
                    "В этом повторяемом сценарии замеры с Boost были устойчиво хуже базовых.";
            }
            else
            {
                result.Verdict = PerformanceProofVerdict.Neutral;
                result.Conclusive = false;
                result.Summary =
                    "Разница находится в пределах вариативности или метрики дают смешанный сигнал.";
            }

            return result;
        }

        private static PerformanceProofResult Refuse(
            PerformanceProofResult result,
            PerformanceProofStatus status,
            string summary)
        {
            result.Status = status;
            result.Verdict = PerformanceProofVerdict.Neutral;
            result.Available = false;
            result.Conclusive = false;
            result.Summary = summary ?? string.Empty;
            return result;
        }

        private static PerformanceFrameMetrics AverageMetrics(
            IList<PerformanceFrameMetrics> values)
        {
            var result = new PerformanceFrameMetrics();
            if (values == null || values.Count == 0)
            {
                return result;
            }

            foreach (PerformanceFrameMetrics value in values)
            {
                result.FrameCount += value.FrameCount;
                result.RejectedFrameCount += value.RejectedFrameCount;
                result.AverageFps += value.AverageFps;
                result.OnePercentLowFps += value.OnePercentLowFps;
                result.P95FrameTimeMs += value.P95FrameTimeMs;
                result.P99FrameTimeMs += value.P99FrameTimeMs;
                result.FramesOver33Ms += value.FramesOver33Ms;
                result.FramesOver50Ms += value.FramesOver50Ms;
                result.FramesOver100Ms += value.FramesOver100Ms;
            }

            result.AverageFps /= values.Count;
            result.OnePercentLowFps /= values.Count;
            result.P95FrameTimeMs /= values.Count;
            result.P99FrameTimeMs /= values.Count;
            return result;
        }

        private static double NearestRankPercentile(
            IList<double> sortedValues,
            double percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0.0;
            }

            int rank = (int)Math.Ceiling(percentile * sortedValues.Count);
            int index = Math.Max(0, Math.Min(sortedValues.Count - 1, rank - 1));
            return sortedValues[index];
        }

        private static double RelativeDelta(double current, double baseline)
        {
            return Math.Abs(baseline) < 0.000001
                ? 0.0
                : (current - baseline) * 100.0 / baseline;
        }

        private static double Mean(IList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0.0;
            }

            double sum = 0.0;
            foreach (double value in values)
            {
                sum += value;
            }
            return sum / values.Count;
        }

        private static double StudentTCritical95(int sampleCount)
        {
            int degreesOfFreedom = sampleCount - 1;
            if (degreesOfFreedom <= 0)
            {
                return double.PositiveInfinity;
            }
            if (degreesOfFreedom < TwoSided95StudentTCritical.Length)
            {
                return TwoSided95StudentTCritical[degreesOfFreedom];
            }
            // 2.042 (df=30) is conservative for every larger finite sample;
            // it approaches 1.96 from above without understating uncertainty.
            return TwoSided95StudentTCritical[
                TwoSided95StudentTCritical.Length - 1];
        }

        private static double StandardDeviation(IList<double> values)
        {
            if (values == null || values.Count < 2)
            {
                return 0.0;
            }

            double mean = 0.0;
            foreach (double value in values)
            {
                mean += value;
            }
            mean /= values.Count;

            double sum = 0.0;
            foreach (double value in values)
            {
                double difference = value - mean;
                sum += difference * difference;
            }
            return Math.Sqrt(sum / (values.Count - 1));
        }

        private static double SignAgreement(IList<double> values, double expected)
        {
            if (values == null || values.Count == 0 || Math.Abs(expected) < 0.000001)
            {
                return 0.0;
            }

            int matching = 0;
            foreach (double value in values)
            {
                if ((expected > 0.0 && value > 0.0) ||
                    (expected < 0.0 && value < 0.0))
                {
                    matching++;
                }
            }
            return matching / (double)values.Count;
        }

        private static int CountSigns(IList<double> values, bool positive)
        {
            if (values == null)
            {
                return 0;
            }

            int count = 0;
            foreach (double value in values)
            {
                if ((positive && value > 0.000001) ||
                    (!positive && value < -0.000001))
                {
                    count++;
                }
            }
            return count;
        }
    }
}
