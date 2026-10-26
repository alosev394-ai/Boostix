using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Boostix.Branding;

namespace Boostix
{
    /// <summary>
    /// Central visual vocabulary for the Boostix desktop experience.
    /// Values are expressed in device-independent pixels and support the
    /// Windows high-contrast palette without replacing user preferences.
    /// </summary>
    internal static class BoostixDesignTokens
    {
        internal const string BackgroundBrushKey = "Boostix.Theme.Background";
        internal const string SidebarBrushKey = "Boostix.Theme.Sidebar";
        internal const string SurfaceBrushKey = "Boostix.Theme.Surface";
        internal const string SurfaceRaisedBrushKey = "Boostix.Theme.SurfaceRaised";
        internal const string HoverBrushKey = "Boostix.Theme.Hover";
        internal const string BorderBrushKey = "Boostix.Theme.Border";
        internal const string DividerBrushKey = "Boostix.Theme.Divider";
        internal const string TextBrushKey = "Boostix.Theme.Text";
        internal const string SecondaryTextBrushKey = "Boostix.Theme.SecondaryText";
        internal const string MutedTextBrushKey = "Boostix.Theme.MutedText";
        internal const string AccentBrushKey = "Boostix.Theme.Accent";
        internal const string AccentTextBrushKey = "Boostix.Theme.AccentText";
        internal const string AccentForegroundBrushKey = "Boostix.Theme.AccentForeground";
        internal const string AccentPressedBrushKey = "Boostix.Theme.AccentPressed";
        internal const string FocusBrushKey = "Boostix.Theme.Focus";
        internal const string SuccessBrushKey = "Boostix.Theme.Success";
        internal const string WarningBrushKey = "Boostix.Theme.Warning";
        internal const string ErrorBrushKey = "Boostix.Theme.Error";
        internal const string DestructiveBrushKey = "Boostix.Theme.Destructive";
        internal const string ChromeGlyphBrushKey = "Boostix.Theme.ChromeGlyph";
        internal const string ToggleKnobOnBrushKey = "Boostix.Theme.ToggleKnobOn";
        internal const string ToggleKnobOffBrushKey = "Boostix.Theme.ToggleKnobOff";

        public const double Space4 = 4;
        public const double Space8 = 8;
        public const double Space12 = 12;
        public const double Space16 = 16;
        public const double Space24 = 24;

        public const double BodyTextSize = 13;
        public const double MetadataTextSize = 11.5;
        public const double OverlineTextSize = 11;
        public const double SectionTitleSize = 15;
        public const double PageTitleSize = 24;
        public const double MetricValueSize = 20;
        public const double MinimumActionHeight = 40;
        public const double PreferredActionHeight = 44;

        public const double RadiusSmall = 6;
        public const double RadiusMedium = 10;
        public const double RadiusLarge = 12;

        public const double WindowWidth = 880;
        public const double WindowHeight = 560;
        public const double SidebarWidth = 216;
        public const double ContentInset = 28;

        public const int MotionFastMilliseconds = 160;
        public const int MotionStandardMilliseconds = 200;
        public const int MotionSlowMilliseconds = 220;
        public const double HoverLift = 1;
        public const double PageSlideDistance = 16;

        /// <summary>
        /// Display face for titles, labels, buttons and numbers. Bahnschrift
        /// ships with Windows 10 1709+ and covers Cyrillic; Segoe UI is the
        /// fallback on older builds.
        /// </summary>
        public static FontFamily DisplayFontFamily
        {
            get { return new FontFamily("Bahnschrift, Segoe UI Variable Display, Segoe UI"); }
        }

        public static FontFamily TextFontFamily
        {
            get { return new FontFamily("Segoe UI Variable Text, Segoe UI"); }
        }

