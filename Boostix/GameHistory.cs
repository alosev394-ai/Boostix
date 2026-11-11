using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Boostix
{
    /// <summary>
    /// One program that held memory in the games of one title.
    /// </summary>
    internal sealed class GameHistoryProgram
    {
        public string Name;
        public string ProcessName;
        public int Sessions;
        public long LargestBytes;
        public long TotalBytes;
        // A launcher or an anti-cheat: the game needs it, so it is never
        // offered for closing.
        public bool GameNeedsIt;
        // Boost asked it to close before and it stayed: offering it again
        // only repeats the failure.
        public bool WouldNotClose;
    }

    /// <summary>
    /// What keeps happening in one game, across all its saved sessions.
    /// </summary>
    internal sealed class GameHistorySummary
    {
        // Finished sessions, newest first.
        public List<BoostSessionReport> Sessions = new List<BoostSessionReport>();
        public GameStats Stats = new GameStats();
        public long LowestFreeBytes;
        public long HighestLowestFreeBytes;
        // Sessions that recorded who held the memory: games where it ran
        // short, played on 5.1 and newer.
        public int ProgramSessions;
        public List<GameHistoryProgram> Programs = new List<GameHistoryProgram>();
        public SessionVerdict Headline;
    }

    /// <summary>
    /// One debrief tells about one game. The history tells what repeats:
    /// how often memory ran short and who held it each time.
    /// </summary>
    internal static class GameHistory
    {
        internal const int MaximumPrograms = 5;

        internal static GameHistorySummary Summarize(IEnumerable<BoostSessionReport> sessions)
        {
            var summary = new GameHistorySummary();
            summary.Sessions = (sessions ?? Enumerable.Empty<BoostSessionReport>())
                .Where(report => report != null &&
                    report.EndedUtc.HasValue &&
                    report.EndedUtc.Value > report.StartedUtc)
                .OrderByDescending(report => report.StartedUtc)
                .ToList();
            summary.Stats = GameStats.For(summary.Sessions);

            List<long> lowest = summary.Sessions
                .Where(report => report.MinimumAvailableMemoryBytes > 0)
                .Select(report => report.MinimumAvailableMemoryBytes)
                .ToList();
            if (lowest.Count > 0)
            {
                summary.LowestFreeBytes = lowest.Min();
                summary.HighestLowestFreeBytes = lowest.Max();
            }

            string gamePath = summary.Sessions
                .Select(report => report.TargetExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            var programs = new Dictionary<string, GameHistoryProgram>(StringComparer.OrdinalIgnoreCase);
            foreach (BoostSessionReport report in summary.Sessions)
            {
                List<BoostMemoryHog> hogs = report.MemoryHogs ?? new List<BoostMemoryHog>();
                if (hogs.Count == 0)
                {
                    continue;
                }
                summary.ProgramSessions++;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (BoostMemoryHog hog in hogs)
                {
                    // Reports before 5.1 kept only the name.
                    string key = string.IsNullOrWhiteSpace(hog.ProcessName) ? hog.Name : hog.ProcessName;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }
                    GameHistoryProgram program;
                    if (!programs.TryGetValue(key, out program))
                    {
                        program = new GameHistoryProgram
                        {
                            Name = string.IsNullOrWhiteSpace(hog.Name) ? key : hog.Name,
                            ProcessName = hog.ProcessName ?? string.Empty,
                            GameNeedsIt = SessionVerdicts.GameNeeds(gamePath, hog)
                        };
                        programs.Add(key, program);
                    }
                    if (seen.Add(key))
                    {
                        program.Sessions++;
                    }
                    program.LargestBytes = Math.Max(program.LargestBytes, hog.Bytes);
                    program.TotalBytes += hog.Bytes;
                }
            }
            var stayed = new HashSet<string>(
                summary.Sessions
                    .Where(report => report.StillRunningPrograms != null)
                    .SelectMany(report => report.StillRunningPrograms)
                    .Select(Simplify),
                StringComparer.Ordinal);
            foreach (GameHistoryProgram program in programs.Values)
            {
                program.WouldNotClose = stayed.Contains(Simplify(program.ProcessName)) ||
                    stayed.Contains(Simplify(program.Name));
            }
            summary.Programs = programs.Values
                .OrderByDescending(program => program.Sessions)
                .ThenByDescending(program => program.TotalBytes)
                .Take(MaximumPrograms)
                .ToList();
            summary.Headline = Describe(summary);
            return summary;
        }

        /// <summary>
        /// The script reports "discord_clips", the memory list "Discord Clips":
        /// one program.
        /// </summary>
        private static string Simplify(string name)
        {
            string text = (name ?? string.Empty).Trim();
            if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - 4);
            }
            return new string(text
                .Where(character => character != ' ' && character != '_' && character != '-')
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        private static SessionVerdict Describe(GameHistorySummary summary)
        {
            GameStats stats = summary.Stats;
            int games = stats.Sessions;
            if (stats.Crashes > 0 && stats.Crashes * 3 >= games)
            {
                return new SessionVerdict
                {
                    Title = L.T("Игра часто вылетает"),
                    Detail = string.Format(
                        CultureInfo.CurrentCulture,
                        L.T("Вылетов: {0} из {1} игр. Что случилось, сказано в разборе каждой."),
                        stats.Crashes,
                        games),
                    Tone = BoostixDesignTokens.Error
                };
            }
            if (stats.TightSessions > 0)
            {
                return new SessionVerdict
                {
                    Title = stats.TightSessions * 4 >= games * 3
                        ? L.T("Памяти не хватает почти в каждой игре")
                        : L.T("Памяти иногда не хватает"),
                    Detail = string.Format(
                        CultureInfo.CurrentCulture,
                        L.T("Так было в {0} из {1} игр."),
                        stats.TightSessions,
                        games) +
                        DescribeLowest(summary) +
                        DescribeTopProgram(summary),
                    Tone = BoostixDesignTokens.Warning
                };
            }
            string calm = string.Format(
                CultureInfo.CurrentCulture,
                L.T("Ни в одной из {0} игр память не заканчивалась."),
                games);
            if (stats.Crashes > 0)
            {
                calm += " " + string.Format(CultureInfo.CurrentCulture, L.T("Вылетов: {0}."), stats.Crashes);
            }
            return new SessionVerdict
            {
                Title = L.T("Памяти хватает"),
                Detail = calm,
                Tone = BoostixDesignTokens.Success
            };
        }

        private static string DescribeLowest(GameHistorySummary summary)
        {
            if (summary.LowestFreeBytes <= 0)
            {
                return string.Empty;
            }
            string lowest = FormatLowest(summary.LowestFreeBytes);
            string highest = FormatLowest(summary.HighestLowestFreeBytes);
            return " " + (lowest == highest
                ? string.Format(CultureInfo.CurrentCulture, L.T("В худший момент свободно было {0}."), lowest)
                : string.Format(CultureInfo.CurrentCulture, L.T("В худший момент свободно было от {0} до {1}."), lowest, highest));
        }

        private static string DescribeTopProgram(GameHistorySummary summary)
        {
            GameHistoryProgram top = summary.Programs.FirstOrDefault();
            if (top == null)
            {
                return string.Empty;
            }
            return " " + string.Format(
                CultureInfo.CurrentCulture,
                L.T("Чаще всех память занимал {0}: в {1} из {2} игр."),
                top.Name,
                top.Sessions,
                summary.ProgramSessions);
        }

        /// <summary>
        /// Like the debrief's gigabytes, but a range starting near zero keeps
        /// its real value instead of "less than 0.1".
        /// </summary>
        internal static string FormatLowest(long bytes)
        {
            double gigabytes = Math.Max(0, bytes) / 1073741824.0;
            return gigabytes < 0.095
                ? gigabytes.ToString("0.00", CultureInfo.CurrentCulture) + L.T(" ГБ")
                : SessionVerdicts.FormatGigabytes(bytes);
        }
    }

    /// <summary>
    /// One bar per game: the least free memory it left. The dashed line is
    /// where Windows counts memory as running out.
    /// </summary>
    internal sealed class GameHistoryChart : FrameworkElement
    {
        internal const int MaximumBars = 24;
        private readonly List<BoostSessionReport> sessions;

        public GameHistoryChart(IEnumerable<BoostSessionReport> newestFirst)
        {
            sessions = (newestFirst ?? Enumerable.Empty<BoostSessionReport>())
                .Take(MaximumBars)
                .Reverse()
                .ToList();
            SnapsToDevicePixels = true;
        }

        public bool HasData
        {
            get { return sessions.Any(report => report.MinimumAvailableMemoryBytes > 0); }
        }

        public IList<BoostSessionReport> Bars
        {
            get { return sessions; }
        }

        // Without a peer Narrator skips the chart and its name.
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new ChartPeer(this);
        }

        private sealed class ChartPeer : FrameworkElementAutomationPeer
        {
            public ChartPeer(FrameworkElement owner)
                : base(owner)
            {
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Image;
            }

            protected override string GetClassNameCore()
            {
                return "GameHistoryChart";
            }
        }

        protected override void OnRender(DrawingContext context)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (sessions.Count == 0 || width < 40 || height < 40)
            {
                return;
            }
            long total = sessions.Max(report => report.PhysicalMemoryTotalBytes);
            long threshold = total > 0 ? DiagnosticPressureClassifier.PhysicalCriticalBytes(total) : 0;
            long largest = sessions.Max(report => report.MinimumAvailableMemoryBytes);
            double scale = Math.Max(Math.Max(threshold * 2, largest), 1);
            const double top = 8;
            double floor = height - 1;
            double usable = floor - top;

            var baseline = new Pen(new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 40)), 1);
            baseline.Freeze();
            context.DrawLine(baseline, new Point(0, floor + 0.5), new Point(width, floor + 0.5));

            double slot = width / sessions.Count;
            double barWidth = Math.Max(4, Math.Min(26, slot * 0.56));
            var crashBrush = new SolidColorBrush(BoostixDesignTokens.Error);
            crashBrush.Freeze();
            for (int index = 0; index < sessions.Count; index++)
            {
                BoostSessionReport report = sessions[index];
                double x = slot * index + (slot - barWidth) / 2;
                long value = report.MinimumAvailableMemoryBytes;
                bool tight = string.Equals(report.WorstResourcePressure, "Critical", StringComparison.OrdinalIgnoreCase);
                // The colours of the session rows below: enough is green, short is yellow.
                Color color = tight ? BoostixDesignTokens.Warning : BoostixDesignTokens.Success;
                double barHeight = value > 0 ? Math.Max(3, usable * Math.Min(1, value / scale)) : 3;
                var brush = new SolidColorBrush(value > 0 ? color : BoostixDesignTokens.WithAlpha(Colors.White, 50));
                brush.Freeze();
                context.DrawRoundedRectangle(
                    brush,
                    null,
                    new Rect(x, floor - barHeight, barWidth, barHeight),
                    Math.Min(4, barWidth / 3),
                    Math.Min(4, barWidth / 3));
                if (!string.IsNullOrWhiteSpace(report.TargetCrashCode))
                {
                    context.DrawEllipse(
                        crashBrush,
                        null,
                        new Point(x + barWidth / 2, Math.Max(4, floor - barHeight - 7)),
                        3,
                        3);
                }
            }

            if (threshold > 0)
            {
                double y = floor - usable * Math.Min(1, threshold / scale);
                var dashed = new Pen(new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 120)), 1)
                {
                    DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
                };
                dashed.Freeze();
                context.DrawLine(dashed, new Point(0, Math.Round(y) + 0.5), new Point(width, Math.Round(y) + 0.5));
            }
        }
    }
}
