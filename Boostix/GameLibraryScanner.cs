using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Boostix
{
    internal sealed class InstalledGame
    {
        public string Name;
        public string ExecutablePath;
        public string Source;
    }

    /// <summary>
    /// Finds games installed through Steam, Epic Games and GOG by reading the
    /// launchers' own files, without starting anything. Each entry points at
    /// the executable that runs the game, so Auto Boost recognises it.
    /// </summary>
    internal static class GameLibraryScanner
    {
        private const int MaximumSearchDepth = 4;
        private const int MaximumFilesPerGame = 4000;

        private static readonly string[] JunkNameParts =
        {
            "unins", "setup", "install", "crash", "report", "redist", "vcredist",
            "dxsetup", "dotnet", "prereq", "helper", "update", "easyanticheat",
            "battleye", "beservice", "cefprocess", "webview", "uplay", "eadesktop",
            "vconsole", "resourcecompiler", "errorreporter"
        };

        private static readonly string[] JunkDirectories =
        {
            "_commonredist", "redist", "redistributables", "directx", "support",
            "__installer", "easyanticheat", "battleye", "engine", "dotnet", "vcredist"
        };

        // Programs sold through Steam that are not games.
        private static readonly string[] KnownNonGames =
        {
            "Steamworks Common Redistributables", "Proton", "Steam Linux Runtime",
            "SteamVR", "Wallpaper Engine", "Action!", "OBS Studio", "Soundpad",
            "Lossless Scaling", "VTube Studio", "Aseprite", "Blender", "FPS Monitor",
            "Spacewar"
        };

        /// <summary>
        /// All installed games, one entry per executable, sorted by name.
        /// Launchers that are missing or unreadable are simply skipped.
        /// </summary>
        internal static List<InstalledGame> Scan()
        {
            var games = new List<InstalledGame>();
            AddSafely(games, ScanSteam);
            AddSafely(games, ScanEpic);
            AddSafely(games, ScanGog);
            return games
                .Where(game => game != null &&
                    !string.IsNullOrWhiteSpace(game.Name) &&
                    !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                    File.Exists(game.ExecutablePath))
                .GroupBy(game => game.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void AddSafely(List<InstalledGame> games, Func<IEnumerable<InstalledGame>> source)
        {
            try
            {
                games.AddRange(source());
            }
            catch (Exception ex)
            {
                CrashLog.Write("A game library could not be read.", ex);
            }
        }

        private static IEnumerable<InstalledGame> ScanSteam()
        {
            string steamPath = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                yield break;
            }
            steamPath = steamPath.Replace('/', '\\');
            string libraryFile = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            var libraries = new List<string> { steamPath };
            if (File.Exists(libraryFile))
            {
                libraries.AddRange(ParseLibraryFolders(ReadTextOrEmpty(libraryFile)));
            }
            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps))
                {
                    continue;
                }
                foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    string name;
                    string installDirectory;
                    if (!ParseAppManifest(ReadTextOrEmpty(manifest), out name, out installDirectory) ||
                        IsKnownNonGame(name))
                    {
                        continue;
                    }
                    string executable = PickMainExecutable(Path.Combine(apps, "common", installDirectory), name);
                    if (executable != null)
                    {
                        yield return new InstalledGame { Name = name, ExecutablePath = executable, Source = "Steam" };
                    }
                }
            }
        }

        private static IEnumerable<InstalledGame> ScanEpic()
        {
            string manifests = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(manifests))
            {
                yield break;
            }
            foreach (string item in Directory.EnumerateFiles(manifests, "*.item"))
            {
                string name;
                string executable;
                string json = ReadTextOrEmpty(item);
                if (!ParseEpicManifest(json, out name, out executable))
                {
                    continue;
                }
                // Some games start through a launcher that exits once the game
                // runs; Auto Boost needs the game itself.
                if (Path.GetFileName(executable).IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string better = PickMainExecutable(ReadJsonString(json, "InstallLocation"), name);
                    if (better != null &&
                        Path.GetFileName(better).IndexOf("launcher", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        executable = better;
                    }
                }
                yield return new InstalledGame { Name = name, ExecutablePath = executable, Source = "Epic Games" };
            }
        }

        private static IEnumerable<InstalledGame> ScanGog()
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(@"SOFTWARE\GOG.com\Games"))
            {
                if (root == null)
                {
                    yield break;
                }
                foreach (string id in root.GetSubKeyNames())
                {
                    using (RegistryKey game = root.OpenSubKey(id))
                    {
                        if (game == null)
                        {
                            continue;
                        }
                        string name = game.GetValue("gameName") as string;
                        string executable = game.GetValue("exe") as string;
                        if (string.IsNullOrWhiteSpace(executable))
                        {
                            string folder = game.GetValue("path") as string;
                            string file = game.GetValue("exeFile") as string;
                            if (!string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(file))
                            {
                                executable = Path.Combine(folder, file);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(executable))
                        {
                            yield return new InstalledGame { Name = name.Trim(), ExecutablePath = executable, Source = "GOG" };
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Library folders listed in Steam's libraryfolders.vdf.
        /// </summary>
        internal static List<string> ParseLibraryFolders(string vdf)
        {
            var paths = new List<string>();
            foreach (Match match in Regex.Matches(vdf ?? string.Empty, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                paths.Add(Unescape(match.Groups[1].Value));
            }
            return paths;
        }

        /// <summary>
        /// Display name and install folder from a Steam appmanifest_*.acf.
        /// </summary>
        internal static bool ParseAppManifest(string acf, out string name, out string installDirectory)
        {
            name = ReadVdfValue(acf, "name");
            installDirectory = ReadVdfValue(acf, "installdir");
            return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(installDirectory);
        }

        /// <summary>
        /// Name and full executable path from an Epic Games .item manifest;
        /// false for entries that are not games (plugins, engines, apps).
        /// </summary>
        internal static bool ParseEpicManifest(string json, out string name, out string executable)
        {
            name = ReadJsonString(json, "DisplayName");
            string location = ReadJsonString(json, "InstallLocation");
            string launch = ReadJsonString(json, "LaunchExecutable");
            executable = null;
            Match categories = Regex.Match(json ?? string.Empty, "\"AppCategories\"\\s*:\\s*\\[([^\\]]*)\\]");
            bool isGame = !categories.Success ||
                Regex.IsMatch(categories.Groups[1].Value, "\"games\"", RegexOptions.IgnoreCase);
            if (!isGame || string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(launch))
            {
                return false;
            }
            executable = Path.Combine(location, launch.Replace('/', '\\'));
            return true;
        }

        /// <summary>
        /// The executable that runs the game in an install folder: an Unreal
        /// "-Shipping" build when there is one, otherwise the largest
        /// executable that is not an installer, crash handler or runtime.
        /// </summary>
        internal static string PickMainExecutable(string directory)
        {
            return PickMainExecutable(directory, null);
        }

        /// <summary>
        /// Same choice, preferring an executable whose name matches the game
        /// (dota2.exe for "Dota 2") over larger tools next to it.
        /// </summary>
        internal static string PickMainExecutable(string directory, string nameHint)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }
            var candidates = new List<FileInfo>();
            var pending = new Queue<KeyValuePair<string, int>>();
            pending.Enqueue(new KeyValuePair<string, int>(directory, 0));
            int seen = 0;
            while (pending.Count > 0 && seen < MaximumFilesPerGame)
            {
                KeyValuePair<string, int> current = pending.Dequeue();
                DirectoryInfo info;
                try
                {
                    info = new DirectoryInfo(current.Key);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && current.Value > 0)
                    {
                        continue;
                    }
                    foreach (FileInfo file in info.EnumerateFiles("*.exe"))
                    {
                        seen++;
                        if (!IsJunkExecutable(file.Name))
                        {
                            candidates.Add(file);
                        }
                    }
                    if (current.Value >= MaximumSearchDepth)
                    {
                        continue;
                    }
                    foreach (DirectoryInfo child in info.EnumerateDirectories())
                    {
                        if (!JunkDirectories.Contains(child.Name.ToLowerInvariant()))
                        {
                            pending.Enqueue(new KeyValuePair<string, int>(child.FullName, current.Value + 1));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
            FileInfo shipping = candidates
                .Where(file => file.Name.IndexOf("-Shipping", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(file => file.Length)
                .FirstOrDefault();
            if (shipping != null)
            {
                return shipping.FullName;
            }
            string hint = Normalize(nameHint);
            if (hint.Length >= 3)
            {
                FileInfo named = candidates
                    .Where(file =>
                    {
                        string stem = Normalize(Path.GetFileNameWithoutExtension(file.Name));
                        return stem.Length >= 3 && (hint.Contains(stem) || stem.Contains(hint));
                    })
                    .OrderByDescending(file => file.Length)
                    .FirstOrDefault();
                if (named != null)
                {
                    return named.FullName;
                }
            }
            FileInfo chosen = candidates.OrderByDescending(file => file.Length).FirstOrDefault();
            return chosen == null ? null : chosen.FullName;
        }

        internal static bool IsKnownNonGame(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                KnownNonGames.Any(item => name.StartsWith(item, StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            var result = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    result.Append(char.ToLowerInvariant(character));
                }
            }
            return result.ToString();
        }

        private static string ReadTextOrEmpty(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static bool IsJunkExecutable(string fileName)
        {
            string lower = fileName.ToLowerInvariant();
            return JunkNameParts.Any(part => lower.Contains(part));
        }

        private static string ReadVdfValue(string vdf, string key)
        {
            Match match = Regex.Match(
                vdf ?? string.Empty,
                "\"" + Regex.Escape(key) + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.IgnoreCase);
            return match.Success ? Unescape(match.Groups[1].Value) : null;
        }

        private static string ReadJsonString(string json, string key)
        {
            Match match = Regex.Match(
                json ?? string.Empty,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return match.Success ? Unescape(match.Groups[1].Value) : null;
        }

        private static string Unescape(string value)
        {
            var result = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character != '\\' || index + 1 >= value.Length)
                {
                    result.Append(character);
                    continue;
                }
                char next = value[++index];
                if (next == 'u' && index + 4 < value.Length)
                {
                    int code;
                    if (int.TryParse(value.Substring(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        result.Append((char)code);
                        index += 4;
                        continue;
                    }
                }
                result.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next);
            }
            return result.ToString();
        }

        private static string ReadRegistryString(RegistryKey root, string path, string name)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(path))
                {
                    return key == null ? null : key.GetValue(name) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
