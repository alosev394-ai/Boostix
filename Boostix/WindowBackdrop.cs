using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Boostix
{
    /// <summary>
    /// Windows 11 system glass behind the main window. DWM draws and blurs
    /// the backdrop itself, so Boostix only asks for it. Older builds, High
    /// Contrast and switched-off transparency effects keep the opaque petrol
    /// theme.
    /// </summary>
    internal static class WindowBackdrop
    {
        internal const int MinimumBuild = 22621;

        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmCaptionColor = 35;
        private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
        private const int DwmSystemBackdropType = 38;
        private const int BackdropNone = 1;
        private const int BackdropAcrylic = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameMargins
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OsVersionInfo
        {
            public int Size;
            public int MajorVersion;
            public int MinorVersion;
            public int BuildNumber;
            public int PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(
            IntPtr window,
            ref FrameMargins margins);

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersionInfo information);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        private const int GwlStyle = -16;
        private const long WsSysMenu = 0x00080000;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpKeepPlacement = 0x0001 | 0x0002 | 0x0004 | 0x0010;

        /// <summary>
        /// The hidden system frame would still let DWM draw a faint close
        /// glyph over the glass; Boostix has its own window buttons, so the
        /// system menu style is dropped. WM_CLOSE and Alt+F4 keep working.
        /// </summary>
        internal static void HideSystemCaptionButtons(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return;
            }
            long style = GetWindowLongPtr(window, GwlStyle).ToInt64();
            if ((style & WsSysMenu) == 0)
            {
                return;
            }
            SetWindowLongPtr(window, GwlStyle, new IntPtr(style & ~WsSysMenu));
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, SwpKeepPlacement | SwpFrameChanged);
        }

        /// <summary>
        /// DWMWA_SYSTEMBACKDROP_TYPE arrived in Windows 11 22H2 (build 22621).
        /// </summary>
        internal static bool IsSupportedBuild(int build)
        {
            return build >= MinimumBuild;
        }

        /// <summary>
        /// Glass only where Windows itself shows glass. With transparency
        /// effects off DWM paints a flat grey instead of acrylic, which would
        /// wash out the petrol tint, so Boostix stays opaque.
        /// </summary>
        internal static bool ShouldUseGlass(
            int build,
            bool highContrast,
            bool transparencyEffects)
        {
            return IsSupportedBuild(build) && !highContrast && transparencyEffects;
        }

        /// <summary>
        /// This Windows build can draw the glass at all. DWM paints no
        /// backdrop behind a frameless popup, so on these builds the main
        /// window keeps a system frame that WindowChrome hides.
        /// </summary>
        internal static bool BuildSupportsGlass
        {
            get { return IsSupportedBuild(CurrentBuild()); }
        }

        /// <summary>
        /// True when the glass can be requested now. Read again whenever
        /// Windows announces a settings change.
        /// </summary>
        internal static bool IsAvailable
        {
            get
            {
                return ShouldUseGlass(
                    CurrentBuild(),
                    SystemParameters.HighContrast,
                    TransparencyEffectsEnabled());
            }
        }

        /// <summary>
        /// RtlGetVersion reports the real build regardless of the
        /// application manifest; 0 when it cannot be read.
        /// </summary>
        private static int CurrentBuild()
        {
            try
            {
                var information = new OsVersionInfo
                {
                    Size = Marshal.SizeOf(typeof(OsVersionInfo))
                };
                return RtlGetVersion(ref information) == 0 &&
                    information.MajorVersion >= 10
                    ? information.BuildNumber
                    : 0;
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
            catch (EntryPointNotFoundException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Settings > Personalization > Colors > Transparency effects.
        /// Windows treats a missing value as on.
        /// </summary>
        private static bool TransparencyEffectsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("EnableTransparency");
                    return !(value is int) || (int)value != 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Puts dark acrylic behind the window and makes the WPF surface
        /// transparent so the tint layers above it show the glass. Returns
        /// false and leaves the window opaque when Windows refuses.
        /// </summary>
        internal static bool TryApply(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsAvailable)
            {
                return false;
            }
            HwndSource source = HwndSource.FromHwnd(window);
            if (source == null || source.CompositionTarget == null)
            {
                return false;
            }
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(
                    window,
                    DwmUseImmersiveDarkMode,
                    ref dark,
                    Marshal.SizeOf(typeof(int)));
                // With "accent colour on title bars" on, DWM would paint the
                // hidden caption strip in the accent colour over the glass.
                int captionColor = DwmColorNone;
                DwmSetWindowAttribute(
                    window,
                    DwmCaptionColor,
                    ref captionColor,
                    Marshal.SizeOf(typeof(int)));
                var margins = new FrameMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                if (DwmExtendFrameIntoClientArea(window, ref margins) != 0)
                {
                    return false;
                }
                int backdrop = BackdropAcrylic;
                if (DwmSetWindowAttribute(
                        window,
                        DwmSystemBackdropType,
                        ref backdrop,
                        Marshal.SizeOf(typeof(int))) != 0)
                {
                    Remove(window);
                    return false;
                }
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the window to an opaque surface, for example when the user
        /// switches High Contrast on while Boostix is open.
        /// </summary>
        internal static void Remove(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return;
            }
            try
            {
                int backdrop = BackdropNone;
                DwmSetWindowAttribute(
                    window,
                    DwmSystemBackdropType,
                    ref backdrop,
                    Marshal.SizeOf(typeof(int)));
                var margins = new FrameMargins();
                DwmExtendFrameIntoClientArea(window, ref margins);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            HwndSource source = HwndSource.FromHwnd(window);
            if (source != null && source.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = SystemColors.WindowColor;
            }
        }
    }
}
