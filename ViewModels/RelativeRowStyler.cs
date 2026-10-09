using System;
using System.Windows;
using System.Windows.Media;

namespace VISOR.ViewModels
{
    /// <summary>
    /// How a relative row looks: name colour, font style, the gap readout and the five proximity
    /// segments. <see cref="RelativeDisplayBuilder"/> measures the gap; this paints it.
    /// </summary>
    internal static class RelativeRowStyler
    {
        // Time gap thresholds (seconds) for proximity segment coloring.
        internal const float TIME_SEG5_CRITICAL = 0.6f;
        internal const float TIME_SEG4_DANGER = 1.5f;
        internal const float TIME_SEG3_WARNING = 3.0f;
        internal const float TIME_SEG2_AWARE = 5.0f;
        internal const float TIME_SEG1_INFO = 8.0f;

        // Deactivation requires exceeding the threshold by this margin to prevent flicker.
        private const float SEGMENT_HYSTERESIS = 0.2f;

        private const float METERS_TO_FEET = 3.28084f;
        private const int MAX_DISTANCE_FEET = 999;

        // Upper sanity bound for the time-gap readout. The history buffer is the real limiter
        // (it can only return a gap as deep as the history it stores); beyond this is noise.
        private const float MAX_TIME_GAP_SECONDS = 600f;

        // The gap is a real transponder-style measurement all the way out to half a lap, which on a
        // long circuit is minutes. Past this it has stopped being proximity information and is just
        // a wide number crowding the column, so it collapses to a bounded "99+" form.
        private const float MAX_PRECISE_GAP_SECONDS = 99.9f;

        private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#80404040");
        private static readonly Color AheadAlertColor = (Color)ColorConverter.ConvertFromString("#FF00FFFF");
        private static readonly Color BehindAlertColor = (Color)ColorConverter.ConvertFromString("#FFFF9900");
        private static readonly Color PitGrayColor = (Color)ColorConverter.ConvertFromString("#60808080");

        // Every brush the rows use, built once and frozen, so no frame allocates any. Segment 1 is
        // the neutral colour, each segment after it a quarter closer to the alert colour, and
        // segment 5 the alert colour itself.
        private static readonly SolidColorBrush PitGrayBrush = Frozen(PitGrayColor);
        private static readonly SolidColorBrush StationaryYellowBrush = Frozen(Colors.Yellow);
        private static readonly SolidColorBrush[] AheadSegmentBrushes = BuildSegmentBrushes(AheadAlertColor);
        private static readonly SolidColorBrush[] BehindSegmentBrushes = BuildSegmentBrushes(BehindAlertColor);

        // Segment n lights once the gap is at or under its threshold (segment 1 = widest gap).
        private static readonly float[] SegmentThresholds =
            { TIME_SEG1_INFO, TIME_SEG2_AWARE, TIME_SEG3_WARNING, TIME_SEG4_DANGER, TIME_SEG5_CRITICAL };

        public static void AssignNameColor(RelativeRowViewModel row, RelativeRowViewModel playerRow)
        {
            if (row.IsPlayer)
                row.NameColor = Brushes.Yellow;
            else if (row.CurrentLap > playerRow.CurrentLap)
                row.NameColor = Brushes.Red;
            else if (row.CurrentLap < playerRow.CurrentLap)
                row.NameColor = Brushes.CornflowerBlue;
            else
                row.NameColor = Brushes.White;
        }

        public static void AssignFontStyle(RelativeRowViewModel row)
        {
            row.FontStyle = row.IsOnPitRoad ? FontStyles.Italic : FontStyles.Normal;
        }

        /// <summary>The player's own row: no gap.</summary>
        public static void ShowNoGap(RelativeRowViewModel row)
        {
            row.GapText = string.Empty;
            ClearSegments(row);
        }

        public static void ShowInPits(RelativeRowViewModel row)
        {
            row.GapText = "PIT";
            row.GapColor = PitGrayBrush;
            row.Segment1Color = PitGrayBrush;
            row.Segment2Color = PitGrayBrush;
            row.Segment3Color = PitGrayBrush;
            row.Segment4Color = PitGrayBrush;
            row.Segment5Color = PitGrayBrush;
        }

