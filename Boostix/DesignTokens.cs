using System;
using System.Windows;
using System.Windows.Media;
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

        public const double BodyTextSize = 12;
        public const double MetadataTextSize = 11;
        public const double SectionTitleSize = 18;
        public const double MinimumActionHeight = 40;
        public const double PreferredActionHeight = 44;

        public const int MotionFastMilliseconds = 160;
        public const int MotionStandardMilliseconds = 200;
        public const int MotionSlowMilliseconds = 220;
        public const double HoverLift = 1;
        public const double PageSlideDistance = 16;

        public static Color Background
        {
            get { return Resolve(SystemColors.WindowColor, Color.FromRgb(22, 22, 22)); }
        }

        public static Color Surface
        {
            get { return Resolve(SystemColors.WindowColor, Color.FromRgb(27, 27, 27)); }
        }

        public static Color SurfaceRaised
        {
            get { return Resolve(SystemColors.WindowColor, Color.FromRgb(37, 37, 37)); }
        }

        public static Color Hover
        {
            get { return Resolve(SystemColors.HighlightColor, Color.FromRgb(45, 45, 45)); }
        }

        public static Color Border
        {
            get { return Resolve(SystemColors.ActiveBorderColor, Color.FromRgb(56, 56, 56)); }
        }

        public static Color Divider
        {
            get { return Resolve(SystemColors.ActiveBorderColor, Color.FromRgb(42, 42, 42)); }
        }

        public static Color Text
        {
            get { return Resolve(SystemColors.WindowTextColor, Color.FromRgb(244, 244, 244)); }
        }

        public static Color SecondaryText
        {
            get { return Resolve(SystemColors.WindowTextColor, Color.FromRgb(189, 189, 189)); }
        }

        public static Color MutedText
        {
            get { return Resolve(SystemColors.GrayTextColor, Color.FromRgb(142, 142, 142)); }
        }

        public static Color Accent
        {
            get
            {
                return Resolve(
                    SystemColors.HighlightColor,
                    Color.FromRgb(
                        ProductBrand.AccentRed,
                        ProductBrand.AccentGreen,
                        ProductBrand.AccentBlue));
            }
        }

        public static Color AccentText
        {
            get
            {
                return Resolve(
                    SystemColors.WindowTextColor,
                    Color.FromRgb(
                        ProductBrand.AccentTextRed,
                        ProductBrand.AccentTextGreen,
                        ProductBrand.AccentTextBlue));
            }
        }

        /// <summary>
        /// Text drawn on an accent/highlight background. High Contrast must use
        /// HighlightText together with Highlight; WindowText is not guaranteed
        /// to contrast with a user-selected highlight colour.
        /// </summary>
        public static Color AccentForeground
        {
            get { return Resolve(SystemColors.HighlightTextColor, Text); }
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
            get { return Resolve(SystemColors.HighlightColor, AccentText); }
        }

        public static Color ChromeGlyph
        {
            get { return Resolve(SystemColors.WindowTextColor, Color.FromRgb(139, 139, 139)); }
        }

        public static Color ToggleKnobOn
        {
            get { return Resolve(SystemColors.HighlightTextColor, Colors.White); }
        }

        public static Color ToggleKnobOff
        {
            get { return Resolve(SystemColors.WindowTextColor, Colors.White); }
        }

        public static Color Success
        {
            get { return Resolve(SystemColors.WindowTextColor, Color.FromRgb(77, 219, 130)); }
        }

        public static Color Warning
        {
            get { return Resolve(SystemColors.WindowTextColor, Color.FromRgb(242, 184, 75)); }
        }

        public static Color Error
        {
            get { return Resolve(SystemColors.WindowTextColor, Color.FromRgb(255, 102, 122)); }
        }

        public static Color Destructive
        {
            get { return Resolve(SystemColors.HighlightColor, Color.FromRgb(231, 24, 42)); }
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

            resources[BackgroundBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowColor,
                Color.FromRgb(22, 22, 22)));
            resources[SurfaceBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowColor,
                Color.FromRgb(27, 27, 27)));
            resources[SurfaceRaisedBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowColor,
                Color.FromRgb(37, 37, 37)));
            resources[HoverBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightColor,
                Color.FromRgb(45, 45, 45)));
            resources[BorderBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.ActiveBorderColor,
                Color.FromRgb(56, 56, 56)));
            resources[DividerBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.ActiveBorderColor,
                Color.FromRgb(42, 42, 42)));
            resources[TextBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Color.FromRgb(244, 244, 244)));
            resources[SecondaryTextBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Color.FromRgb(189, 189, 189)));
            resources[MutedTextBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.GrayTextColor,
                Color.FromRgb(142, 142, 142)));
            resources[AccentBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightColor,
                Color.FromRgb(
                    ProductBrand.AccentRed,
                    ProductBrand.AccentGreen,
                    ProductBrand.AccentBlue)));
            resources[AccentTextBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Color.FromRgb(
                    ProductBrand.AccentTextRed,
                    ProductBrand.AccentTextGreen,
                    ProductBrand.AccentTextBlue)));
            resources[AccentForegroundBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightTextColor,
                Color.FromRgb(244, 244, 244)));
            resources[AccentPressedBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightColor,
                (Color)ColorConverter.ConvertFromString(
                    ProductBrand.AccentPressedHex)));
            resources[FocusBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightColor,
                Color.FromRgb(
                    ProductBrand.AccentTextRed,
                    ProductBrand.AccentTextGreen,
                    ProductBrand.AccentTextBlue)));
            resources[SuccessBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Color.FromRgb(77, 219, 130)));
            resources[WarningBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Color.FromRgb(242, 184, 75)));
            resources[ErrorBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Color.FromRgb(255, 102, 122)));
            resources[DestructiveBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightColor,
                Color.FromRgb(231, 24, 42)));
            resources[ChromeGlyphBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Color.FromRgb(139, 139, 139)));
            resources[ToggleKnobOnBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.HighlightTextColor,
                Colors.White));
            resources[ToggleKnobOffBrushKey] = Brush(Resolve(
                highContrast,
                SystemColors.WindowTextColor,
                Colors.White));
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
}
