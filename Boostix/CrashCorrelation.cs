using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Boostix
{
    internal enum CrashEventKind
    {
        Unknown,
        ApplicationCrash,
        WindowsErrorReporting,
        ApplicationHang
    }

    internal enum CrashCorrelationStatus
    {
        NotApplicable,
        InvalidTarget,
        ProviderUnavailable,
        NoEvidence,
        Correlated
    }

    internal sealed class CrashCorrelationTarget
    {
        public int ProcessId;
        public string ProcessName;
        public DateTime StartedUtc;
        public DateTime ExitedUtc;
        public int? ExitCode;
        public bool ExpectedExit;
    }

    internal sealed class CrashEventQuery
    {
        public int ProcessId;
        public string ProcessName;
        public DateTime FromUtc;
        public DateTime ToUtc;
    }

    internal sealed class CrashEventRecord
    {
        public CrashEventKind Kind;
        public DateTime TimeCreatedUtc;
        public int? ProcessId;
        public string ProcessName;
        public string EventSource;
        public int EventId;
        public string ExceptionCode;
        public string FaultingModule;
        public string FaultOffset;
        public string Message;
    }

    internal interface ICrashEventProvider
    {
        IList<CrashEventRecord> Read(CrashEventQuery query);
    }

    internal sealed class CrashCorrelationResult
    {
        public CrashCorrelationResult()
        {
            Status = CrashCorrelationStatus.NoEvidence;
            EvidenceOnly = true;
            Summary = string.Empty;
            ExceptionCode = string.Empty;
            FaultingModule = string.Empty;
            FaultOffset = string.Empty;
            Message = string.Empty;
            EventSource = string.Empty;
            ProcessName = string.Empty;
        }

        public CrashCorrelationStatus Status;
        public bool EvidenceOnly;
        public string Summary;
        public int ProcessId;
        public string ProcessName;
        public DateTime EventUtc;
        public string EventSource;
        public int EventId;
        public string ExceptionCode;
        public string FaultingModule;
        public string FaultOffset;
        public string Message;
    }

    internal sealed class CrashCorrelationService
    {
        private readonly ICrashEventProvider _provider;

        public CrashCorrelationService(ICrashEventProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider");
            }
            _provider = provider;
        }

        public CrashCorrelationResult Correlate(CrashCorrelationTarget target)
        {
            CrashEventQuery query;
            CrashCorrelationResult validation = CrashEventCorrelator.ValidateAndBuildQuery(
                target,
                out query);
            if (validation != null)
            {
                return validation;
            }

            IList<CrashEventRecord> records;
            try
            {
                records = _provider.Read(query);
            }
            catch
            {
                return new CrashCorrelationResult
                {
                    Status = CrashCorrelationStatus.ProviderUnavailable,
                    Summary = "Не удалось прочитать журнал Windows. Причина завершения не определена."
                };
            }

            return CrashEventCorrelator.Correlate(target, records);
        }
    }

    /// <summary>
    /// Reads a small, time-bounded slice of the local Application event log.
    /// It performs no log mutation and returns raw evidence for the pure
    /// correlator, which applies the exact process/time checks.
    /// </summary>
    internal sealed class WindowsCrashEventProvider : ICrashEventProvider
    {
        private const int MaximumEntries = 256;

        public IList<CrashEventRecord> Read(CrashEventQuery query)
        {
            if (query == null)
            {
                throw new ArgumentNullException("query");
            }
            var results = new List<CrashEventRecord>();
            using (var log = new EventLog("Application"))
            {
                for (int index = log.Entries.Count - 1;
                     index >= 0 && results.Count < MaximumEntries;
                     index--)
                {
                    EventLogEntry entry = log.Entries[index];
                    DateTime eventUtc = entry.TimeGenerated.ToUniversalTime();
                    if (eventUtc > query.ToUtc)
                    {
                        continue;
                    }
                    if (eventUtc < query.FromUtc)
                    {
                        break;
                    }

                    int eventId = unchecked((int)(entry.InstanceId & 0xFFFF));
                    CrashEventKind kind = GetKind(entry.Source, eventId);
                    if (kind == CrashEventKind.Unknown)
                    {
                        continue;
                    }

                    string[] values;
                    try { values = entry.ReplacementStrings ?? new string[0]; }
                    catch { values = new string[0]; }
                    string processName = FindProcessName(values, query.ProcessName);
                    if (!string.Equals(
                            NormalizeName(processName),
                            NormalizeName(query.ProcessName),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var record = new CrashEventRecord
                    {
                        Kind = kind,
                        TimeCreatedUtc = eventUtc,
                        ProcessName = processName,
                        EventSource = entry.Source,
                        EventId = eventId
                    };
                    if (kind == CrashEventKind.ApplicationCrash)
                    {
                        record.FaultingModule = Get(values, 3);
                        record.ExceptionCode = Get(values, 6);
                        record.FaultOffset = Get(values, 7);
                        int processId;
                        if (TryParseProcessId(Get(values, 8), out processId))
                        {
                            record.ProcessId = processId;
                        }
                    }
                    try { record.Message = entry.Message; }
                    catch { record.Message = string.Empty; }
                    results.Add(record);
                }
            }
            return results;
        }

        private static CrashEventKind GetKind(string source, int eventId)
        {
            if (eventId == 1000 && string.Equals(
                    source,
                    "Application Error",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CrashEventKind.ApplicationCrash;
            }
            if (eventId == 1001 && string.Equals(
                    source,
                    "Windows Error Reporting",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CrashEventKind.WindowsErrorReporting;
            }
            if (eventId == 1002 && string.Equals(
                    source,
                    "Application Hang",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CrashEventKind.ApplicationHang;
            }
            return CrashEventKind.Unknown;
        }

        private static string FindProcessName(string[] values, string expected)
        {
            string expectedName = NormalizeName(expected);
            foreach (string value in values ?? new string[0])
            {
                if (string.Equals(
                        NormalizeName(value),
                        expectedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        private static string NormalizeName(string value)
        {
            try
            {
                return Path.GetFileNameWithoutExtension(
                    Path.GetFileName((value ?? string.Empty).Trim().Trim('"')));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Get(string[] values, int index)
        {
            return values != null && index >= 0 && index < values.Length
                ? values[index]
                : string.Empty;
        }

        private static bool TryParseProcessId(string value, out int processId)
        {
            processId = 0;
            string candidate = (value ?? string.Empty).Trim();
            bool hexadecimal = candidate.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase);
            if (hexadecimal)
            {
                candidate = candidate.Substring(2);
            }
            int parsed;
            bool parsedSuccessfully = hexadecimal
                ? int.TryParse(
                    candidate,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out parsed)
                : int.TryParse(
                    candidate,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed);
            if (parsedSuccessfully &&
                parsed > 0)
            {
                processId = parsed;
                return true;
            }
            return false;
        }
    }

    internal static class CrashEventCorrelator
    {
        internal const int SecondsBeforeExit = 15;
        internal const int SecondsAfterExit = 30;
        internal const int NameOnlySecondsFromExit = 5;
        internal const int MaximumMessageLength = 512;
        internal const int MaximumEventSourceLength = 96;
        internal const int MaximumProcessNameLength = 128;
        internal const int MaximumModuleNameLength = 160;
        internal const int MaximumEvidenceInputLength = 4096;

        private static readonly Regex QuotedPathPattern = new Regex(
            "\"[A-Za-z]:\\\\[^\"\\r\\n]+\"",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex UnquotedPathPattern = new Regex(
            "(?i)\\b[A-Z]:\\\\[^\\r\\n\"<>|;,]*",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex NetworkPathPattern = new Regex(
            "\\\\\\\\[^\\r\\n\"<>|;,]*",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex EmailPattern = new Regex(
            "(?i)\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static CrashCorrelationResult Correlate(
            CrashCorrelationTarget target,
            IList<CrashEventRecord> records)
        {
            CrashEventQuery query;
            CrashCorrelationResult validation = ValidateAndBuildQuery(target, out query);
            if (validation != null)
            {
                return validation;
            }

            CrashEventRecord best = null;
            int bestScore = int.MinValue;
            double bestDistance = double.MaxValue;
            if (records != null)
            {
                foreach (CrashEventRecord record in records)
                {
                    int score;
                    double distance;
                    if (!TryScore(record, target, query, out score, out distance))
                    {
                        continue;
                    }

                    if (best == null || score > bestScore ||
                        (score == bestScore && distance < bestDistance))
                    {
                        best = record;
                        bestScore = score;
                        bestDistance = distance;
                    }
                }
            }

            if (best == null)
            {
                return new CrashCorrelationResult
                {
                    Status = CrashCorrelationStatus.NoEvidence,
                    ProcessId = target.ProcessId,
                    ProcessName = NormalizeProcessName(target.ProcessName),
                    Summary =
                        "Рядом с завершением выбранного процесса не найдено подходящее событие Windows. " +
                        "Причина завершения не определена."
                };
            }

            return new CrashCorrelationResult
            {
                Status = CrashCorrelationStatus.Correlated,
                ProcessId = target.ProcessId,
                ProcessName = NormalizeProcessName(target.ProcessName),
                EventUtc = NormalizeUtc(best.TimeCreatedUtc),
                EventSource = SanitizeEvidenceText(
                    best.EventSource,
                    MaximumEventSourceLength),
                EventId = best.EventId,
                ExceptionCode = NormalizeHex(best.ExceptionCode, 16),
                FaultingModule = NormalizeModule(best.FaultingModule),
                FaultOffset = NormalizeHex(best.FaultOffset, 16),
                Message = SanitizeMessage(best.Message),
                Summary =
                    "Найдено совпадающее по процессу и времени событие Windows. " +
                    "Оно является свидетельством события, а не автоматическим диагнозом."
            };
        }

        internal static CrashCorrelationResult ValidateAndBuildQuery(
            CrashCorrelationTarget target,
            out CrashEventQuery query)
        {
            query = null;
            if (target == null ||
                target.ProcessId <= 0 ||
                string.IsNullOrEmpty(NormalizeProcessName(target.ProcessName)) ||
                target.StartedUtc == DateTime.MinValue ||
                target.ExitedUtc == DateTime.MinValue)
            {
                return new CrashCorrelationResult
                {
                    Status = CrashCorrelationStatus.InvalidTarget,
                    Summary = "Недостаточно данных для сопоставления завершившегося процесса."
                };
            }

            DateTime startedUtc = NormalizeUtc(target.StartedUtc);
            DateTime exitedUtc = NormalizeUtc(target.ExitedUtc);
            if (exitedUtc < startedUtc)
            {
                return new CrashCorrelationResult
                {
                    Status = CrashCorrelationStatus.InvalidTarget,
                    Summary = "Время завершения процесса предшествует времени запуска."
                };
            }

            if (target.ExpectedExit || (target.ExitCode.HasValue && target.ExitCode.Value == 0))
            {
                return new CrashCorrelationResult
                {
                    Status = CrashCorrelationStatus.NotApplicable,
                    ProcessId = target.ProcessId,
                    ProcessName = NormalizeProcessName(target.ProcessName),
                    Summary = "Процесс завершился штатно; поиск аварии не выполнялся."
                };
            }

            DateTime fromUtc = exitedUtc.AddSeconds(-SecondsBeforeExit);
            if (fromUtc < startedUtc)
            {
                fromUtc = startedUtc;
            }
            query = new CrashEventQuery
            {
                ProcessId = target.ProcessId,
                ProcessName = NormalizeProcessName(target.ProcessName),
                FromUtc = fromUtc,
                ToUtc = exitedUtc.AddSeconds(SecondsAfterExit)
            };
            return null;
        }

        private static bool TryScore(
            CrashEventRecord record,
            CrashCorrelationTarget target,
            CrashEventQuery query,
            out int score,
            out double distance)
        {
            score = 0;
            distance = double.MaxValue;
            if (record == null || record.TimeCreatedUtc == DateTime.MinValue)
            {
                return false;
            }

            DateTime eventUtc = NormalizeUtc(record.TimeCreatedUtc);
            if (eventUtc < query.FromUtc || eventUtc > query.ToUtc)
            {
                return false;
            }

            string targetName = NormalizeProcessName(target.ProcessName);
            string eventName = NormalizeProcessName(record.ProcessName);
            if (string.IsNullOrEmpty(eventName) ||
                !string.Equals(targetName, eventName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            distance = Math.Abs((eventUtc - NormalizeUtc(target.ExitedUtc)).TotalSeconds);
            if (record.ProcessId.HasValue)
            {
                if (record.ProcessId.Value != target.ProcessId)
                {
                    return false;
                }
                score += 100;
            }
            else if (distance > NameOnlySecondsFromExit)
            {
                return false;
            }

            if (!IsRecognizedCrashRecord(record))
            {
                return false;
            }

            score += Math.Max(0, 30 - (int)Math.Round(distance));
            if (!string.IsNullOrEmpty(NormalizeHex(record.ExceptionCode, 16)))
            {
                score += 25;
            }
            if (!string.IsNullOrEmpty(NormalizeModule(record.FaultingModule)))
            {
                score += 15;
            }
            if (!string.IsNullOrEmpty(NormalizeHex(record.FaultOffset, 16)))
            {
                score += 5;
            }
            if (record.Kind == CrashEventKind.ApplicationCrash)
            {
                score += 10;
            }
            else if (record.Kind == CrashEventKind.WindowsErrorReporting)
            {
                score += 5;
            }
            return true;
        }

        private static bool IsRecognizedCrashRecord(CrashEventRecord record)
        {
            if (record.Kind == CrashEventKind.ApplicationCrash ||
                record.Kind == CrashEventKind.WindowsErrorReporting ||
                record.Kind == CrashEventKind.ApplicationHang)
            {
                return true;
            }

            string source = (record.EventSource ?? string.Empty).Trim();
            if (record.EventId == 1000 &&
                source.Equals("Application Error", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (record.EventId == 1001 &&
                source.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return record.EventId == 1002 &&
                   source.Equals("Application Hang", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }
            if (value.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
            return value.ToUniversalTime();
        }

        private static string NormalizeProcessName(string value)
        {
            string candidate = LimitInput(value, MaximumEvidenceInputLength);
            try
            {
                candidate = Path.GetFileName(candidate.Trim().Trim('"'));
                candidate = Path.GetFileNameWithoutExtension(candidate);
            }
            catch
            {
                candidate = string.Empty;
            }
            return SanitizeText(candidate, MaximumProcessNameLength);
        }

        private static string NormalizeModule(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string candidate;
            try
            {
                candidate = Path.GetFileName(
                    LimitInput(value, MaximumEvidenceInputLength)
                        .Trim()
                        .Trim('"'));
            }
            catch
            {
                candidate = string.Empty;
            }
            return SanitizeText(candidate, MaximumModuleNameLength);
        }

        private static string NormalizeHex(string value, int maximumDigits)
        {
            string candidate = LimitInput(value, maximumDigits + 4)
                .Trim()
                .ToUpperInvariant();
            if (candidate.StartsWith("0X", StringComparison.Ordinal))
            {
                candidate = candidate.Substring(2);
            }
            if (candidate.Length == 0 || candidate.Length > maximumDigits)
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
            return "0X" + candidate;
        }

        private static string SanitizeMessage(string value)
        {
            return SanitizeEvidenceText(value, MaximumMessageLength);
        }

        private static string SanitizeEvidenceText(
            string value,
            int maximumLength)
        {
            string candidate = LimitInput(value, MaximumEvidenceInputLength);
            candidate = QuotedPathPattern.Replace(candidate, "[path]");
            candidate = NetworkPathPattern.Replace(candidate, "[network path]");
            candidate = UnquotedPathPattern.Replace(candidate, "[path]");
            candidate = EmailPattern.Replace(candidate, "[email]");
            return SanitizeText(candidate, maximumLength);
        }

        private static string LimitInput(string value, int maximumLength)
        {
            string candidate = value ?? string.Empty;
            if (maximumLength <= 0)
            {
                return string.Empty;
            }
            return candidate.Length <= maximumLength
                ? candidate
                : candidate.Substring(0, maximumLength);
        }

        private static string SanitizeText(string value, int maximumLength)
        {
            string candidate = value ?? string.Empty;
            var builder = new StringBuilder(Math.Min(candidate.Length, maximumLength));
            bool previousWasSpace = false;
            foreach (char character in candidate)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(
                    character);
                if (category == UnicodeCategory.Format ||
                    category == UnicodeCategory.Surrogate)
                {
                    continue;
                }
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                {
                    if (!previousWasSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                        previousWasSpace = true;
                    }
                    continue;
                }

                builder.Append(character);
                previousWasSpace = false;
                if (builder.Length >= maximumLength)
                {
                    break;
                }
            }
            return builder.ToString().Trim();
        }
    }
}