        /// <summary>
        /// A stopped car: its distance in feet, or nothing when the track length (and so the
        /// distance) isn't known.
        /// </summary>
        public static void ShowStationary(RelativeRowViewModel row, float? distanceMeters)
        {
            if (distanceMeters.HasValue)
            {
                int distanceFeet = Math.Min((int)(distanceMeters.Value * METERS_TO_FEET), MAX_DISTANCE_FEET);

                row.GapText = $"{distanceFeet}ft";
                row.GapColor = StationaryYellowBrush;
                row.GapFontWeight = FontWeights.Bold;
            }
            else
            {
                row.GapText = string.Empty;
            }
            ClearSegments(row);
        }

        /// <summary>The time gap readout and the proximity segments it lights.</summary>
        public static void ShowGap(RelativeRowViewModel row, float displayGap, bool isAhead)
        {
            // Show the gap whenever it's in a sane range. The history buffer naturally limits how
            // far back a crossing can be found, so this just guards against garbage values.
            if (displayGap > 0 && displayGap < MAX_TIME_GAP_SECONDS)
            {
                string sign = isAhead ? "+" : "-";
                row.GapText = (displayGap > MAX_PRECISE_GAP_SECONDS)
                    ? $"{sign}99+"
                    : $"{sign}{displayGap:F1}";
            }
            else
            {
                row.GapText = string.Empty;
            }

            row.GapColor = Brushes.White;
            row.GapFontWeight = FontWeights.SemiBold;

            // Count active segments. Hysteresis: once lit, a segment stays on until
            // the gap exceeds its threshold + margin, preventing flicker at boundaries.
            int prevSegments = row._lastActiveSegmentCount;
            int activeSegments = 0;

            for (int s = 0; s < SegmentThresholds.Length; s++)
            {
                float deactivateAt = (s < prevSegments) ? SegmentThresholds[s] + SEGMENT_HYSTERESIS : SegmentThresholds[s];
                if (displayGap <= deactivateAt)
                    activeSegments = s + 1;
            }

            // Explicitly set every segment — either colored or Transparent — so segments
            // that are no longer active get cleared without needing a blanket reset at the top.
            var segmentBrushes = isAhead ? AheadSegmentBrushes : BehindSegmentBrushes;
            row.Segment1Color = (activeSegments >= 1) ? segmentBrushes[0] : Brushes.Transparent;
            row.Segment2Color = (activeSegments >= 2) ? segmentBrushes[1] : Brushes.Transparent;
            row.Segment3Color = (activeSegments >= 3) ? segmentBrushes[2] : Brushes.Transparent;
            row.Segment4Color = (activeSegments >= 4) ? segmentBrushes[3] : Brushes.Transparent;
            row.Segment5Color = (activeSegments >= 5) ? segmentBrushes[4] : Brushes.Transparent;

            row._lastActiveSegmentCount = activeSegments;
        }

        private static void ClearSegments(RelativeRowViewModel row)
        {
            row.Segment1Color = Brushes.Transparent;
            row.Segment2Color = Brushes.Transparent;
            row.Segment3Color = Brushes.Transparent;
            row.Segment4Color = Brushes.Transparent;
            row.Segment5Color = Brushes.Transparent;
            row._lastActiveSegmentCount = 0;
        }

        private static SolidColorBrush[] BuildSegmentBrushes(Color alertColor) => new[]
        {
            Frozen(BlendColors(NeutralColor, alertColor, 0.0)),
            Frozen(BlendColors(NeutralColor, alertColor, 0.25)),
            Frozen(BlendColors(NeutralColor, alertColor, 0.50)),
            Frozen(BlendColors(NeutralColor, alertColor, 0.75)),
            Frozen(alertColor)
        };

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Color BlendColors(Color color1, Color color2, double ratio)
        {
            byte r = (byte)(color1.R + (color2.R - color1.R) * ratio);
            byte g = (byte)(color1.G + (color2.G - color1.G) * ratio);
            byte b = (byte)(color1.B + (color2.B - color1.B) * ratio);
            byte a = (byte)(color1.A + (color2.A - color1.A) * ratio);
            return Color.FromArgb(a, r, g, b);
        }
    }
}
