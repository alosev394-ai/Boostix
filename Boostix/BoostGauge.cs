using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

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
    /// session starts, and settles in the Boost zone while it runs.
    /// </summary>
    internal sealed class BoostGauge : Grid
    {
        public const double GaugeWidth = 224;
        public const double GaugeHeight = 196;

        private const double StartAngle = -120;
        private const double EndAngle = 120;
        private const double ZoneStartAngle = 60;
        private const double RestAngle = 90;
        private const double Radius = 94;
        private const double CenterX = GaugeWidth / 2;
        private const double CenterY = 104;

        private readonly List<Line> scaleTicks = new List<Line>();
        private readonly List<Line> zoneTicks = new List<Line>();
        private readonly Path zoneBand;
        private readonly Path needle;
        private readonly RotateTransform needleRotation;
        private readonly Ellipse hub;
        private readonly TextBlock readout;
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
                canvas.Children.Add(tick);
                (inZone ? zoneTicks : scaleTicks).Add(tick);
            }

            zoneBand = new Path
            {
                Data = MakeArc(ZoneStartAngle, EndAngle, Radius - 17),
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat
            };
            canvas.Children.Add(zoneBand);

            offLabel = MakeLabel("выкл", StartAngle, false);
            canvas.Children.Add(offLabel);
            boostLabel = MakeLabel("Boost", EndAngle, true);
            canvas.Children.Add(boostLabel);

            readout = new TextBlock
            {
                FontFamily = BoostixDesignTokens.DisplayFontFamily,
                FontWeight = FontWeights.SemiBold,
                FontSize = 22,
                TextAlignment = TextAlignment.Center,
                Width = 120
            };
            Canvas.SetLeft(readout, CenterX - 60);
            Canvas.SetTop(readout, CenterY + 22);
            canvas.Children.Add(readout);

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
                    System.Globalization.CultureInfo.InvariantCulture,
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
            SetReadout("Пуск", string.Empty);
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
            readout.Text = value ?? string.Empty;
            readoutCaption.Text = caption ?? string.Empty;
        }

        public void RefreshTheme()
        {
            ApplyColors();
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
            readout.Foreground = BoostixDesignTokens.Brush(
                state == BoostGaugeState.Active ? BoostixDesignTokens.AccentText : BoostixDesignTokens.Text);
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
}
