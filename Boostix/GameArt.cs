using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Boostix
{
    /// <summary>
    /// Everything the stage needs to dress itself as one game: the full-size
    /// art, the logo, and the colours taken from that art.
    /// </summary>
    internal sealed class StageArt
    {
        public ImageSource Hero;
        public ImageSource Ambient;
        public ImageSource Soft;
        public ImageSource Logo;
        public ImageSource Icon;
        public Color Accent;
        public Color Floor;
        public bool DarkLogo;
    }

    /// <summary>
    /// Colours taken from a game's art, so every game repaints the stage. The
    /// accent always carries dark text and the floor stays dark enough for
    /// white text, whatever the picture looks like.
    /// </summary>
    internal static class ArtPalette
    {
        private const int HueBins = 24;
        private const double MinimumAccentContrast = 5.5;
        private static readonly Color DarkText = Color.FromRgb(8, 17, 29);

        /// <summary>
        /// The most prominent vivid colour of a BGRA picture. Grey, black and
        /// washed-out pictures have no such colour and get the fallback.
        /// </summary>
        internal static Color PickAccent(byte[] bgra, Color fallback)
        {
            int pixels = bgra == null ? 0 : bgra.Length / 4;
            if (pixels == 0)
            {
                return fallback;
            }

            var weight = new double[HueBins];
            var red = new double[HueBins];
            var green = new double[HueBins];
            var blue = new double[HueBins];
            double total = 0;
            for (int index = 0; index + 3 < bgra.Length; index += 4)
            {
                double b = bgra[index] / 255.0;
                double g = bgra[index + 1] / 255.0;
                double r = bgra[index + 2] / 255.0;
                double maximum = Math.Max(r, Math.Max(g, b));
                double minimum = Math.Min(r, Math.Min(g, b));
                double saturation = maximum <= 0 ? 0 : (maximum - minimum) / maximum;
                if (maximum < 0.2 || saturation < 0.28)
                {
                    continue;
                }
                int bin = (int)(Hue(r, g, b, maximum, minimum) / 360.0 * HueBins) % HueBins;
                double share = saturation * saturation * maximum;
                weight[bin] += share;
                red[bin] += r * share;
                green[bin] += g * share;
                blue[bin] += b * share;
                total += share;
            }
            if (total < pixels * 0.012)
            {
                return fallback;
            }

            // A hue spread over two neighbouring bins is still one colour.
            int best = 0;
            double bestScore = -1;
            for (int bin = 0; bin < HueBins; bin++)
            {
                double score = weight[bin] +
                    0.5 * weight[(bin + HueBins - 1) % HueBins] +
                    0.5 * weight[(bin + 1) % HueBins];
                if (score > bestScore && weight[bin] > 0)
                {
                    bestScore = score;
                    best = bin;
                }
            }
            return Readable(Color.FromRgb(
                ToByte(red[best] / weight[best]),
                ToByte(green[best] / weight[best]),
                ToByte(blue[best] / weight[best])));
        }

        /// <summary>
        /// Lifts a colour until dark text on it is comfortable to read.
        /// </summary>
        internal static Color Readable(Color color)
        {
            double hue;
            double saturation;
            double lightness;
            ToHsl(color, out hue, out saturation, out lightness);
            saturation = Math.Max(0.62, Math.Min(0.95, saturation));
            lightness = Math.Max(0.56, Math.Min(0.72, lightness));
            Color result = FromHsl(hue, saturation, lightness);
            while (Contrast(result, DarkText) < MinimumAccentContrast && lightness < 0.9)
            {
                lightness += 0.02;
                result = FromHsl(hue, saturation, lightness);
            }
            return result;
        }

        /// <summary>
        /// The stage floor: nearly black with a trace of the game's colour.
        /// </summary>
        internal static Color Floor(Color accent)
        {
            double hue;
            double saturation;
            double lightness;
            ToHsl(accent, out hue, out saturation, out lightness);
            return FromHsl(hue, Math.Min(saturation, 0.42), 0.07);
        }

        /// <summary>
        /// True when most of a transparent logo is too dark to read on a dark
        /// stage: the part that is drawn, not the empty background, decides.
        /// </summary>
        internal static bool IsDarkLogo(byte[] bgra)
        {
            int drawn = 0;
            int dark = 0;
            for (int index = 0; bgra != null && index + 3 < bgra.Length; index += 4)
            {
                if (bgra[index + 3] < 128)
                {
                    continue;
                }
                drawn++;
                double luminance = Luminance(Color.FromRgb(bgra[index + 2], bgra[index + 1], bgra[index]));
                if (luminance < 0.09)
                {
                    dark++;
                }
            }
            return drawn > 0 && dark >= drawn * 0.25;
        }

        internal static double Contrast(Color first, Color second)
        {
            double a = Luminance(first);
            double b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        internal static byte[] ReadPixels(ImageSource image)
        {
            var source = image as BitmapSource;
            if (source == null)
            {
                return null;
            }
            try
            {
                var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = bgra.PixelWidth * 4;
                var pixels = new byte[stride * bgra.PixelHeight];
                bgra.CopyPixels(pixels, stride, 0);
                return pixels;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read the colours of game art.", ex);
                return null;
            }
        }

        private static double Luminance(Color color)
        {
            return 0.2126 * Linear(color.R) +
                0.7152 * Linear(color.G) +
                0.0722 * Linear(color.B);
        }

        private static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, Math.Round(value * 255)));
        }

        private static double Hue(double r, double g, double b, double maximum, double minimum)
        {
            double delta = maximum - minimum;
            if (delta <= 0)
            {
                return 0;
            }
            double hue;
            if (maximum == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (maximum == g)
            {
                hue = 60 * ((b - r) / delta + 2);
            }
            else
            {
                hue = 60 * ((r - g) / delta + 4);
            }
            return hue < 0 ? hue + 360 : hue;
        }

        private static void ToHsl(Color color, out double hue, out double saturation, out double lightness)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;
            double maximum = Math.Max(r, Math.Max(g, b));
            double minimum = Math.Min(r, Math.Min(g, b));
            lightness = (maximum + minimum) / 2;
            double delta = maximum - minimum;
            saturation = delta <= 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
            hue = Hue(r, g, b, maximum, minimum);
        }

        private static Color FromHsl(double hue, double saturation, double lightness)
        {
            double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            double second = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
            double match = lightness - chroma / 2;
            double r = 0;
            double g = 0;
            double b = 0;
            if (hue < 60) { r = chroma; g = second; }
            else if (hue < 120) { r = second; g = chroma; }
            else if (hue < 180) { g = chroma; b = second; }
            else if (hue < 240) { g = second; b = chroma; }
            else if (hue < 300) { r = second; b = chroma; }
            else { r = chroma; b = second; }
            return Color.FromRgb(ToByte(r + match), ToByte(g + match), ToByte(b + match));
        }
    }

    /// <summary>
    /// The icon stored in an executable at its largest size. Shell icons stop
    /// at 32 px, which is too small to stand in for a game's art.
    /// </summary>
    internal static class LargeIcon
    {
        private const int IconSize = 256;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(
            string file,
            int index,
            int width,
            int height,
            [Out] IntPtr[] icons,
            [Out] uint[] ids,
            uint count,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        internal static ImageSource Read(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !System.IO.File.Exists(executablePath))
            {
                return null;
            }
            var icons = new IntPtr[1];
            var ids = new uint[1];
            try
            {
                if (PrivateExtractIcons(executablePath, 0, IconSize, IconSize, icons, ids, 1, 0) == 0 ||
                    icons[0] == IntPtr.Zero)
                {
                    return null;
                }
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    icons[0],
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read the icon of a game.", ex);
                return null;
            }
            finally
            {
                if (icons[0] != IntPtr.Zero)
                {
                    DestroyIcon(icons[0]);
                }
            }
        }
    }

    /// <summary>
    /// What Boostix remembers about one game across its sessions.
    /// </summary>
    internal sealed class GameStats
    {
        public int Sessions;
        public int Crashes;
        public int TightSessions;
        public TimeSpan Total;

        internal static GameStats For(IEnumerable<BoostSessionReport> reports)
        {
            var stats = new GameStats();
            foreach (BoostSessionReport report in reports ?? Enumerable.Empty<BoostSessionReport>())
            {
                if (report == null || !report.EndedUtc.HasValue)
                {
                    continue;
                }
                TimeSpan length = report.EndedUtc.Value - report.StartedUtc;
                if (length <= TimeSpan.Zero)
                {
                    continue;
                }
                stats.Sessions++;
                stats.Total += length;
                if (!string.IsNullOrWhiteSpace(report.TargetCrashCode))
                {
                    stats.Crashes++;
                }
                if (string.Equals(report.WorstResourcePressure, "Critical", StringComparison.OrdinalIgnoreCase))
                {
                    stats.TightSessions++;
                }
            }
            return stats;
        }
    }
}