        // Night instrument cluster: graphite surfaces, amber light for Boost
        // and live readings, and status colours only where they mean status.
        private static readonly Color GraphiteBackground = Color.FromRgb(27, 31, 36);
        private static readonly Color GraphiteSidebar = Color.FromRgb(21, 24, 28);
        private static readonly Color GraphiteSurface = Color.FromRgb(34, 39, 45);
        private static readonly Color GraphiteRaised = Color.FromRgb(43, 49, 56);
        private static readonly Color GraphiteHover = Color.FromRgb(51, 58, 66);
        private static readonly Color GraphiteBorder = Color.FromRgb(53, 60, 68);
        private static readonly Color GraphiteDivider = Color.FromRgb(44, 50, 57);
        private static readonly Color InkText = Color.FromRgb(237, 239, 242);
        private static readonly Color SteelText = Color.FromRgb(180, 187, 195);
        private static readonly Color SlateText = Color.FromRgb(138, 146, 156);
        private static readonly Color AmberLight = Color.FromRgb(
            ProductBrand.AccentRed,
            ProductBrand.AccentGreen,
            ProductBrand.AccentBlue);
        private static readonly Color AmberText = Color.FromRgb(
            ProductBrand.AccentTextRed,
            ProductBrand.AccentTextGreen,
            ProductBrand.AccentTextBlue);
        private static readonly Color OnAmber = Color.FromRgb(23, 25, 29);
        private static readonly Color GoGreen = Color.FromRgb(91, 208, 142);
        private static readonly Color CautionYellow = Color.FromRgb(242, 209, 92);
        private static readonly Color FaultRed = Color.FromRgb(242, 107, 99);
        private static readonly Color DestructiveRed = Color.FromRgb(229, 72, 77);

        public static Color Background
        {
            get { return Resolve(SystemColors.WindowColor, GraphiteBackground); }
        }

        public static Color Sidebar
        {
            get { return Resolve(SystemColors.WindowColor, GraphiteSidebar); }
        }

        public static Color Surface
        {
            get { return Resolve(SystemColors.WindowColor, GraphiteSurface); }
        }

        public static Color SurfaceRaised
        {
            get { return Resolve(SystemColors.WindowColor, GraphiteRaised); }
        }

        public static Color Hover
        {
            get { return Resolve(SystemColors.HighlightColor, GraphiteHover); }
        }

        public static Color Border
        {
            get { return Resolve(SystemColors.ActiveBorderColor, GraphiteBorder); }
        }

        public static Color Divider
        {
            get { return Resolve(SystemColors.ActiveBorderColor, GraphiteDivider); }
        }

        public static Color Text
        {
            get { return Resolve(SystemColors.WindowTextColor, InkText); }
        }

        public static Color SecondaryText
        {
            get { return Resolve(SystemColors.WindowTextColor, SteelText); }
        }

        public static Color MutedText
        {
            get { return Resolve(SystemColors.GrayTextColor, SlateText); }
        }

        /// <summary>
        /// Amber is reserved for Boost: the primary action, the gauge needle
        /// and live readings while a session runs.
        /// </summary>
        public static Color Accent
        {
            get { return Resolve(SystemColors.HighlightColor, AmberLight); }
        }

        public static Color AccentText
        {
            get { return Resolve(SystemColors.WindowTextColor, AmberText); }
        }

        /// <summary>
        /// Text drawn on an accent/highlight background. High Contrast must use
        /// HighlightText together with Highlight; WindowText is not guaranteed
        /// to contrast with a user-selected highlight colour.
        /// </summary>
        public static Color AccentForeground
        {
            get { return Resolve(SystemColors.HighlightTextColor, OnAmber); }
        }

        public static Color AccentPressed
        {
            get
            {
                return Resolve(
                    SystemColors.HighlightColor,
                    (Color)ColorConverter.ConvertFromString(
                        ProductBrand.AccentPressedHex));
            }
        }

        public static Color Focus
        {
            get { return Resolve(SystemColors.HighlightColor, AmberText); }
        }

        public static Color ChromeGlyph
        {
            get { return Resolve(SystemColors.WindowTextColor, SlateText); }
        }

        public static Color ToggleKnobOn
        {
            get { return Resolve(SystemColors.HighlightTextColor, OnAmber); }
        }

        public static Color ToggleKnobOff
        {
            get { return Resolve(SystemColors.WindowTextColor, SteelText); }
        }

        public static Color Success
        {
            get { return Resolve(SystemColors.WindowTextColor, GoGreen); }
        }

        public static Color Warning
        {
            get { return Resolve(SystemColors.WindowTextColor, CautionYellow); }
        }

        public static Color Error
        {
            get { return Resolve(SystemColors.WindowTextColor, FaultRed); }
        }

        public static Color Destructive
        {
            get { return Resolve(SystemColors.HighlightColor, DestructiveRed); }
        }

        public static bool MotionEnabled
        {
            get { return SystemParameters.ClientAreaAnimation; }
        }

