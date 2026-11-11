using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Boostix
{
    internal enum PreGameProgramKind
    {
        // Has a window: Boostix can ask it to close, like its own X button.
        Closable,
        // A launcher or an anti-cheat the game cannot start without.
        GameNeedsIt,
        // Already on the list Boost closes when the game starts.
        ClosesForGame,
        // Lives at the clock without a window: only its own menu closes it.
        NoWindow,
        // Closed from this screen.
        Closed
    }

    internal sealed class PreGameProgram
    {
        public string Name;
        public string ProcessName;
        public long Bytes;
        public PreGameProgramKind Kind;
    }

    /// <summary>
    /// The memory a game usually takes against what is free right now, and
    /// who holds the rest.
    /// </summary>
    internal sealed class PreGamePlan
    {
        public string GameName = string.Empty;
        public string GamePath = string.Empty;
        public long TypicalBytes;
        public long AvailableBytes;
        public long TotalBytes;
        public List<PreGameProgram> Programs = new List<PreGameProgram>();

        public bool IsShort
        {
            get { return PreGameCheck.IsShort(TypicalBytes, AvailableBytes); }
        }

        public long MissingBytes
        {
            get { return Math.Max(0, TypicalBytes - AvailableBytes); }
        }
    }

    /// <summary>
    /// Seven of seven real CS2 games ran out of memory: the advice came in
    /// the debrief, after the game. This check comes before it, when closing
    /// something still helps.
    /// </summary>
    internal static class PreGameCheck
    {
        internal const int MaximumPrograms = 6;

        internal static bool IsShort(long typicalBytes, long availableBytes)
        {
            return typicalBytes > 0 && availableBytes > 0 && typicalBytes > availableBytes;
        }

        internal static List<PreGameProgram> Classify(
            IEnumerable<BoostMemoryHog> hogs,
            string gamePath,
            IEnumerable<string> closeForGame,
            Func<string, bool> hasWindow)
        {
            var chosen = new HashSet<string>(
                closeForGame ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var programs = new List<PreGameProgram>();
            foreach (BoostMemoryHog hog in (hogs ?? Enumerable.Empty<BoostMemoryHog>())
                .OrderByDescending(item => item.Bytes))
            {
                string processName = string.IsNullOrWhiteSpace(hog.ProcessName) ? hog.Name : hog.ProcessName;
                PreGameProgramKind kind;
                if (SessionVerdicts.GameNeeds(gamePath, hog))
                {
                    kind = PreGameProgramKind.GameNeedsIt;
                }
                else if (chosen.Contains(processName) || chosen.Contains(hog.Name ?? string.Empty))
                {
                    kind = PreGameProgramKind.ClosesForGame;
                }
                else if (hasWindow != null && hasWindow(processName))
                {
                    kind = PreGameProgramKind.Closable;
                }
                else
                {
                    kind = PreGameProgramKind.NoWindow;
                }
                programs.Add(new PreGameProgram
                {
                    Name = hog.Name ?? processName,
                    ProcessName = processName,
                    Bytes = hog.Bytes,
                    Kind = kind
                });
            }
            return programs;
        }

        internal static SessionVerdict Describe(PreGamePlan plan)
        {
            if (plan.IsShort)
            {
                return new SessionVerdict
                {
                    Title = L.T("Памяти может не хватить"),
                    Detail = string.Format(
                        CultureInfo.CurrentCulture,
                        L.T("{0} обычно занимает до {1}, а свободно {2}. Закройте лишнее, пока игра не запущена."),
                        plan.GameName,
                        SessionVerdicts.FormatGigabytes(plan.TypicalBytes),
                        SessionVerdicts.FormatGigabytes(plan.AvailableBytes)),
                    Tone = BoostixDesignTokens.Warning
                };
            }
            return new SessionVerdict
            {
                Title = L.T("Памяти хватит"),
                Detail = string.Format(
                    CultureInfo.CurrentCulture,
                    L.T("{0} обычно занимает до {1}, свободно {2}. Можно играть."),
                    plan.GameName,
                    SessionVerdicts.FormatGigabytes(plan.TypicalBytes),
                    SessionVerdicts.FormatGigabytes(plan.AvailableBytes)),
                Tone = BoostixDesignTokens.Success
            };
        }

        /// <summary>
        /// One look at the system: memory, then the biggest programs. Only the
        /// few listed programs are asked whether they have a window.
        /// </summary>
        internal static PreGamePlan Capture(
            string gameName,
            string gamePath,
            long typicalBytes,
            IEnumerable<string> closeForGame)
        {
            var plan = new PreGamePlan
            {
                GameName = gameName ?? string.Empty,
                GamePath = gamePath ?? string.Empty,
                TypicalBytes = typicalBytes
            };
            long total;
            long available;
            if (BoostSystemMetrics.TryGetMemory(out total, out available))
            {
                plan.TotalBytes = total;
                plan.AvailableBytes = available;
            }

            Process[] processes = new Process[0];
            try
            {
                processes = Process.GetProcesses();
                string gameProcess = Path.GetFileNameWithoutExtension(plan.GamePath);
                List<BoostMemoryHog> hogs = MemoryHogRanking.RankTop(
                    processes.Select(process => new KeyValuePair<string, long>(
                        process.ProcessName,
                        process.WorkingSet64)),
                    gameProcess,
                    MaximumPrograms);
                foreach (BoostMemoryHog hog in hogs)
                {
                    hog.Name = MemoryHogRanking.ReadableName(hog.ProcessName, processes);
                }
                Process[] snapshot = processes;
                plan.Programs = Classify(
                    MemoryHogRanking.MergeHelpers(hogs),
                    plan.GamePath,
                    closeForGame,
                    name => snapshot.Any(process =>
                        string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase) &&
                        HasWindow(process)));
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not look at the programs before the game.", ex);
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
            return plan;
        }

        private static bool HasWindow(Process process)
        {
            try
            {
                return process.MainWindowHandle != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The same request as the program's own X button, to every window
        /// it has. Never forced: a program can ask to save, or stay open.
        /// </summary>
        internal static bool RequestClose(string processName, int waitMilliseconds)
        {
            if (!SessionCloseList.IsValidName(processName) || AntiCheatGuard.IsPartOf(processName))
            {
                return false;
            }
            Process[] processes = new Process[0];
            try
            {
                processes = Process.GetProcessesByName(processName);
                var asked = new List<Process>();
                foreach (Process process in processes)
                {
                    if (HasWindow(process) && process.CloseMainWindow())
                    {
                        asked.Add(process);
                    }
                }
                if (asked.Count == 0)
                {
                    return false;
                }
                DateTime until = DateTime.UtcNow.AddMilliseconds(Math.Max(0, waitMilliseconds));
                foreach (Process process in asked)
                {
                    int left = (int)Math.Max(0, (until - DateTime.UtcNow).TotalMilliseconds);
                    if (!process.WaitForExit(left))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not ask a program to close before the game.", ex);
                return false;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// --demo-pregame: a 16 GB computer before CS2, as in the real
        /// sessions. Closing a program frees its memory.
        /// </summary>
        internal static PreGamePlan Demo(string gameName, string gamePath, ICollection<string> closed)
        {
            const long MiB = 1024L * 1024L;
            var hogs = new List<BoostMemoryHog>
            {
                new BoostMemoryHog { Name = "Firefox", ProcessName = "firefox", Bytes = 2390 * MiB },
                new BoostMemoryHog { Name = "FACEIT", ProcessName = "FACEIT", Bytes = 2050 * MiB },
                new BoostMemoryHog { Name = "Discord", ProcessName = "Discord", Bytes = 1610 * MiB },
                new BoostMemoryHog { Name = "Steam Client WebHelper", ProcessName = "steamwebhelper", Bytes = 1200 * MiB },
                new BoostMemoryHog { Name = "Wallpaper Engine", ProcessName = "wallpaper64", Bytes = 400 * MiB }
            };
            long freed = hogs
                .Where(hog => closed != null && closed.Contains(hog.ProcessName))
                .Sum(hog => hog.Bytes);
            List<PreGameProgram> programs = Classify(
                hogs.Where(hog => closed == null || !closed.Contains(hog.ProcessName)),
                @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
                null,
                name => name != "wallpaper64");
            foreach (string name in closed ?? new List<string>())
            {
                BoostMemoryHog hog = hogs.First(item => item.ProcessName == name);
                programs.Add(new PreGameProgram
                {
                    Name = hog.Name,
                    ProcessName = hog.ProcessName,
                    Bytes = hog.Bytes,
                    Kind = PreGameProgramKind.Closed
                });
            }
            return new PreGamePlan
            {
                GameName = gameName,
                GamePath = gamePath,
                TypicalBytes = 7780 * MiB,
                AvailableBytes = 6250 * MiB + freed,
                TotalBytes = 16220 * MiB,
                Programs = programs
            };
        }
    }

    /// <summary>
    /// The whole memory as one bar: what other programs hold, then the block
    /// the game will take. What does not fit hangs past the end in red.
    /// </summary>
    internal sealed class PreGameMeter : FrameworkElement
    {
        private PreGamePlan plan;

        public PreGamePlan Plan
        {
            get { return plan; }
            set
            {
                plan = value;
                InvalidateVisual();
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new MeterPeer(this);
        }

        protected override void OnRender(DrawingContext context)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (plan == null || plan.TotalBytes <= 0 || width < 40 || height < 8)
            {
                return;
            }
            double scale = width / Math.Max(plan.TotalBytes, plan.TotalBytes - plan.AvailableBytes + plan.TypicalBytes);
            double used = Math.Max(0, plan.TotalBytes - plan.AvailableBytes) * scale;
            double game = plan.TypicalBytes * scale;
            double end = plan.TotalBytes * scale;
            double radius = height / 2;

            var track = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 26));
            track.Freeze();
            context.DrawRoundedRectangle(track, null, new Rect(0, 0, end, height), radius, radius);

            var others = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 92));
            others.Freeze();
            context.DrawRoundedRectangle(others, null, new Rect(0, 0, Math.Max(height, used), height), radius, radius);

            double fits = Math.Max(0, Math.Min(game, end - used));
            var fitBrush = new SolidColorBrush(plan.IsShort ? BoostixDesignTokens.Warning : BoostixDesignTokens.Success);
            fitBrush.Freeze();
            if (fits > 0)
            {
                context.DrawRectangle(fitBrush, null, new Rect(used + 2, 0, Math.Max(0, fits - 2), height));
            }
            if (game > fits)
            {
                var overflow = new SolidColorBrush(BoostixDesignTokens.Error);
                overflow.Freeze();
                context.DrawRoundedRectangle(
                    overflow,
                    null,
                    new Rect(end + 2, 0, Math.Max(height, game - fits - 2), height),
                    radius,
                    radius);
            }
            var edge = new Pen(new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 200)), 2);
            edge.Freeze();
            context.DrawLine(edge, new Point(end, -4), new Point(end, height + 4));
        }

        private sealed class MeterPeer : FrameworkElementAutomationPeer
        {
            public MeterPeer(FrameworkElement owner)
                : base(owner)
            {
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Image;
            }

            protected override string GetClassNameCore()
            {
                return "PreGameMeter";
            }
        }
    }
}
