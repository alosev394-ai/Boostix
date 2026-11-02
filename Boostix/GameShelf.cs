using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Boostix
{
    /// <summary>
    /// One game on the shelf: a saved profile, a game found in a launcher's
    /// library, or both merged into one card.
    /// </summary>
    internal sealed class ShelfGame
    {
        public ShelfGame()
        {
            Sessions = new List<BoostSessionReport>();
        }

        public string Name;
        public string ExecutablePath;
        public string Source;
        public string SteamAppId;
        public bool Saved;
        public bool AutoBoost;
        public List<BoostSessionReport> Sessions;

        public BoostSessionReport LastSession
        {
            get { return Sessions.Count == 0 ? null : Sessions[0]; }
        }

        /// <summary>
        /// Identity used for selection and stable fallback art.
        /// </summary>
        public string Key
        {
            get { return (ExecutablePath ?? Name ?? string.Empty).ToUpperInvariant(); }
        }
    }

    internal static class GameShelf
    {
        /// <summary>
        /// Merges saved profiles, library games and session history. A profile
        /// and a library entry are one game when they share the executable or
        /// the Steam install folder; the profile's executable wins because
        /// Auto Boost matches it. Order: recently played, then saved, then the
        /// rest of the library by name.
        /// </summary>
        internal static List<ShelfGame> Build(
            IEnumerable<GameProfile> profiles,
            IEnumerable<InstalledGame> installed,
            IEnumerable<BoostSessionReport> history)
        {
            var games = new List<ShelfGame>();
            foreach (GameProfile profile in profiles ?? Enumerable.Empty<GameProfile>())
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.ExecutablePath))
                {
                    continue;
                }
                games.Add(new ShelfGame
                {
                    Name = PrettyName(profile.DisplayName, profile.ExecutablePath),
                    ExecutablePath = profile.ExecutablePath,
                    SteamAppId = GameLibraryScanner.SteamAppIdForExecutable(profile.ExecutablePath),
                    Saved = true,
                    AutoBoost = profile.AutoBoost
                });
            }

            foreach (InstalledGame game in installed ?? Enumerable.Empty<InstalledGame>())
            {
                if (game == null || string.IsNullOrWhiteSpace(game.ExecutablePath))
                {
                    continue;
                }
                string appId = game.SteamAppId ??
                    GameLibraryScanner.SteamAppIdForExecutable(game.ExecutablePath);
                ShelfGame match = games.FirstOrDefault(item =>
                    SamePath(item.ExecutablePath, game.ExecutablePath) ||
                    (appId != null && string.Equals(item.SteamAppId, appId, StringComparison.Ordinal)));
                if (match != null)
                {
                    // The library knows the real title ("Counter-Strike 2"
                    // rather than the process name "cs2").
                    match.Name = game.Name;
                    match.Source = game.Source;
                    match.SteamAppId = match.SteamAppId ?? appId;
                    continue;
                }
                games.Add(new ShelfGame
                {
                    Name = game.Name,
                    ExecutablePath = game.ExecutablePath,
                    Source = game.Source,
                    SteamAppId = appId
                });
            }

            List<BoostSessionReport> reports = (history ?? Enumerable.Empty<BoostSessionReport>())
                .Where(item => item != null)
                .OrderByDescending(item => item.StartedUtc)
                .ToList();
            foreach (ShelfGame game in games)
            {
                string processName = Path.GetFileNameWithoutExtension(game.ExecutablePath ?? string.Empty);
                foreach (BoostSessionReport report in reports)
                {
                    bool samePath = !string.IsNullOrWhiteSpace(report.TargetExecutablePath) &&
                        SamePath(report.TargetExecutablePath, game.ExecutablePath);
                    bool sameName = string.IsNullOrWhiteSpace(report.TargetExecutablePath) &&
                        !string.IsNullOrWhiteSpace(report.TargetName) &&
                        string.Equals(report.TargetName, processName, StringComparison.OrdinalIgnoreCase);
                    if (samePath || sameName)
                    {
                        game.Sessions.Add(report);
                    }
                }
            }

            return games
                .OrderByDescending(game => game.LastSession == null
                    ? DateTime.MinValue
                    : game.LastSession.StartedUtc)
                .ThenByDescending(game => game.Saved)
                .ThenByDescending(game => game.AutoBoost)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Profiles saved from a running process carry the process name
        /// ("Cyberpunk2077"); the executable's product name reads better, and
        /// failing that the name is split at case and digit changes.
        /// </summary>
        internal static string PrettyName(string name, string executablePath)
        {
            string fileName = Path.GetFileNameWithoutExtension(executablePath ?? string.Empty);
            bool looksLikeProcess = string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase);
            if (!looksLikeProcess)
            {
                return name.Trim();
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
                    string product = (info.ProductName ?? string.Empty).Trim();
                    if (product.Length >= 2 && product.Length <= 60 &&
                        product.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return product;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable version info just falls through to the split name.
            }
            return SplitName(string.IsNullOrWhiteSpace(name) ? fileName : name);
        }

        internal static string SplitName(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var builder = new StringBuilder(text.Length + 4);
            for (int index = 0; index < text.Length; index++)
            {
                char current = text[index];
                if (current == '_' || current == '-')
                {
                    current = ' ';
                }
                if (index > 0 && builder.Length > 0 && builder[builder.Length - 1] != ' ')
                {
                    char previous = text[index - 1];
                    bool caseChange = char.IsLower(previous) && char.IsUpper(current);
                    bool digitChange = char.IsLetter(previous) && char.IsDigit(current);
                    if (caseChange || digitChange)
                    {
                        builder.Append(' ');
                    }
                }
                builder.Append(current);
            }
            string result = builder.ToString().Trim();
            return result.Length == 0 ? text : char.ToUpper(result[0], CultureInfo.CurrentCulture) + result.Substring(1);
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd('\\'),
                    Path.GetFullPath(right).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Cover art without the network: Steam keeps every library image in
    /// appcache\librarycache\&lt;appid&gt;\ (sometimes one hash folder deeper);
    /// other games get one of Boostix's own covers, picked by name.
    /// </summary>
    internal static class GameArtwork
    {
        internal const int FallbackCoverCount = 6;
        private static readonly Dictionary<string, ImageSource> cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        internal static ImageSource Cover(ShelfGame game)
        {
            ImageSource steam = SteamImage(game, new[] { "library_600x900.jpg", "library_capsule.jpg" }, 320);
            return steam ?? FallbackCover(game == null ? string.Empty : game.Key);
        }

        internal static bool HasOwnCover(ShelfGame game)
        {
            return FindSteamFile(game, new[] { "library_600x900.jpg", "library_capsule.jpg" }) != null;
        }

        internal static ImageSource Hero(ShelfGame game)
        {
            return SteamImage(game, new[] { "library_hero.jpg", "header.jpg", "library_header.jpg" }, 900);
        }

        internal static ImageSource Logo(ShelfGame game)
        {
            return SteamImage(game, new[] { "logo.png" }, 520);
        }

        internal static ImageSource FallbackCover(string key)
        {
            int sum = 0;
            foreach (char character in key ?? string.Empty)
            {
                sum = unchecked(sum * 31 + character);
            }
            int index = (sum & int.MaxValue) % FallbackCoverCount + 1;
            return Embedded("cover-" + index.ToString(CultureInfo.InvariantCulture) + ".jpg");
        }

        internal static ImageSource EmptyShelf()
        {
            return Embedded("empty-shelf.jpg");
        }

        internal static string FindSteamFile(ShelfGame game, string[] names)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.SteamAppId))
            {
                return null;
            }
            try
            {
                string steam = GameLibraryScanner.SteamDirectory();
                if (steam == null)
                {
                    return null;
                }
                string folder = Path.Combine(steam, "appcache", "librarycache", game.SteamAppId);
                if (!Directory.Exists(folder))
                {
                    // Older Steam builds kept flat names: 730_library_600x900.jpg.
                    string flat = Path.Combine(steam, "appcache", "librarycache");
                    foreach (string name in names)
                    {
                        string candidate = Path.Combine(flat, game.SteamAppId + "_" + name);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    return null;
                }
                foreach (string name in names)
                {
                    string direct = Path.Combine(folder, name);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }
                    foreach (string nested in Directory.EnumerateDirectories(folder))
                    {
                        string candidate = Path.Combine(nested, name);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return null;
        }

        private static ImageSource SteamImage(ShelfGame game, string[] names, int decodeWidth)
        {
            string path = FindSteamFile(game, names);
            return path == null ? null : FromFile(path, decodeWidth);
        }

        private static ImageSource FromFile(string path, int decodeWidth)
        {
            string key = path + "|" + decodeWidth.ToString(CultureInfo.InvariantCulture);
            lock (cache)
            {
                ImageSource cached;
                if (cache.TryGetValue(key, out cached))
                {
                    return cached;
                }
            }
            ImageSource image = null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read game art " + Path.GetFileName(path) + ".", ex);
            }
            lock (cache)
            {
                cache[key] = image;
            }
            return image;
        }

        private static ImageSource Embedded(string name)
        {
            string key = "embedded:" + name;
            lock (cache)
            {
                ImageSource cached;
                if (cache.TryGetValue(key, out cached))
                {
                    return cached;
                }
            }
            ImageSource image = null;
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Boostix.Art." + name))
                {
                    if (stream != null)
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        image = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read built-in art " + name + ".", ex);
            }
            lock (cache)
            {
                cache[key] = image;
            }
            return image;
        }
    }

    /// <summary>
    /// Starts a game the way its launcher would: Steam games through Steam so
    /// overlays, cloud saves and DRM work, everything else from its folder.
    /// </summary>
    internal static class GameLauncher
    {
        internal static bool TryLaunch(ShelfGame game, out string error)
        {
            error = null;
            if (game == null)
            {
                error = L.T("Игра не выбрана.");
                return false;
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(game.SteamAppId) && IsDigits(game.SteamAppId))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "steam://rungameid/" + game.SteamAppId,
                        UseShellExecute = true
                    });
                    return true;
                }
                if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
                {
                    error = L.T("Файл игры не найден. Возможно, её удалили или перенесли.");
                    return false;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath),
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not start " + game.Name + ".", ex);
                error = L.T("Windows не запустила игру.");
                return false;
            }
        }

        private static bool IsDigits(string text)
        {
            foreach (char character in text)
            {
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }
            return text.Length > 0 && text.Length <= 10;
        }
    }

    /// <summary>
    /// A fixed shelf for --demo --demo-shelf: screenshots and UI tests see the
    /// same games and sessions on every PC. Nothing here touches the disk;
    /// Steam art appears only where Steam has it cached.
    /// </summary>
    internal static class DemoShelf
    {
        internal static List<InstalledGame> Games()
        {
            return new List<InstalledGame>
            {
                new InstalledGame { Name = "Counter-Strike 2", ExecutablePath = @"C:\Games\Counter-Strike 2\cs2.exe", Source = "Steam", SteamAppId = "730" },
                new InstalledGame { Name = "Dota 2", ExecutablePath = @"C:\Games\Dota 2\dota2.exe", Source = "Steam", SteamAppId = "570" },
                new InstalledGame { Name = "Cyberpunk 2077", ExecutablePath = @"C:\Games\Cyberpunk 2077\Cyberpunk2077.exe", Source = "GOG" },
                new InstalledGame { Name = "Hollow Knight", ExecutablePath = @"C:\Games\Hollow Knight\hollow_knight.exe", Source = "Epic Games" }
            };
        }

        internal static List<BoostSessionReport> Sessions()
        {
            DateTime now = DateTime.UtcNow;
            return new List<BoostSessionReport>
            {
                Session("cs2", @"C:\Games\Counter-Strike 2\cs2.exe", now.AddHours(-3), 62, "Critical", 1, 0x1A, ""),
                Session("cs2", @"C:\Games\Counter-Strike 2\cs2.exe", now.AddDays(-1).AddHours(-2), 41, "Normal", 2, 0x2B, ""),
                Session("dota2", @"C:\Games\Dota 2\dota2.exe", now.AddDays(-1).AddHours(-6), 55, "Elevated", 3, 0x3C, ""),
                Session("Cyberpunk2077", @"C:\Games\Cyberpunk 2077\Cyberpunk2077.exe", now.AddDays(-3), 96, "Normal", 4, 0x4D, "0xc0000005")
            };
        }

        private static BoostSessionReport Session(
            string processName,
            string path,
            DateTime startedUtc,
            int minutes,
            string pressure,
            int seed,
            int id,
            string crashCode)
        {
            const long GiB = 1024L * 1024L * 1024L;
            var report = new BoostSessionReport
            {
                SessionId = "d3a0" + id.ToString("x2", CultureInfo.InvariantCulture) + "0000000000000000000000000a",
                Trigger = "Auto",
                Status = "TargetExited",
                StartedUtc = startedUtc,
                EndedUtc = startedUtc.AddMinutes(minutes),
                TargetName = processName,
                TargetExecutablePath = path,
                PhysicalMemoryTotalBytes = 16 * GiB,
                GpuDedicatedTotalBytes = 8 * GiB,
                PeakGpuDedicatedUsageBytes = 6 * GiB,
                WorstResourcePressure = pressure,
                TargetCrashCode = crashCode,
                StopReason = L.T("Игра закрылась.")
            };
            var random = new Random(seed);
            int dip = minutes * 60 * 2 / 5;
            int deepest = pressure == "Critical" ? 1700 : pressure == "Elevated" ? 2600 : 5200;
            long lowest = long.MaxValue;
            for (int offset = 0; offset <= minutes * 60; offset += BoostSessionReport.TimelineIntervalSeconds)
            {
                double distance = Math.Abs(offset - dip) / (minutes * 60 / 4.0);
                double memory = 7600 - (7600 - deepest) * Math.Max(0, 1 - distance) + random.Next(-260, 260);
                int cpu = 38 + random.Next(0, 24) + (distance < 0.5 ? 18 : 0);
                long bytes = (long)(Math.Max(600, memory) * 1024 * 1024);
                lowest = Math.Min(lowest, bytes);
                report.AddTimelinePoint(startedUtc.AddSeconds(offset), bytes, Math.Min(100, cpu));
            }
            report.MinimumAvailableMemoryBytes = lowest;
            return report;
        }
    }

    /// <summary>
    /// Plain-language conclusions about one finished game.
    /// </summary>
    internal sealed class SessionVerdict
    {
        public string Title;
        public string Detail;
        public Color Tone;
    }

    internal static class SessionVerdicts
    {
        internal static List<SessionVerdict> Describe(BoostSessionReport report)
        {
            var verdicts = new List<SessionVerdict>();
            if (report == null)
            {
                return verdicts;
            }

            long minimum = report.MinimumAvailableMemoryBytes;
            string pressure = report.WorstResourcePressure ?? string.Empty;
            if (string.Equals(pressure, "Critical", StringComparison.OrdinalIgnoreCase))
            {
                verdicts.Add(new SessionVerdict
                {
                    Title = L.T("Памяти почти не осталось"),
                    Detail = minimum > 0
                        ? string.Format(CultureInfo.CurrentCulture, L.T("В худший момент свободно было {0}."), FormatGigabytes(minimum))
                        : L.T("Windows почти исчерпала память во время игры."),
                    Tone = BoostixDesignTokens.Warning
                });
            }
            else if (string.Equals(pressure, "Elevated", StringComparison.OrdinalIgnoreCase))
            {
                verdicts.Add(new SessionVerdict
                {
                    Title = L.T("Памяти было впритык"),
                    Detail = minimum > 0
                        ? string.Format(CultureInfo.CurrentCulture, L.T("Минимум свободной памяти: {0}."), FormatGigabytes(minimum))
                        : L.T("Иногда памяти было меньше, чем хотелось бы."),
                    Tone = BoostixDesignTokens.Warning
                });
            }
            else
            {
                verdicts.Add(new SessionVerdict
                {
                    Title = L.T("Памяти хватило"),
                    Detail = minimum > 0
                        ? string.Format(CultureInfo.CurrentCulture, L.T("Даже в худший момент свободно было {0}."), FormatGigabytes(minimum))
                        : L.T("Нехватки памяти Boostix не заметил."),
                    Tone = BoostixDesignTokens.Success
                });
            }

            if (!string.IsNullOrWhiteSpace(report.TargetCrashCode))
            {
                verdicts.Add(new SessionVerdict
                {
                    Title = L.T("Игра вылетела"),
                    Detail = string.Format(CultureInfo.CurrentCulture, L.T("Windows записала сбой с кодом {0}."), report.TargetCrashCode),
                    Tone = BoostixDesignTokens.Error
                });
            }
            else
            {
                verdicts.Add(new SessionVerdict
                {
                    Title = L.T("Без вылетов"),
                    Detail = L.T("Игра закрылась сама, сбоев Windows не записала."),
                    Tone = BoostixDesignTokens.Success
                });
            }

            verdicts.Add(Advice(report));
            return verdicts;
        }

        private static SessionVerdict Advice(BoostSessionReport report)
        {
            string pressure = report.WorstResourcePressure ?? string.Empty;
            bool tight = string.Equals(pressure, "Critical", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pressure, "Elevated", StringComparison.OrdinalIgnoreCase);
            long gpuTotal = report.GpuDedicatedTotalBytes;
            bool videoFull = gpuTotal > 0 &&
                report.PeakGpuDedicatedUsageBytes >= gpuTotal / 100L * 95L;
            if (videoFull)
            {
                return new SessionVerdict
                {
                    Title = L.T("Совет: снизьте текстуры"),
                    Detail = L.T("Видеопамять заполнялась почти до конца. Текстуры на ступень ниже уберут подтормаживания."),
                    Tone = BoostixDesignTokens.Data
                };
            }
            if (tight)
            {
                return new SessionVerdict
                {
                    Title = L.T("Совет: закройте браузер"),
                    Detail = L.T("Браузер с вкладками держит гигабайты памяти. Закройте его перед следующей игрой."),
                    Tone = BoostixDesignTokens.Data
                };
            }
            return new SessionVerdict
            {
                Title = L.T("Менять ничего не нужно"),
                Detail = L.T("Компьютер справился. Boost можно оставить как есть."),
                Tone = BoostixDesignTokens.Data
            };
        }

        internal static string FormatGigabytes(long bytes)
        {
            double gigabytes = Math.Max(0, bytes) / 1073741824.0;
            return gigabytes.ToString(gigabytes >= 10 ? "0" : "0.0", CultureInfo.CurrentCulture) + L.T(" ГБ");
        }
    }

    /// <summary>
    /// The session chart: free memory as a filled area in the data colour,
    /// processor load as a thin line on its own 0-100% scale, and a marker at
    /// the moment memory was lowest.
    /// </summary>
    internal sealed class SessionChart : FrameworkElement
    {
        private readonly IList<SessionTimelinePoint> points;
        private readonly long totalMemoryBytes;

        public SessionChart(IList<SessionTimelinePoint> timeline, long physicalTotalBytes)
        {
            points = timeline ?? new List<SessionTimelinePoint>();
            totalMemoryBytes = physicalTotalBytes;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        public bool HasData
        {
            get { return points.Count >= 2; }
        }

        protected override void OnRender(DrawingContext context)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 20 || height < 20)
            {
                return;
            }
            Color data = BoostixDesignTokens.Data;
            Color muted = BoostixDesignTokens.MutedText;
            var gridPen = new Pen(new SolidColorBrush(BoostixDesignTokens.WithAlpha(muted, 40)), 1);
            gridPen.Freeze();
            for (int line = 1; line <= 3; line++)
            {
                double y = Math.Round(height * line / 4.0) + 0.5;
                context.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            }
            if (points.Count < 2)
            {
                return;
            }

            double span = Math.Max(1, points[points.Count - 1].OffsetSeconds - points[0].OffsetSeconds);
            double maximumMegabytes = totalMemoryBytes > 0
                ? totalMemoryBytes / (1024.0 * 1024.0)
                : points.Max(point => point.AvailableMegabytes) * 1.15;
            maximumMegabytes = Math.Max(1, maximumMegabytes);

            Func<SessionTimelinePoint, double> x = point =>
                (point.OffsetSeconds - points[0].OffsetSeconds) / span * width;
            Func<SessionTimelinePoint, double> memoryY = point =>
                height - Math.Min(1, point.AvailableMegabytes / maximumMegabytes) * (height - 6) - 1;

            var area = new StreamGeometry();
            using (StreamGeometryContext geometry = area.Open())
            {
                geometry.BeginFigure(new Point(0, height), true, true);
                foreach (SessionTimelinePoint point in points)
                {
                    geometry.LineTo(new Point(x(point), memoryY(point)), true, true);
                }
                geometry.LineTo(new Point(width, height), true, true);
            }
            area.Freeze();
            var fill = new SolidColorBrush(BoostixDesignTokens.WithAlpha(data, 60));
            fill.Freeze();
            context.DrawGeometry(fill, null, area);

            var memoryLine = new StreamGeometry();
            using (StreamGeometryContext geometry = memoryLine.Open())
            {
                geometry.BeginFigure(new Point(x(points[0]), memoryY(points[0])), false, false);
                for (int index = 1; index < points.Count; index++)
                {
                    geometry.LineTo(new Point(x(points[index]), memoryY(points[index])), true, true);
                }
            }
            memoryLine.Freeze();
            var memoryPen = new Pen(new SolidColorBrush(data), 2) { LineJoin = PenLineJoin.Round };
            memoryPen.Freeze();
            context.DrawGeometry(null, memoryPen, memoryLine);

            List<SessionTimelinePoint> cpu = points.Where(point => point.CpuPercent >= 0).ToList();
            if (cpu.Count >= 2)
            {
                // Processor load jumps between 10 s readings; a short moving
                // average shows the trend without the noise.
                int window = Math.Max(1, Math.Min(9, cpu.Count / 24));
                var cpuLine = new StreamGeometry();
                using (StreamGeometryContext geometry = cpuLine.Open())
                {
                    for (int index = 0; index < cpu.Count; index++)
                    {
                        int from = Math.Max(0, index - window);
                        int to = Math.Min(cpu.Count - 1, index + window);
                        double sum = 0;
                        for (int sample = from; sample <= to; sample++)
                        {
                            sum += cpu[sample].CpuPercent;
                        }
                        double load = sum / (to - from + 1);
                        var point = new Point(x(cpu[index]), height - load / 100.0 * (height - 6) - 1);
                        if (index == 0)
                        {
                            geometry.BeginFigure(point, false, false);
                        }
                        else
                        {
                            geometry.LineTo(point, true, true);
                        }
                    }
                }
                cpuLine.Freeze();
                var cpuPen = new Pen(
                    new SolidColorBrush(BoostixDesignTokens.WithAlpha(BoostixDesignTokens.SecondaryText, 120)),
                    1.4) { LineJoin = PenLineJoin.Round, DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
                cpuPen.Freeze();
                context.DrawGeometry(null, cpuPen, cpuLine);
            }

            SessionTimelinePoint lowest = points[0];
            foreach (SessionTimelinePoint point in points)
            {
                if (point.AvailableMegabytes < lowest.AvailableMegabytes)
                {
                    lowest = point;
                }
            }
            var markerFill = new SolidColorBrush(BoostixDesignTokens.Warning);
            markerFill.Freeze();
            var markerRing = new Pen(new SolidColorBrush(BoostixDesignTokens.Background), 2);
            markerRing.Freeze();
            context.DrawEllipse(markerFill, markerRing, new Point(x(lowest), memoryY(lowest)), 4.5, 4.5);
        }
    }
}