        public static SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }
            return brush;
        }

        public static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        /// <summary>
        /// Replaces the application-level semantic brushes. Controls using a
        /// DynamicResource receive the new brush immediately, so each individual
        /// brush may remain frozen and safe to share.
        /// </summary>
        internal static void ApplyThemeResources(ResourceDictionary resources)
        {
            ApplyThemeResources(resources, SystemParameters.HighContrast);
        }

        /// <summary>
        /// Deterministic overload used by regression tests without changing the
        /// user's Windows contrast setting.
        /// </summary>
        internal static void ApplyThemeResources(
            ResourceDictionary resources,
            bool highContrast)
        {
            if (resources == null)
            {
                throw new ArgumentNullException("resources");
            }

            resources[BackgroundBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowColor, GraphiteBackground));
            resources[SidebarBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowColor, GraphiteSidebar));
            resources[SurfaceBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowColor, GraphiteSurface));
            resources[SurfaceRaisedBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowColor, GraphiteRaised));
            resources[HoverBrushKey] = Brush(Resolve(highContrast, SystemColors.HighlightColor, GraphiteHover));
            resources[BorderBrushKey] = Brush(Resolve(highContrast, SystemColors.ActiveBorderColor, GraphiteBorder));
            resources[DividerBrushKey] = Brush(Resolve(highContrast, SystemColors.ActiveBorderColor, GraphiteDivider));
            resources[TextBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, InkText));
            resources[SecondaryTextBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, SteelText));
            resources[MutedTextBrushKey] = Brush(Resolve(highContrast, SystemColors.GrayTextColor, SlateText));
            resources[AccentBrushKey] = Brush(Resolve(highContrast, SystemColors.HighlightColor, AmberLight));
            resources[AccentTextBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, AmberText));
            resources[AccentForegroundBrushKey] = Brush(Resolve(highContrast, SystemColors.HighlightTextColor, OnAmber));
            resources[AccentPressedBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightColor,
                (Color)ColorConverter.ConvertFromString(ProductBrand.AccentPressedHex)));
            resources[FocusBrushKey] = Brush(Resolve(highContrast, SystemColors.HighlightColor, AmberText));
            resources[SuccessBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, GoGreen));
            resources[WarningBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, CautionYellow));
            resources[ErrorBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, FaultRed));
            resources[DestructiveBrushKey] = Brush(Resolve(highContrast, SystemColors.HighlightColor, DestructiveRed));
            resources[ChromeGlyphBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, SlateText));
            resources[ToggleKnobOnBrushKey] = Brush(Resolve(highContrast, SystemColors.HighlightTextColor, OnAmber));
            resources[ToggleKnobOffBrushKey] = Brush(Resolve(highContrast, SystemColors.WindowTextColor, SteelText));
        }

        private static Color Resolve(Color highContrast, Color normal)
        {
            return Resolve(SystemParameters.HighContrast, highContrast, normal);
        }

        private static Color Resolve(
            bool useHighContrast,
            Color highContrast,
            Color normal)
        {
            return useHighContrast ? highContrast : normal;
        }
    }

    /// <summary>
    /// Stroke icons drawn on a 24×24 grid (round caps and joins). Geometry is
    /// vector data only, so icons stay crisp at every DPI and follow the
    /// foreground colour, including High Contrast.
    /// </summary>
    internal static class BoostixIcons
    {
        public const string Home = "M3,10.5 L12,3 L21,10.5 M5,9 V20 H10 V14.5 H14 V20 H19 V9";
        public const string Activity = "M3,12 H7 L10,4.5 L14,19.5 L17,12 H21";
        public const string Layers = "M12,3 L21,7.75 L12,12.5 L3,7.75 Z M3,12 L12,16.75 L21,12 M3,16.25 L12,21 L21,16.25";
        public const string Chart = "M3,20.5 H21 M6.5,20.5 V12 M11.5,20.5 V5 M16.5,20.5 V14.5";
        public const string Gamepad = "M6,11 H10 M8,9 V13 M15,12 H15.01 M18,10 H18.01 M17.32,5 H6.68 A4,4 0 0 0 2.7,8.6 C2.7,8.6 2,13 2,15 A3,3 0 0 0 5,18 C6,18 6.5,17.5 7,17 L8.41,15.59 A2,2 0 0 1 9.83,15 H14.17 A2,2 0 0 1 15.59,15.59 L17,17 C17.5,17.5 18,18 19,18 A3,3 0 0 0 22,15 C22,13 21.3,8.6 21.3,8.6 A4,4 0 0 0 17.32,5 Z";
        public const string Sliders = "M3,6 H13 M17,6 H21 M15,4 V8 M3,12 H7 M11,12 H21 M9,10 V14 M3,18 H15 M19,18 H21 M17,16 V20";
        public const string Refresh = "M20.5,12 A8.5,8.5 0 0 1 6,18 M3.5,12 A8.5,8.5 0 0 1 18,6 M18,2.5 V6 H14.5 M6,21.5 V18 H9.5";
        public const string Download = "M12,3.5 V15 M7,10 L12,15 L17,10 M4.5,20.5 H19.5";
        public const string Close = "M6,6 L18,18 M18,6 L6,18";
        public const string Minimize = "M5.5,12 H18.5";
        public const string ChevronRight = "M9.5,6 L15.5,12 L9.5,18";
        public const string Check = "M5,12.5 L10,17.5 L19,7";
        public const string Alert = "M12,3.5 L21.5,20 H2.5 Z M12,9.5 V13.5 M12,17 H12.01";
        public const string Info = "M2.5,12 A9.5,9.5 0 1 1 21.5,12 A9.5,9.5 0 1 1 2.5,12 Z M12,11 V16.5 M12,7.5 H12.01";
        public const string CrossCircle = "M2.5,12 A9.5,9.5 0 1 1 21.5,12 A9.5,9.5 0 1 1 2.5,12 Z M15,9 L9,15 M9,9 L15,15";
        public const string CheckCircle = "M2.5,12 A9.5,9.5 0 1 1 21.5,12 A9.5,9.5 0 1 1 2.5,12 Z M8,12.5 L11,15.5 L16.5,9.5";
        public const string Clock = "M2.5,12 A9.5,9.5 0 1 1 21.5,12 A9.5,9.5 0 1 1 2.5,12 Z M12,7 V12 L15.5,14";
        public const string Cpu = "M6,6 H18 V18 H6 Z M9.5,9.5 H14.5 V14.5 H9.5 Z M9.5,2.5 V6 M14.5,2.5 V6 M9.5,18 V21.5 M14.5,18 V21.5 M2.5,9.5 H6 M2.5,14.5 H6 M18,9.5 H21.5 M18,14.5 H21.5";
        public const string Memory = "M2.5,7.5 H21.5 V15.5 H2.5 Z M6.5,15.5 V19 M12,15.5 V19 M17.5,15.5 V19 M6.5,10.5 V12.5 M10.5,10.5 V12.5 M14.5,10.5 V12.5 M18,10.5 V12.5";
        public const string Trash = "M3.5,6.5 H20.5 M9,6.5 V3.5 H15 V6.5 M6,6.5 L7,20.5 H17 L18,6.5 M10,11 V16.5 M14,11 V16.5";
        public const string Power = "M12,2.5 V11 M18.36,6.64 A9,9 0 1 1 5.64,6.64";
        public const string Shield = "M12,2.5 L20,6 V12 C20,16.75 16.6,20.25 12,21.5 C7.4,20.25 4,16.75 4,12 V6 Z M9,12 L11,14 L15.5,9.5";
        public const string File = "M14,2.5 H6.5 V21.5 H17.5 V6 Z M14,2.5 V6 H17.5 M9,12 H15 M9,16 H15";
        public const string Zap = "M13,2.5 L4,13.5 H11.5 L10.5,21.5 L20,10 H12.5 Z";
        public const string Plus = "M12,5 V19 M5,12 H19";
        public const string Stop = "M7,7 H17 V17 H7 Z";
        public const string Export = "M12,15 V3.5 M7.5,8 L12,3.5 L16.5,8 M4.5,13.5 V20.5 H19.5 V13.5";

        /// <summary>
        /// Creates a crisp vector icon. The 24×24 canvas keeps every glyph on
        /// the same optical grid regardless of its geometry bounds.
        /// </summary>
        public static FrameworkElement Create(
            string data,
            double size,
            Brush stroke,
            double thickness)
        {
            var path = new Path
            {
                Data = Geometry.Parse(data),
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = false
            };
            var canvas = new Canvas
            {
                Width = 24,
                Height = 24,
                IsHitTestVisible = false
            };
            canvas.Children.Add(path);
            var host = new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Child = canvas,
                IsHitTestVisible = false
            };
            return host;
        }

        public static FrameworkElement Create(string data, double size, Color stroke)
        {
            return Create(data, size, BoostixDesignTokens.Brush(stroke), 1.8);
        }
    }

    internal enum BoostixButtonKind
    {
        Primary,
        Secondary,
        Ghost,
        Destructive
    }

    /// <summary>
    /// Shared controls for every Boostix surface: the main window, the
    /// section pages and the setup/update dialogs use the same buttons,
    /// cards and focus visuals.
    /// </summary>
    internal static class BoostixUi
    {
        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiLargeIcon = 0x000000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInformation
        {
            public IntPtr Icon;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string TypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string path,
            uint fileAttributes,
            ref ShellFileInformation information,
            uint informationSize,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        private static readonly Dictionary<string, ImageSource> ExecutableIcons =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private sealed class ButtonVisuals
        {
            public BoostixButtonKind Kind;
            public SolidColorBrush Fill;
            public SolidColorBrush Stroke;
            public SolidColorBrush Foreground;
            public TranslateTransform Lift;
        }

        /// <summary>
        /// The shell icon of an executable, as Explorer shows it. Results are
        /// cached per path; a missing or unreadable file yields null.
        /// </summary>
        public static ImageSource ExecutableIcon(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }
            ImageSource cached;
            if (ExecutableIcons.TryGetValue(executablePath, out cached))
            {
                return cached;
            }

            ImageSource result = null;
            var information = new ShellFileInformation();
            try
            {
                if (System.IO.File.Exists(executablePath) &&
                    SHGetFileInfo(
                        executablePath,
                        0,
                        ref information,
                        (uint)Marshal.SizeOf(typeof(ShellFileInformation)),
                        ShgfiIcon | ShgfiLargeIcon) != IntPtr.Zero &&
                    information.Icon != IntPtr.Zero)
                {
                    BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        information.Icon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    result = bitmap;
                }
            }
            catch (Exception)
            {
                result = null;
            }
            finally
            {
                if (information.Icon != IntPtr.Zero)
                {
                    DestroyIcon(information.Icon);
                }
            }
            if (ExecutableIcons.Count > 256)
            {
                ExecutableIcons.Clear();
            }
            ExecutableIcons[executablePath] = result;
            return result;
        }

        private static readonly Dictionary<string, string> DisplayWords =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "BOOST", "Boost" }, { "BOOSTIX", "Boostix" },
                { "SESSION", "Session" }, { "GUARD", "Guard" },
                { "PAGEFILE", "Pagefile" }, { "DISCORD", "Discord" },
                { "STEAM", "Steam" }, { "EPIC", "Epic" }, { "GAMES", "Games" },
                { "ONEDRIVE", "OneDrive" }, { "TEAMS", "Teams" },
                { "MICROSOFT", "Microsoft" }, { "NVIDIA", "NVIDIA" },
                { "OVERLAY", "Overlay" }, { "WALLPAPER", "Wallpaper" },
                { "ENGINE", "Engine" }, { "RAM", "RAM" }, { "CPU", "CPU" },
                { "GPU", "GPU" }, { "VRAM", "VRAM" }, { "FPS", "FPS" },
                { "PID", "PID" }, { "UAC", "UAC" }, { "WINDOWS", "Windows" },
                { "PROOF", "Proof" }, { "MODE", "Mode" }, { "EXE", "EXE" },
                { "SHA", "SHA" }, { "DVR", "DVR" }, { "USB", "USB" },
                { "PRESENTMON", "PresentMon" }, { "WER", "WER" }
            };

        /// <summary>
        /// Shows legacy upper-case labels (stored in session reports) in
        /// sentence case without changing the stored text. Known product
        /// names and abbreviations keep their usual spelling.
        /// </summary>
        public static string DisplayCase(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }
            var result = new System.Text.StringBuilder(text.Length);
            bool firstWordWasUpper = false;
            bool firstWordSeen = false;
            int index = 0;
            while (index < text.Length)
            {
                if (!char.IsLetterOrDigit(text[index]))
                {
                    result.Append(text[index]);
                    index++;
                    continue;
                }
                int start = index;
                while (index < text.Length && char.IsLetterOrDigit(text[index]))
                {
                    index++;
                }
                string word = text.Substring(start, index - start);
                bool hasLower = false;
                int letters = 0;
                foreach (char character in word)
                {
                    if (char.IsLetter(character))
                    {
                        letters++;
                        hasLower |= char.IsLower(character);
                    }
                }
                bool upperWord = letters >= 2 && !hasLower;
                if (!firstWordSeen)
                {
                    firstWordSeen = true;
                    firstWordWasUpper = upperWord;
                }
                string mapped;
                if (upperWord && DisplayWords.TryGetValue(word, out mapped))
                {
                    result.Append(mapped);
                }
                else if (upperWord)
                {
                    result.Append(word.ToLowerInvariant());
                }
                else
                {
                    result.Append(word);
                }
            }
            string display = result.ToString();
            if (firstWordWasUpper)
            {
                for (int position = 0; position < display.Length; position++)
                {
                    if (char.IsLetter(display[position]))
                    {
                        if (char.IsLower(display[position]))
                        {
                            display = display.Substring(0, position) +
                                char.ToUpperInvariant(display[position]) +
                                display.Substring(position + 1);
                        }
                        break;
                    }
                }
            }
            return display;
        }

        public static TextBlock Text(
            string text,
            double size,
            Color color,
            bool display,
            FontWeight weight)
        {
            var block = new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = Math.Max(BoostixDesignTokens.OverlineTextSize, size),
                FontFamily = display
                    ? BoostixDesignTokens.DisplayFontFamily
                    : BoostixDesignTokens.TextFontFamily,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            return block;
        }

        /// <summary>
        /// Small sentence-case label naming a value or a field.
        /// </summary>
        public static TextBlock FieldLabel(string text)
        {
            return Text(
                text ?? string.Empty,
                BoostixDesignTokens.MetadataTextSize,
                BoostixDesignTokens.MutedText,
                false,
                FontWeights.Normal);
        }

        /// <summary>
        /// The Boostix mark: a small boost gauge with its needle in the amber
        /// Boost zone. The same drawing is used for the application icon.
        /// </summary>
        public static FrameworkElement BrandMark(double size)
        {
            var host = new Grid { Width = size, Height = size, SnapsToDevicePixels = true };
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(size * 0.26),
                Background = new SolidColorBrush(BoostixDesignTokens.SurfaceRaised),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.Border),
                BorderThickness = new Thickness(1)
            });
            var canvas = new Canvas { Width = size, Height = size };
            double centerX = size / 2;
            double centerY = size * 0.58;
            double radius = size * 0.31;
            canvas.Children.Add(MarkArc(centerX, centerY, radius, -120, 55, BoostixDesignTokens.MutedText, size * 0.07));
            canvas.Children.Add(MarkArc(centerX, centerY, radius, 62, 120, BoostixDesignTokens.Accent, size * 0.09));
            double needleAngle = 78 * Math.PI / 180;
            canvas.Children.Add(new Line
            {
                X1 = centerX,
                Y1 = centerY,
                X2 = centerX + radius * 0.92 * Math.Sin(needleAngle),
                Y2 = centerY - radius * 0.92 * Math.Cos(needleAngle),
                Stroke = new SolidColorBrush(BoostixDesignTokens.Text),
                StrokeThickness = size * 0.075,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
            var hub = new Ellipse
            {
                Width = size * 0.16,
                Height = size * 0.16,
                Fill = new SolidColorBrush(BoostixDesignTokens.Accent)
            };
            Canvas.SetLeft(hub, centerX - size * 0.08);
            Canvas.SetTop(hub, centerY - size * 0.08);
            canvas.Children.Add(hub);
            host.Children.Add(canvas);
            AutomationProperties.SetName(host, ProductBrand.ProductName);
            return host;
        }

        private static Path MarkArc(
            double centerX,
            double centerY,
            double radius,
            double fromAngle,
            double toAngle,
            Color color,
            double thickness)
        {
            Func<double, Point> at = delegate(double angle)
            {
                double radians = angle * Math.PI / 180;
                return new Point(
                    centerX + radius * Math.Sin(radians),
                    centerY - radius * Math.Cos(radians));
            };
            var figure = new PathFigure { StartPoint = at(fromAngle), IsClosed = false };
            figure.Segments.Add(new ArcSegment(
                at(toAngle),
                new Size(radius, radius),
                0,
                toAngle - fromAngle > 180,
                SweepDirection.Clockwise,
                true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return new Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }

        public static Border Card(Thickness padding)
        {
            return new Border
            {
                Background = new SolidColorBrush(BoostixDesignTokens.Surface),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(BoostixDesignTokens.RadiusMedium),
                Padding = padding,
                SnapsToDevicePixels = true
            };
        }

        /// <summary>
        /// A small rounded status label ("chip").
        /// </summary>
        public static Border Chip(string text, Color color)
        {
            var label = Text(
                text ?? string.Empty,
                BoostixDesignTokens.MetadataTextSize,
                color,
                true,
                FontWeights.Normal);
            label.VerticalAlignment = VerticalAlignment.Center;
            return new Border
            {
                Background = new SolidColorBrush(
                    SystemParameters.HighContrast
                        ? BoostixDesignTokens.Surface
                        : BoostixDesignTokens.WithAlpha(color, 34)),
                BorderBrush = new SolidColorBrush(
                    SystemParameters.HighContrast
                        ? color
                        : BoostixDesignTokens.WithAlpha(color, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 1, 7, 2),
                Child = label,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        /// <summary>
        /// Round status dot.
        /// </summary>
        public static FrameworkElement StatusDot(Color color, double size)
        {
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center
            };
            return dot;
        }

        public static Button ActionButton(
            string text,
            BoostixButtonKind kind,
            string iconData)
        {
            Color fill;
            Color stroke;
            Color foreground;
            GetButtonColors(kind, false, out fill, out stroke, out foreground);
            var fillBrush = new SolidColorBrush(fill);
            var strokeBrush = new SolidColorBrush(stroke);
            var foregroundBrush = new SolidColorBrush(foreground);
            var lift = new TranslateTransform();

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!string.IsNullOrEmpty(iconData))
            {
                FrameworkElement icon = BoostixIcons.Create(
                    iconData,
                    16,
                    foregroundBrush,
                    2);
                icon.Margin = new Thickness(0, 0, 8, 0);
                content.Children.Add(icon);
            }
            var label = new TextBlock
            {
                Text = text ?? string.Empty,
                FontFamily = BoostixDesignTokens.DisplayFontFamily,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = foregroundBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            content.Children.Add(label);

            var button = new Button
            {
                Height = BoostixDesignTokens.MinimumActionHeight,
                MinWidth = 96,
                Padding = new Thickness(18, 0, 18, 0),
                Background = fillBrush,
                BorderBrush = strokeBrush,
                Foreground = foregroundBrush,
                BorderThickness = new Thickness(1),
                FontFamily = BoostixDesignTokens.DisplayFontFamily,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Content = content,
                RenderTransform = lift,
                FocusVisualStyle = FocusVisual(BoostixDesignTokens.RadiusSmall)
            };
            AutomationProperties.SetName(button, text ?? string.Empty);

            button.Template = ButtonTemplate();
            var visuals = new ButtonVisuals
            {
                Kind = kind,
                Fill = fillBrush,
                Stroke = strokeBrush,
                Foreground = foregroundBrush,
                Lift = lift
            };
            button.Tag = visuals;

            button.MouseEnter += delegate { UpdateButtonState(button, true, true); };
            button.MouseLeave += delegate { UpdateButtonState(button, false, true); };
            button.IsEnabledChanged += delegate
            {
                button.Opacity = button.IsEnabled ? 1.0 : 0.42;
            };
            return button;
        }

        /// <summary>
        /// Updates the visible label of a button created by ActionButton.
        /// </summary>
        public static void SetButtonText(Button button, string text)
        {
            if (button == null)
            {
                return;
            }
            var content = button.Content as Panel;
            if (content != null)
            {
                foreach (UIElement child in content.Children)
                {
                    var label = child as TextBlock;
                    if (label != null)
                    {
                        label.Text = text ?? string.Empty;
                    }
                }
            }
            AutomationProperties.SetName(button, text ?? string.Empty);
        }

        public static void RefreshButtonTheme(Button button)
        {
            UpdateButtonState(button, button != null && button.IsMouseOver, false);
        }

        private static void UpdateButtonState(Button button, bool hovered, bool animate)
        {
            var visuals = button == null ? null : button.Tag as ButtonVisuals;
            if (visuals == null || (hovered && !button.IsEnabled))
            {
                return;
            }
            Color fill;
            Color stroke;
            Color foreground;
            GetButtonColors(visuals.Kind, hovered, out fill, out stroke, out foreground);
            int duration = hovered
                ? BoostixDesignTokens.MotionFastMilliseconds
                : BoostixDesignTokens.MotionSlowMilliseconds;
            AnimateColor(visuals.Fill, fill, duration, animate);
            AnimateColor(visuals.Stroke, stroke, duration, animate);
            AnimateColor(visuals.Foreground, foreground, duration, animate);
        }

        private static void GetButtonColors(
            BoostixButtonKind kind,
            bool hovered,
            out Color fill,
            out Color stroke,
            out Color foreground)
        {
            switch (kind)
            {
                case BoostixButtonKind.Primary:
                    fill = hovered
                        ? BoostixDesignTokens.AccentText
                        : BoostixDesignTokens.Accent;
                    stroke = fill;
                    foreground = BoostixDesignTokens.AccentForeground;
                    return;
                case BoostixButtonKind.Destructive:
                    fill = hovered
                        ? BoostixDesignTokens.Destructive
                        : BoostixDesignTokens.SurfaceRaised;
                    stroke = hovered
                        ? BoostixDesignTokens.Destructive
                        : BoostixDesignTokens.Border;
                    foreground = hovered
                        ? BoostixDesignTokens.AccentForeground
                        : BoostixDesignTokens.Error;
                    return;
                case BoostixButtonKind.Ghost:
                    fill = hovered
                        ? BoostixDesignTokens.Hover
                        : BoostixDesignTokens.WithAlpha(BoostixDesignTokens.Background, 0);
                    stroke = hovered
                        ? BoostixDesignTokens.Hover
                        : BoostixDesignTokens.WithAlpha(BoostixDesignTokens.Background, 0);
                    foreground = hovered
                        ? BoostixDesignTokens.Text
                        : BoostixDesignTokens.SecondaryText;
                    return;
                default:
                    fill = hovered
                        ? BoostixDesignTokens.Hover
                        : BoostixDesignTokens.Surface;
                    stroke = hovered
                        ? BoostixDesignTokens.SecondaryText
                        : BoostixDesignTokens.Border;
                    foreground = BoostixDesignTokens.Text;
                    return;
            }
        }

        private static ControlTemplate ButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "Chrome";
            chrome.SetValue(
                Border.CornerRadiusProperty,
                new CornerRadius(BoostixDesignTokens.RadiusSmall));
            chrome.SetValue(
                Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(
                Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(
                Border.BorderThicknessProperty,
                new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(
                Border.PaddingProperty,
                new TemplateBindingExtension(Control.PaddingProperty));
            chrome.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(
                ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(
                ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            presenter.SetValue(
                ContentPresenter.ContentProperty,
                new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(
                TextBlock.ForegroundProperty,
                new TemplateBindingExtension(Control.ForegroundProperty));
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;
            return template;
        }

        public static Style FocusVisual(double radius)
        {
            var style = new Style(typeof(Control));
            var template = new ControlTemplate(typeof(Control));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetResourceReference(
                Border.BorderBrushProperty,
                BoostixDesignTokens.FocusBrushKey);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius + 2));
            chrome.SetValue(Border.MarginProperty, new Thickness(-3));
            chrome.SetValue(UIElement.IsHitTestVisibleProperty, false);
            template.VisualTree = chrome;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        public static void AnimateColor(
            SolidColorBrush brush,
            Color target,
            int milliseconds,
            bool animate)
        {
            if (brush == null || brush.IsFrozen)
            {
                return;
            }
            if (!animate ||
                SystemParameters.HighContrast ||
                !SystemParameters.ClientAreaAnimation)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = target;
                return;
            }
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        /// <summary>
        /// Thin level bar; value is clamped to 0..1.
        /// </summary>
        public static Grid ProgressBar(double value, Color fill, double height)
        {
            double clamped = double.IsNaN(value)
                ? 0
                : Math.Max(0, Math.Min(1, value));
            var host = new Grid
            {
                Height = height,
                SnapsToDevicePixels = true
            };
            host.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(clamped, GridUnitType.Star)
            });
            host.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1 - clamped, GridUnitType.Star)
            });
            var track = new Border
            {
                Background = new SolidColorBrush(BoostixDesignTokens.SurfaceRaised),
                CornerRadius = new CornerRadius(1)
            };
            Grid.SetColumnSpan(track, 2);
            host.Children.Add(track);
            if (clamped > 0)
            {
                var bar = new Border
                {
                    Background = new SolidColorBrush(fill),
                    CornerRadius = new CornerRadius(1)
                };
                host.Children.Add(bar);
            }
            return host;
        }
    }
}
