using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Boostix
{
    internal enum BoostGaugeState
    {
        Idle,
        Working,
        Active
    }

    /// <summary>
    /// The Boost control drawn as a boost-pressure gauge: a 240° scale with an
    /// lemon Boost zone at the top end. The needle rests at the left stop
    /// while Boost is off, does the ignition sweep to the end stop while the
    /// session starts, and settles in the Boost zone while it runs, where it
    /// follows the processor load.
    /// </summary>
    internal sealed class BoostGauge : Grid
    {
        public const double GaugeWidth = 224;
        public const double GaugeHeight = 196;

        private const double StartAngle = -120;
        private const double EndAngle = 120;
        private const double ZoneStartAngle = 60;
        private const double RestAngle = 90;
        private const double LiveMinAngle = 68;
        private const double LiveMaxAngle = 112;
        private const double Radius = 94;
        private const double CenterX = GaugeWidth / 2;
        private const double CenterY = 104;

        // Self-test on start-up, like a dashboard when the ignition turns:
        // the needle decelerates to the end stop, holds, and returns.
        private const int SelfTestUpMilliseconds = 620;
        private const int SelfTestHoldMilliseconds = 140;
        private const int SelfTestDownMilliseconds = 560;
        private const int TickLightMilliseconds = 160;
        private const double DimTickOpacity = 0.2;
        private const int LiveNeedleMilliseconds = 650;

        private readonly List<Line> scaleTicks = new List<Line>();
        private readonly List<Line> zoneTicks = new List<Line>();
        private readonly List<Line> allTicks = new List<Line>();
        private readonly Path zoneBand;
        private readonly Path needle;
        private readonly RotateTransform needleRotation;
        private readonly Ellipse hub;
        private readonly RollingReadout readout;
        private readonly TextBlock readoutCaption;
        private readonly TextBlock offLabel;
        private readonly TextBlock boostLabel;
        private BoostGaugeState state = BoostGaugeState.Idle;
        private bool hovered;

        public BoostGauge()
        {
            Width = GaugeWidth;
            Height = GaugeHeight;
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;

            var canvas = new Canvas { Width = GaugeWidth, Height = GaugeHeight };
            Children.Add(canvas);

            for (double angle = StartAngle; angle <= EndAngle + 0.1; angle += 10)
            {
                bool major = Math.Abs(angle % 30) < 0.1;
                bool inZone = angle >= ZoneStartAngle - 0.1;
                double length = major ? 11 : 6;
                Line tick = MakeRadialLine(angle, Radius - length, Radius, major ? 2 : 1.2);
                tick.Tag = angle;
                canvas.Children.Add(tick);
                (inZone ? zoneTicks : scaleTicks).Add(tick);
                allTicks.Add(tick);
            }

            zoneBand = new Path
            {
                Data = MakeArc(ZoneStartAngle, EndAngle, Radius - 17),
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat
            };
            canvas.Children.Add(zoneBand);

            offLabel = MakeLabel(L.T("выкл"), StartAngle, false);
            canvas.Children.Add(offLabel);
            boostLabel = MakeLabel("Boost", EndAngle, true);
            canvas.Children.Add(boostLabel);

            readout = new RollingReadout(
                BoostixDesignTokens.DisplayFontFamily,
                22,
                FontWeights.SemiBold);
            var readoutHost = new Grid { Width = 120 };
            readoutHost.Children.Add(readout);
            Canvas.SetLeft(readoutHost, CenterX - 60);
            Canvas.SetTop(readoutHost, CenterY + 22);
            canvas.Children.Add(readoutHost);

            readoutCaption = new TextBlock
            {
                FontFamily = BoostixDesignTokens.TextFontFamily,
                FontSize = BoostixDesignTokens.MetadataTextSize,
                TextAlignment = TextAlignment.Center,
                Width = 120
            };
            Canvas.SetLeft(readoutCaption, CenterX - 60);
            Canvas.SetTop(readoutCaption, CenterY + 50);
            canvas.Children.Add(readoutCaption);

            needleRotation = new RotateTransform(StartAngle, CenterX, CenterY);
            needle = new Path
            {
                Data = Geometry.Parse(string.Format(
                    CultureInfo.InvariantCulture,
                    "M {0},{1} L {2},{3} L {4},{3} L {5},{1} L {6},{7} L {8},{7} Z",
                    CenterX - 2.6, CenterY,
                    CenterX - 0.8, CenterY - (Radius - 20),
                    CenterX + 0.8,
                    CenterX + 2.6,
                    CenterX + 1.6, CenterY + 13,
                    CenterX - 1.6)),
                RenderTransform = needleRotation
            };
            canvas.Children.Add(needle);

            hub = new Ellipse
            {
                Width = 16,
                Height = 16,
                StrokeThickness = 2
            };
            Canvas.SetLeft(hub, CenterX - 8);
            Canvas.SetTop(hub, CenterY - 8);
            canvas.Children.Add(hub);

            ApplyColors();
            SetReadout(L.T("Пуск"), string.Empty);
        }

        public BoostGaugeState State
        {
            get { return state; }
        }

        public void SetState(BoostGaugeState value)
        {
            state = value;
            ApplyColors();
        }

        public void SetHover(bool value)
        {
            hovered = value;
            ApplyColors();
        }

        public void SetReadout(string value, string caption)
        {
            readout.SetText(value ?? string.Empty);
            readoutCaption.Text = caption ?? string.Empty;
        }

        public void RefreshTheme()
        {
            ApplyColors();
        }

        /// <summary>
        /// Start-up self-test: the scale lights up behind the needle as it
        /// swings to the end stop, then the needle returns to rest. Skipped
        /// when Windows animations are off or Boost is already starting.
        /// </summary>
        public void PlaySelfTest()
        {
            if (!BoostixDesignTokens.MotionEnabled || state != BoostGaugeState.Idle)
            {
                return;
            }
            foreach (Line tick in allTicks)
            {
                LightUp(tick, SelfTestTickDelay((double)tick.Tag, SelfTestUpMilliseconds));
            }
            LightUp(zoneBand, SelfTestTickDelay(ZoneStartAngle, SelfTestUpMilliseconds));
            RotateNeedle(
                EndAngle,
                SelfTestUpMilliseconds,
                new CubicEase { EasingMode = EasingMode.EaseOut },
                delegate
                {
                    if (state != BoostGaugeState.Idle)
                    {
                        return;
                    }
                    var hold = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(SelfTestHoldMilliseconds)
                    };
                    hold.Tick += delegate
                    {
                        hold.Stop();
                        if (state == BoostGaugeState.Idle)
                        {
                            RotateNeedle(
                                StartAngle,
                                SelfTestDownMilliseconds,
                                new CubicEase { EasingMode = EasingMode.EaseInOut },
                                null);
                        }
                    };
                    hold.Start();
                });
        }

        /// <summary>
        /// While Boost runs, the needle moves inside the Boost zone with the
        /// processor load (0..1) and settles like a damped instrument needle.
        /// </summary>
        public void SetLiveLoad(double load)
        {
            if (state != BoostGaugeState.Active ||
                !BoostixDesignTokens.MotionEnabled ||
                double.IsNaN(load))
            {
                return;
            }
            double target = LiveAngle(load);
            if (Math.Abs(target - needleRotation.Angle) < 0.75)
            {
                return;
            }
            RotateNeedle(
                target,
                LiveNeedleMilliseconds,
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 },
                null);
        }

        /// <summary>
        /// Needle angle for a processor load: inside the Boost zone with a
        /// margin from its edges; unknown load reads as idle.
        /// </summary>
        internal static double LiveAngle(double load)
        {
            double clamped = double.IsNaN(load) ? 0 : Math.Max(0, Math.Min(1, load));
            return LiveMinAngle + (LiveMaxAngle - LiveMinAngle) * clamped;
        }

        /// <summary>
        /// When the decelerating (cubic ease-out) needle passes a scale angle
        /// during the self-test sweep of the given duration.
        /// </summary>
        internal static double SelfTestTickDelay(double angle, int sweepMilliseconds)
        {
            double progress = Math.Max(0, Math.Min(1, (angle - StartAngle) / (EndAngle - StartAngle)));
            double time = 1 - Math.Pow(1 - progress, 1.0 / 3.0);
            return sweepMilliseconds * time;
        }

        /// <summary>
        /// First half of the ignition sweep: up to the end stop.
        /// </summary>
        public void SweepToEndStop(Action completed)
        {
            RotateNeedle(EndAngle, 560, new CubicEase { EasingMode = EasingMode.EaseInOut }, completed);
        }

        /// <summary>
        /// Second half: back from the end stop into the Boost zone.
        /// </summary>
        public void SettleInBoostZone(Action completed)
        {
            RotateNeedle(RestAngle, 520, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }, completed);
        }

        public void SweepToRest(Action completed)
        {
            RotateNeedle(StartAngle, 640, new CubicEase { EasingMode = EasingMode.EaseInOut }, completed);
        }

        public void SnapNeedle(bool boostZone)
        {
            needleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            needleRotation.Angle = boostZone ? RestAngle : StartAngle;
        }

        private static void LightUp(UIElement element, double delayMilliseconds)
        {
            var light = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            light.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                DimTickOpacity,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));
            light.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                DimTickOpacity,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMilliseconds))));
            light.KeyFrames.Add(new EasingDoubleKeyFrame(
                1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMilliseconds + TickLightMilliseconds)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            element.BeginAnimation(UIElement.OpacityProperty, light);
        }

        private void RotateNeedle(double target, int milliseconds, IEasingFunction easing, Action completed)
        {
            if (!BoostixDesignTokens.MotionEnabled)
            {
                needleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                needleRotation.Angle = target;
                if (completed != null)
                {
                    // Keep the same asynchronous order as the animated path.
                    Dispatcher.BeginInvoke(completed);
                }
                return;
            }
            double from = needleRotation.Angle;
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += delegate
            {
                needleRotation.Angle = target;
                if (completed != null)
                {
                    completed();
                }
            };
            needleRotation.Angle = target;
            needleRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void ApplyColors()
        {
            bool running = state != BoostGaugeState.Idle;
            Color scale = running ? BoostixDesignTokens.SecondaryText : BoostixDesignTokens.MutedText;
            foreach (Line tick in scaleTicks)
            {
                tick.Stroke = BoostixDesignTokens.Brush(scale);
            }
            Color lemon = BoostixDesignTokens.Accent;
            byte zoneAlpha = SystemParameters.HighContrast || running
                ? (byte)255
                : (hovered ? (byte)200 : (byte)120);
            Brush zone = BoostixDesignTokens.Brush(BoostixDesignTokens.WithAlpha(lemon, zoneAlpha));
            foreach (Line tick in zoneTicks)
            {
                tick.Stroke = zone;
            }
            zoneBand.Stroke = zone;
            needle.Fill = BoostixDesignTokens.Brush(running ? lemon : BoostixDesignTokens.SecondaryText);
            hub.Fill = BoostixDesignTokens.Brush(BoostixDesignTokens.SurfaceRaised);
            hub.Stroke = BoostixDesignTokens.Brush(running || hovered ? lemon : BoostixDesignTokens.Border);
            readout.SetForeground(BoostixDesignTokens.Brush(
                state == BoostGaugeState.Active ? BoostixDesignTokens.AccentText : BoostixDesignTokens.Text));
            readoutCaption.Foreground = BoostixDesignTokens.Brush(BoostixDesignTokens.MutedText);
            offLabel.Foreground = BoostixDesignTokens.Brush(BoostixDesignTokens.MutedText);
            boostLabel.Foreground = BoostixDesignTokens.Brush(
                running ? BoostixDesignTokens.AccentText : BoostixDesignTokens.MutedText);
        }

        private static Point PointAt(double angle, double radius)
        {
            double radians = angle * Math.PI / 180;
            return new Point(
                CenterX + radius * Math.Sin(radians),
                CenterY - radius * Math.Cos(radians));
        }

        private static Line MakeRadialLine(double angle, double inner, double outer, double thickness)
        {
            Point from = PointAt(angle, inner);
            Point to = PointAt(angle, outer);
            return new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat
            };
        }

        private static Geometry MakeArc(double fromAngle, double toAngle, double radius)
        {
            var figure = new PathFigure { StartPoint = PointAt(fromAngle, radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(
                PointAt(toAngle, radius),
                new Size(radius, radius),
                0,
                toAngle - fromAngle > 180,
                SweepDirection.Clockwise,
                true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static TextBlock MakeLabel(string text, double angle, bool alignLeft)
        {
            Point anchor = PointAt(angle, Radius);
            var label = new TextBlock
            {
                Text = text,
                FontFamily = BoostixDesignTokens.DisplayFontFamily,
                FontSize = BoostixDesignTokens.MetadataTextSize,
                Width = 48,
                TextAlignment = alignLeft ? TextAlignment.Left : TextAlignment.Right
            };
            Canvas.SetLeft(label, alignLeft ? anchor.X - 12 : anchor.X - 36);
            Canvas.SetTop(label, anchor.Y + 6);
            return label;
        }
    }

    /// <summary>
    /// The gauge readout. When a clock value changes, only the digits that
    /// changed roll up like an odometer; words, length changes and switched
    /// off Windows animations replace the text at once.
    /// </summary>
    internal sealed class RollingReadout : StackPanel
    {
        private const int RollMilliseconds = 260;

        private readonly FontFamily fontFamily;
        private readonly double fontSize;
        private readonly FontWeight fontWeight;
        private readonly double cellHeight;
        private readonly double digitWidth;
        private Brush foreground = Brushes.Transparent;
        private string text = string.Empty;

        public RollingReadout(FontFamily family, double size, FontWeight weight)
        {
            fontFamily = family;
            fontSize = size;
            fontWeight = weight;
            cellHeight = Math.Ceiling(size * 1.25);
            Orientation = Orientation.Horizontal;
            HorizontalAlignment = HorizontalAlignment.Center;
            Height = cellHeight;
            var probe = new FormattedText(
                "0",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
                size,
                Brushes.Black,
                1.0);
            // Every digit gets the width of "0", so a rolling 1 does not
            // shift the digits next to it.
            digitWidth = Math.Ceiling(probe.WidthIncludingTrailingWhitespace);
        }

        public string Text
        {
            get { return text; }
        }

        public void SetForeground(Brush brush)
        {
            foreground = brush ?? Brushes.Transparent;
            foreach (UIElement cell in Children)
            {
                var grid = cell as Grid;
                if (grid == null)
                {
                    continue;
                }
                foreach (UIElement child in grid.Children)
                {
                    var glyph = child as TextBlock;
                    if (glyph != null)
                    {
                        glyph.Foreground = foreground;
                    }
                }
            }
        }

        public void SetText(string value)
        {
            value = value ?? string.Empty;
            if (string.Equals(value, text, StringComparison.Ordinal))
            {
                return;
            }
            int[] positions = BoostixDesignTokens.MotionEnabled
                ? ChangedPositions(text, value)
                : null;
            if (positions == null || Children.Count != value.Length)
            {
                Rebuild(value);
            }
            else
            {
                foreach (int position in positions)
                {
                    Roll((Grid)Children[position], value[position]);
                }
            }
            text = value;
        }

        /// <summary>
        /// Positions of the digits that differ between two clock readings of
        /// the same length; null when the change is not a clock tick.
        /// </summary>
        internal static int[] ChangedPositions(string from, string to)
        {
            if (from == null || to == null || from.Length != to.Length ||
                !IsClock(from) || !IsClock(to))
            {
                return null;
            }
            var positions = new List<int>();
            for (int index = 0; index < to.Length; index++)
            {
                if (from[index] != to[index])
                {
                    positions.Add(index);
                }
            }
            return positions.ToArray();
        }

        private static bool IsClock(string value)
        {
            if (value.Length == 0)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!char.IsDigit(character) && character != ':')
                {
                    return false;
                }
            }
            return true;
        }

        private void Rebuild(string value)
        {
            Children.Clear();
            if (value.Length == 0)
            {
                return;
            }
            if (!IsClock(value))
            {
                // Words keep their kerning in a single text run.
                Children.Add(MakeCell(value, double.NaN));
                return;
            }
            foreach (char character in value)
            {
                Children.Add(MakeCell(
                    character.ToString(),
                    char.IsDigit(character) ? digitWidth : double.NaN));
            }
        }

        private Grid MakeCell(string value, double width)
        {
            var cell = new Grid
            {
                Height = cellHeight,
                Width = width,
                ClipToBounds = true
            };
            cell.Children.Add(MakeGlyph(value));
            return cell;
        }

        private TextBlock MakeGlyph(string value)
        {
            var glyph = new TextBlock
            {
                Text = value,
                FontFamily = fontFamily,
                FontSize = fontSize,
                FontWeight = fontWeight,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new TranslateTransform()
            };
            TextOptions.SetTextFormattingMode(glyph, TextFormattingMode.Display);
            return glyph;
        }

        private void Roll(Grid cell, char next)
        {
            // An interrupted roll leaves its outgoing glyph behind; keep only
            // the glyph that is currently shown.
            while (cell.Children.Count > 1)
            {
                cell.Children.RemoveAt(0);
            }
            var outgoing = cell.Children.Count > 0 ? cell.Children[0] as TextBlock : null;
            TextBlock incoming = MakeGlyph(next.ToString());
            cell.Children.Add(incoming);

            var duration = TimeSpan.FromMilliseconds(RollMilliseconds);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            double travel = cellHeight * 0.8;
            ((TranslateTransform)incoming.RenderTransform).BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(travel, 0, duration) { EasingFunction = ease });
            incoming.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            if (outgoing == null)
            {
                return;
            }
            var exit = new DoubleAnimation(0, -travel, duration) { EasingFunction = ease };
            exit.Completed += delegate { cell.Children.Remove(outgoing); };
            ((TranslateTransform)outgoing.RenderTransform).BeginAnimation(
                TranslateTransform.YProperty,
                exit);
            outgoing.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, duration) { EasingFunction = ease });
        }
    }

    /// <summary>
    /// Processor load between two calls from GetSystemTimes: cheap enough
    /// to read once a second while a game runs.
    /// </summary>
    internal sealed class SystemLoadSampler
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;

            public ulong Value
            {
                get { return ((ulong)High << 32) | Low; }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        private ulong lastIdle;
        private ulong lastTotal;
        private bool primed;

        /// <summary>
        /// Busy share of processor time since the previous call (0..1); NaN
        /// on the first call or when Windows cannot report it.
        /// </summary>
        public double Sample()
        {
            FileTime idle;
            FileTime kernel;
            FileTime user;
            if (!GetSystemTimes(out idle, out kernel, out user))
            {
                return double.NaN;
            }
            ulong total = kernel.Value + user.Value;
            double load = primed
                ? LoadBetween(lastIdle, lastTotal, idle.Value, total)
                : double.NaN;
            lastIdle = idle.Value;
            lastTotal = total;
            primed = true;
            return load;
        }

        /// <summary>
        /// Kernel time already contains idle time, so the busy share is one
        /// minus idle over kernel plus user.
        /// </summary>
        internal static double LoadBetween(
            ulong idleBefore,
            ulong totalBefore,
            ulong idleAfter,
            ulong totalAfter)
        {
            if (totalAfter <= totalBefore || idleAfter < idleBefore)
            {
                return double.NaN;
            }
            double elapsed = totalAfter - totalBefore;
            double idle = idleAfter - idleBefore;
            return Math.Max(0, Math.Min(1, 1 - idle / elapsed));
        }
    }
}
