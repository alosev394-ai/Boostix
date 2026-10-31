using System;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Boostix
{
    /// <summary>
    /// A small badge in the top-right corner that confirms Ctrl+Alt+B while a
    /// game has focus. It never takes focus and lets clicks through, so the
    /// game keeps running as if it were not there. Exclusive fullscreen games
    /// do not show other windows; there only the sound is heard.
    /// </summary>
    internal sealed class BoostToast : Window
    {
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020;
        private const long WsExToolWindow = 0x00000080;
        private const long WsExNoActivate = 0x08000000;
        private const double ScreenMargin = 24;
        private const int HoldMilliseconds = 1800;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        private readonly TextBlock title;
        private readonly TextBlock detail;
        private readonly Border card;
        private readonly TranslateTransform slide = new TranslateTransform();
        private readonly DispatcherTimer hold;

        public BoostToast()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Focusable = false;
            IsHitTestVisible = false;
            Title = "Boostix";

            title = BoostixUi.Text(string.Empty, 14, BoostixDesignTokens.Text, true, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.None;
            detail = BoostixUi.Text(
                BoostHotkey.DisplayText,
                BoostixDesignTokens.MetadataTextSize,
                BoostixDesignTokens.MutedText,
                false,
                FontWeights.Normal);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(detail);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement mark = BoostixUi.BrandMark(30);
            mark.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(mark);
            row.Children.Add(text);
            card = new Border
            {
                CornerRadius = new CornerRadius(BoostixDesignTokens.RadiusMedium),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(BoostixDesignTokens.Surface, 0xF2)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.Border),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 20, 11),
                MinWidth = 220,
                Child = row,
                RenderTransform = slide
            };
            Content = card;

            hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMilliseconds) };
            hold.Tick += delegate
            {
                hold.Stop();
                FadeOut();
            };
            SourceInitialized += delegate
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                SetWindowLongPtr(
                    handle,
                    GwlExStyle,
                    new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
            };
        }

        /// <summary>
        /// Shows or updates the badge; it stays for a moment after the last
        /// update and then fades away.
        /// </summary>
        public void ShowMessage(string heading, bool lit)
        {
            title.Text = heading ?? string.Empty;
            title.Foreground = new SolidColorBrush(lit ? BoostixDesignTokens.AccentText : BoostixDesignTokens.Text);
            hold.Stop();
            // Re-measured on every update so a longer line keeps the same
            // right margin.
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Point corner = PlaceInWorkArea(SystemParameters.WorkArea, card.DesiredSize);
            Left = corner.X;
            Top = corner.Y;
            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }
            FadeIn();
            hold.Start();
        }

        /// <summary>
        /// Top-right corner of the work area, clear of the edges.
        /// </summary>
        internal static Point PlaceInWorkArea(Rect workArea, Size toast)
        {
            return new Point(
                workArea.Right - toast.Width - ScreenMargin,
                workArea.Top + ScreenMargin);
        }

        private void FadeIn()
        {
            BeginAnimation(OpacityProperty, null);
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            if (!BoostixDesignTokens.MotionEnabled)
            {
                Opacity = 1;
                slide.Y = 0;
                return;
            }
            var duration = TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionSlowMilliseconds);
            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private void FadeOut()
        {
            if (!BoostixDesignTokens.MotionEnabled)
            {
                Hide();
                return;
            }
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionStandardMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += delegate
            {
                if (!hold.IsEnabled)
                {
                    Hide();
                }
            };
            BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>
    /// A short two-note chime: rising when Boost turns on, falling when it
    /// turns off. Built in memory, quiet, and played without waiting.
    /// </summary>
    internal static class ConfirmationSound
    {
        private const int SampleRate = 44100;
        private const double NoteSeconds = 0.09;
        private const double Amplitude = 0.3;
        private static SoundPlayer onPlayer;
        private static SoundPlayer offPlayer;

        public static void Play(bool on)
        {
            try
            {
                if (on)
                {
                    if (onPlayer == null)
                    {
                        onPlayer = new SoundPlayer(new MemoryStream(BuildWave(true)));
                        onPlayer.Load();
                    }
                    onPlayer.Play();
                }
                else
                {
                    if (offPlayer == null)
                    {
                        offPlayer = new SoundPlayer(new MemoryStream(BuildWave(false)));
                        offPlayer.Load();
                    }
                    offPlayer.Play();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not play the Boost confirmation sound.", ex);
            }
        }

        /// <summary>
        /// 16-bit mono PCM WAV: two sine notes with a soft attack and decay.
        /// </summary>
        internal static byte[] BuildWave(bool on)
        {
            double[] notes = on ? new[] { 660.0, 990.0 } : new[] { 990.0, 660.0 };
            int perNote = (int)(SampleRate * NoteSeconds);
            int total = perNote * notes.Length;
            var samples = new short[total];
            for (int note = 0; note < notes.Length; note++)
            {
                for (int i = 0; i < perNote; i++)
                {
                    double t = (double)i / SampleRate;
                    double attack = Math.Min(1.0, t / 0.005);
                    double decay = Math.Exp(-t * 18);
                    double value = Math.Sin(2 * Math.PI * notes[note] * t) * attack * decay * Amplitude;
                    samples[note * perNote + i] = (short)(value * short.MaxValue);
                }
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                int dataBytes = total * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(SampleRate);
                writer.Write(SampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);
                foreach (short sample in samples)
                {
                    writer.Write(sample);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }
    }

    /// <summary>
    /// One plain sentence after a game closes: how long it ran with Boost and
    /// whether memory was enough.
    /// </summary>
    internal static class SessionSummary
    {
        internal static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes < 1)
            {
                return L.T("меньше минуты");
            }
            int hours = (int)duration.TotalHours;
            return hours > 0
                ? hours + L.T(" ч ") + duration.Minutes + L.T(" мин")
                : duration.Minutes + L.T(" мин");
        }

        internal static string Describe(string targetName, TimeSpan duration, string pressure)
        {
            string game = string.IsNullOrWhiteSpace(targetName)
                ? L.T("Игра")
                : targetName.Trim();
            if (game.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                game = game.Substring(0, game.Length - 4);
            }
            string text = game + ": " + FormatDuration(duration) + L.T(" с Boost.");
            if (string.Equals(pressure, "Critical", StringComparison.OrdinalIgnoreCase))
            {
                return text + L.T(" Памяти не хватало, подробности в «Истории».");
            }
            if (string.Equals(pressure, "Elevated", StringComparison.OrdinalIgnoreCase))
            {
                return text + L.T(" Памяти было впритык, подробности в «Истории».");
            }
            if (string.Equals(pressure, "Normal", StringComparison.OrdinalIgnoreCase))
            {
                return text + L.T(" Памяти хватало.");
            }
            return text;
        }
    }

    /// <summary>
    /// "Start with Windows": one value in the user's Run key that starts
    /// Boostix hidden in the tray. The registry is the source of truth, so
    /// Settings shows what Windows will actually do.
    /// </summary>
    internal static class AutostartRegistration
    {
        internal const string ValueName = "Boostix";
        internal const string TrayArgument = "--tray";
        internal static string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal static string BuildCommand(string executablePath)
        {
            return "\"" + executablePath + "\" " + TrayArgument;
        }

        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key != null && key.GetValue(ValueName) is string;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool Set(bool enabled, string executablePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    if (enabled)
                    {
                        key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not change the Windows start-up entry.", ex);
                return false;
            }
        }
    }
}
