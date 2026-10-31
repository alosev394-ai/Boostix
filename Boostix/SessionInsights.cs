using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Boostix
{
    internal enum BoostCrashCategory
    {
        None,
        MemoryPressure,
        GraphicsDevice,
        AccessViolation,
        CorruptedState,
        Unknown
    }

    internal sealed class BoostCrashInsight
    {
        public BoostCrashInsight()
        {
            Steps = new List<string>();
        }

        public BoostCrashCategory Category;
        public string Title;
        public string Summary;
        public string Evidence;
        public List<string> Steps;
    }

    internal static class BoostCrashAssistant
    {
        private static readonly HashSet<string> MemoryPressureCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "0XC0000017",
                "0XC000009A",
                "0XC000012D"
            };

        private static readonly HashSet<string> GraphicsDeviceCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "0X887A0005",
                "0X887A0006",
                "0X887A0007"
            };

        public static BoostCrashInsight Analyze(BoostSessionReport report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.TargetCrashCode))
            {
                return new BoostCrashInsight
                {
                    Category = BoostCrashCategory.None,
                    Title = L.T("АВАРИЙНОЕ ЗАВЕРШЕНИЕ НЕ ОБНАРУЖЕНО"),
                    Summary = L.T("В отчёте Windows нет подтверждённой записи об аварии отслеживаемого приложения."),
                    Evidence = string.Empty
                };
            }

            string code = NormalizeCode(report.TargetCrashCode);
            string module = NormalizeModule(report.TargetCrashModule);
            BoostCrashInsight insight;
            if (MemoryPressureCodes.Contains(code))
            {
                insight = BuildMemoryPressureInsight();
            }
            else if (GraphicsDeviceCodes.Contains(code))
            {
                insight = BuildGraphicsInsight();
            }
            else if (string.Equals(code, "0XC0000005", StringComparison.OrdinalIgnoreCase))
            {
                insight = BuildAccessViolationInsight(module);
            }
            else if (string.Equals(code, "0XC0000409", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(code, "0XC0000374", StringComparison.OrdinalIgnoreCase))
            {
                insight = BuildCorruptedStateInsight();
            }
            else if (IsGraphicsModule(module))
            {
                insight = BuildGraphicsInsight();
            }
            else
            {
                insight = BuildUnknownInsight();
            }

            insight.Evidence = BuildEvidence(code, module, report.TargetCrashOffset);
            return insight;
        }

        private static BoostCrashInsight BuildMemoryPressureInsight()
        {
            var insight = new BoostCrashInsight
            {
                Category = BoostCrashCategory.MemoryPressure,
                Title = L.T("ВОЗМОЖНА НЕХВАТКА COMMIT / ФАЙЛА ПОДКАЧКИ"),
                Summary =
                    L.T("Код Windows похож на отказ выделения памяти. Это не то же самое, ") +
                    L.T("что заполненная шкала RAM: процессу нужен запас commit, который зависит ") +
                    L.T("от физической памяти и файла подкачки.")
            };
            insight.Steps.Add(L.T("Оставьте файл подкачки в режиме «по выбору системы» и убедитесь, что на системном диске есть свободное место."));
            insight.Steps.Add(L.T("Закройте самые тяжёлые фоновые приложения и повторите тот же сценарий."));
            insight.Steps.Add(L.T("Если в отчёте резерв памяти опускался почти до нуля, освободите место на системном диске."));
            return insight;
        }

        private static BoostCrashInsight BuildGraphicsInsight()
        {
            var insight = new BoostCrashInsight
            {
                Category = BoostCrashCategory.GraphicsDevice,
                Title = L.T("СБОЙ ГРАФИЧЕСКОГО УСТРОЙСТВА ИЛИ ДРАЙВЕРА"),
                Summary =
                    L.T("Windows зафиксировал потерю, зависание или сброс графического устройства. ") +
                    L.T("Причиной могут быть драйвер, разгон, переполнение видеопамяти, ReShade или другой графический оверлей.")
            };
            insight.Steps.Add(L.T("Отключите разгон и графические оверлеи, затем повторите запуск."));
            insight.Steps.Add(L.T("Проверьте запас видеопамяти в отчёте и при необходимости снизьте качество текстур."));
            insight.Steps.Add(L.T("Если сбой повторяется без модов и оверлеев, выполните чистую установку стабильного драйвера видеокарты."));
            return insight;
        }

        private static BoostCrashInsight BuildAccessViolationInsight(string module)
        {
            var insight = new BoostCrashInsight
            {
                Category = BoostCrashCategory.AccessViolation,
                Title = L.T("НАРУШЕНИЕ ДОСТУПА К ПАМЯТИ"),
                Summary =
                    L.T("Код 0xC0000005 означает обращение к недопустимому адресу. Сам по себе ") +
                    L.T("он не доказывает нехватку RAM: частые причины — несовместимый мод, ") +
                    L.T("оверлей, повреждённый файл или ошибка драйвера.")
            };

            if (IsOverlayOrModModule(module))
            {
                insight.Steps.Add(L.T("Сначала отключите компонент, связанный с указанным модулем сбоя, и повторите ту же сцену."));
            }
            else
            {
                insight.Steps.Add(L.T("Временно отключите ReShade, моды и сторонние оверлеи, затем повторите ту же сцену."));
            }
            insight.Steps.Add(L.T("Проверьте файлы отслеживаемого приложения и его лаунчера штатным способом."));
            insight.Steps.Add(L.T("Если модуль относится к драйверу видеокарты, выполните чистую установку стабильной версии драйвера."));
            return insight;
        }

        private static BoostCrashInsight BuildCorruptedStateInsight()
        {
            var insight = new BoostCrashInsight
            {
                Category = BoostCrashCategory.CorruptedState,
                Title = L.T("ПОВРЕЖДЕНО СОСТОЯНИЕ ПРОЦЕССА"),
                Summary =
                    L.T("Windows остановил процесс после повреждения кучи или проверки защиты. ") +
                    L.T("Обычно это связано с несовместимым модулем, хуком, оверлеем или повреждённым файлом.")
            };
            insight.Steps.Add(L.T("Запустите отслеживаемое приложение без модов, ReShade и сторонних оверлеев."));
            insight.Steps.Add(L.T("Проверьте файлы отслеживаемого приложения и его лаунчера."));
            insight.Steps.Add(L.T("Если ошибка повторяется в чистом запуске, сохраните диагностический отчёт и сравните модуль сбоя между сессиями."));
            return insight;
        }

        private static BoostCrashInsight BuildUnknownInsight()
        {
            var insight = new BoostCrashInsight
            {
                Category = BoostCrashCategory.Unknown,
                Title = L.T("WINDOWS ЗАФИКСИРОВАЛ АВАРИЮ ПРИЛОЖЕНИЯ"),
                Summary =
                    L.T("По одному коду нельзя надёжно назвать причину. Сопоставьте модуль сбоя ") +
                    L.T("с показателями RAM, commit и видеопамяти из этой же сессии.")
            };
            insight.Steps.Add(L.T("Повторите тот же сценарий и проверьте, совпадает ли код и модуль сбоя."));
            insight.Steps.Add(L.T("Сохраните безопасный диагностический отчёт перед изменением драйверов или модов."));
            insight.Steps.Add(L.T("Отключайте по одному недавно добавленному моду или оверлею, чтобы найти воспроизводимую причину."));
            return insight;
        }

        private static string BuildEvidence(string code, string module, string offset)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(code))
            {
                parts.Add(L.T("код ") + code);
            }
            if (!string.IsNullOrEmpty(module))
            {
                parts.Add(L.T("модуль ") + Path.GetFileName(module));
            }
            if (!string.IsNullOrWhiteSpace(offset))
            {
                parts.Add(L.T("смещение ") + offset.Trim());
            }
            return parts.Count == 0
                ? L.T("Подробности события недоступны.")
                : "Windows: " + string.Join(", ", parts.ToArray()) + ".";
        }

        private static string NormalizeCode(string value)
        {
            string code = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (code.Length == 8 && IsHex(code))
            {
                return "0X" + code;
            }
            if (code.StartsWith("0X", StringComparison.Ordinal) &&
                code.Length == 10 &&
                IsHex(code.Substring(2)))
            {
                return code;
            }
            return code;
        }

        private static bool IsHex(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static string NormalizeModule(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetFileName(value.Trim());
            }
            catch
            {
                return value.Trim();
            }
        }

        private static bool IsGraphicsModule(string module)
        {
            string normalized = (module ?? string.Empty).ToLowerInvariant();
            return normalized.Contains("nvwgf") ||
                   normalized.Contains("atidxx") ||
                   normalized.Contains("amdxx") ||
                   normalized.Contains("igd") ||
                   normalized == "dxgi.dll" ||
                   normalized == "d3d11.dll" ||
                   normalized == "d3d12.dll";
        }

        private static bool IsOverlayOrModModule(string module)
        {
            string normalized = (module ?? string.Empty).ToLowerInvariant();
            return normalized.Contains("reshade") ||
                   normalized.Contains("discordhook") ||
                   normalized.Contains("gameoverlayrenderer") ||
                   normalized.Contains("rtss") ||
                   normalized.Contains("hook");
        }
    }

    internal sealed class BoostPerformanceComparison
    {
        public bool Available;
        public double AverageFpsDelta;
        public double OnePercentLowFpsDelta;
        public double P95FrameTimeDeltaMs;
        public int FramesOver50MsDelta;
        public string ComparedSessionId;
    }

    internal static class BoostSessionComparison
    {
        public static BoostPerformanceComparison Compare(
            BoostSessionReport current,
            IList<BoostSessionReport> recent)
        {
            var result = new BoostPerformanceComparison();
            if (!HasPerformance(current) || recent == null)
            {
                return result;
            }

            string currentIdentity = GetProcessIdentity(current);
            string currentContext = GetComparisonContextKey(current);
            if (string.IsNullOrEmpty(currentIdentity) ||
                string.IsNullOrEmpty(currentContext))
            {
                return result;
            }

            BoostSessionReport previous = null;
            foreach (BoostSessionReport candidate in recent)
            {
                if (!HasPerformance(candidate) ||
                    !string.Equals(
                        GetProcessIdentity(candidate),
                        currentIdentity,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        GetComparisonContextKey(candidate),
                        currentContext,
                        StringComparison.Ordinal) ||
                    ReferenceEquals(candidate, current) ||
                    string.Equals(
                        candidate.SessionId,
                        current.SessionId,
                        StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartedUtc >= current.StartedUtc)
                {
                    continue;
                }

                if (previous == null || candidate.StartedUtc > previous.StartedUtc)
                {
                    previous = candidate;
                }
            }

            if (previous == null)
            {
                return result;
            }

            result.Available = true;
            result.AverageFpsDelta =
                current.Performance.AverageFps - previous.Performance.AverageFps;
            result.OnePercentLowFpsDelta =
                current.Performance.OnePercentLowFps - previous.Performance.OnePercentLowFps;
            result.P95FrameTimeDeltaMs =
                current.Performance.P95FrameTimeMs - previous.Performance.P95FrameTimeMs;
            result.FramesOver50MsDelta =
                current.Performance.FramesOver50Ms - previous.Performance.FramesOver50Ms;
            result.ComparedSessionId = previous.SessionId;
            return result;
        }

        public static string FormatSigned(double value, string suffix)
        {
            string sign = value > 0 ? "+" : string.Empty;
            return sign + value.ToString("0.0", CultureInfo.InvariantCulture) + (suffix ?? string.Empty);
        }

        private static bool HasPerformance(BoostSessionReport report)
        {
            return report != null &&
                   report.Performance != null &&
                   report.Performance.Available &&
                   report.Performance.Frames > 0;
        }

        private static string GetComparisonContextKey(BoostSessionReport report)
        {
            if (report == null ||
                report.Performance == null ||
                string.IsNullOrWhiteSpace(
                    report.Performance.ComparisonContextKey))
            {
                return string.Empty;
            }

            return report.Performance.ComparisonContextKey.Trim();
        }

        private static string GetProcessIdentity(BoostSessionReport report)
        {
            if (report == null)
            {
                return string.Empty;
            }

            string value = report.Performance == null
                ? string.Empty
                : report.Performance.ProcessName;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = report.TargetName;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string fileName = Path.GetFileName(value.Trim());
            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            return (withoutExtension ?? string.Empty).Trim().ToUpperInvariant();
        }
    }
}
