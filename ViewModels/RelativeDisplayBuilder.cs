using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Builds the 7-row proximity-based relative display with visual properties.
    /// Uses PositionCalculator for all filtering, validation, and prediction.
    /// Focuses purely on proximity sorting and visual styling.
    /// </summary>
    public class RelativeDisplayBuilder
    {
        #region Constants
        // Segment thresholds (exponential curve, gentle)
        private const double SEGMENT_1_THRESHOLD = 0.100;  // Farthest
        private const double SEGMENT_2_THRESHOLD = 0.075;
        private const double SEGMENT_3_THRESHOLD = 0.055;
        private const double SEGMENT_4_THRESHOLD = 0.035;
        private const double SEGMENT_5_THRESHOLD = 0.018;  // Closest

        private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#80404040");
        private static readonly Color AheadAlertColor = (Color)ColorConverter.ConvertFromString("#FF00FFFF");
        private static readonly Color BehindAlertColor = (Color)ColorConverter.ConvertFromString("#FFFF9900");
        #endregion

        #region Private Fields
        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;
        private readonly PositionCalculator _positionCalculator;
        #endregion

        #region Constructor
        public RelativeDisplayBuilder(
            Dictionary<int, RelativeRowViewModel> carCache,
            ClassColorManager classColorManager,
            PositionCalculator positionCalculator)
        {
            _carCache = carCache;
            _classColorManager = classColorManager;
            _positionCalculator = positionCalculator;
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Calculate the 7-row proximity display (3 ahead, player, 3 behind).
        /// Uses PositionCalculator's filtered roster and effective (predicted) positions.
        /// </summary>
        public List<RelativeRowViewModel> Calculate(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");

            var carClassIDs = dataProvider.CarClassIDs;
            var carClassColors = dataProvider.CarClassColors;
            var userNames = dataProvider.UserNames;
            var carNumbers = dataProvider.CarNumbers;
            var carIsAI = dataProvider.CarIsAI;
            var incidentCounts = dataProvider.CurDriverIncidentCount;
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad");

            // Get valid cars from PositionCalculator
            // This list is already filtered to: (1) YAML data exists, (2) HasEverHadValidData, (3) Cache valid
            var validCarIndices = _positionCalculator.ValidCarIndices;

            var allValidCars = BuildValidCarsList(validCarIndices, carNumbers, userNames, carIsAI,
                carClassIDs, incidentCounts, currentLap, onPitRoad, playerCarIdx);

            if (!allValidCars.Any())
            {
                return new List<RelativeRowViewModel>();
            }

            List<RelativeRowViewModel> finalRows = BuildProximityBasedRows(allValidCars);

            bool useFastestLap = dataProvider.ShouldUseFastestLapPositioning();
            ApplyDisplayLogic(finalRows, useFastestLap, dataProvider, carClassColors, carClassIDs);

            return finalRows;
        }

        public void Reset() { }
        #endregion

        #region Private Methods - Car List Building
        private List<RelativeRowViewModel> BuildValidCarsList(
            IReadOnlySet<int> validCarIndices,
            string[] carNumbers,
            string[] userNames,
            bool[] carIsAI,
            int[] carClassIDs,
            int[] incidentCounts,
            int[] currentLap,
            bool[] onPitRoad,
            int playerCarIdx)
        {
            var allValidCars = new List<RelativeRowViewModel>();

            // PositionCalculator has already filtered this list to valid cars only
            // We just need to build the view models
            foreach (int i in validCarIndices)
            {
                if (i >= 0 && i < carNumbers.Length &&
                    !string.IsNullOrEmpty(carNumbers[i]) &&
                    !string.IsNullOrEmpty(userNames[i]))
                {
                    string displayName = carIsAI[i] ? $"🤖 {userNames[i]}" : userNames[i];

                    if (!_carCache.TryGetValue(i, out var row))
                    {
                        row = new RelativeRowViewModel();
                        _carCache[i] = row;
                    }

                    // CRITICAL: Use PositionCalculator's effective LapDistPct (current OR predicted)
                    // This ensures smooth display during brief data gaps
                    float effectiveLapDistPct = _positionCalculator.GetEffectiveLapDistPct(i);

                    row.CarIdx = i;
                    row.IsPlayer = (i == playerCarIdx);
                    row.CurrentLap = currentLap[i];
                    row.LapDistPct = effectiveLapDistPct; // Use predicted value if needed
                    row.Name = displayName;
                    row.CarNum = carNumbers[i];
                    row.ClassID = carClassIDs[i];
                    row.IncidentCount = incidentCounts[i];
                    row.IsOnPitRoad = (onPitRoad != null && i < onPitRoad.Length) && onPitRoad[i];

                    allValidCars.Add(row);
                }
            }

            return allValidCars;
        }
        #endregion

        #region Private Methods - Proximity Sorting
        private List<RelativeRowViewModel> BuildProximityBasedRows(List<RelativeRowViewModel> allCars)
        {
            var playerRow = allCars.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return new List<RelativeRowViewModel>();

            float playerTrackPercent = playerRow.LapDistPct;

            // No need to filter LapDistPct here - PositionCalculator already provides clean data
            // All cars in this list have valid effective LapDistPct (current or predicted)
            var otherCars = allCars.Where(c => !c.IsPlayer).Select(car =>
            {
                float directDistance = Math.Abs(car.LapDistPct - playerTrackPercent);
                float proximity = Math.Min(directDistance, 1.0f - directDistance);
                bool isAhead = (car.LapDistPct - playerTrackPercent + 1.5f) % 1.0f > 0.5f;
                return new { Car = car, Proximity = proximity, IsAhead = isAhead };
            }).ToList();

            var carsAhead = otherCars.Where(x => x.IsAhead).OrderBy(x => x.Proximity).Select(x => x.Car).ToList();
            var carsBehind = otherCars.Where(x => !x.IsAhead).OrderBy(x => x.Proximity).Select(x => x.Car).ToList();

            var result = new List<RelativeRowViewModel>();
            result.AddRange(carsAhead.Take(3).Reverse());
            result.Add(playerRow);
            result.AddRange(carsBehind.Take(3));

            return result;
        }
        #endregion

        #region Private Methods - Display Styling
        private void ApplyDisplayLogic(
            List<RelativeRowViewModel> displayRows,
            bool isFastestLapMode,
            ISessionDataProvider dataProvider,
            int[] carClassColors,
            int[] carClassIDs)
        {
            var playerRow = displayRows.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            foreach (var row in displayRows)
            {
                AssignClassPositionDisplay(row, isFastestLapMode, dataProvider);
                AssignNameColor(row, playerRow);
                AssignClassBackgroundColor(row, playerRow, carClassColors, carClassIDs);
                AssignFontStyle(row);
                AssignProximitySegments(row, playerRow);
            }
        }

        private void AssignClassPositionDisplay(
            RelativeRowViewModel row,
            bool isFastestLapMode,
            ISessionDataProvider dataProvider)
        {
            if (isFastestLapMode)
            {
                var fastestLapData = dataProvider.GetFastestLapPositioning();
                var carData = fastestLapData.FirstOrDefault(d => d.carIdx == row.CarIdx);
                row.ClassPos = (carData.fastestTime > 0) ? $"{carData.position}" : "--";
            }
            else
            {
                int position = _positionCalculator.GetClassPosition(row.CarIdx, row.ClassID);
                row.ClassPos = (position > 0) ? $"{position}" : "--";
            }
        }

        private void AssignNameColor(RelativeRowViewModel row, RelativeRowViewModel playerRow)
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

        private void AssignClassBackgroundColor(
            RelativeRowViewModel row,
            RelativeRowViewModel playerRow,
            int[] carClassColors,
            int[] carClassIDs)
        {
            if (row.ClassID == 0) return;
            row.ClassBackground = _classColorManager.GetClassColor(row.ClassID, carClassColors, carClassIDs);
        }

        private void AssignFontStyle(RelativeRowViewModel row)
        {
            row.FontStyle = row.IsOnPitRoad ? FontStyles.Italic : FontStyles.Normal;
        }

        private void AssignProximitySegments(RelativeRowViewModel row, RelativeRowViewModel playerRow)
        {
            // Reset all segments to transparent
            row.Segment1Color = Brushes.Transparent;
            row.Segment2Color = Brushes.Transparent;
            row.Segment3Color = Brushes.Transparent;
            row.Segment4Color = Brushes.Transparent;
            row.Segment5Color = Brushes.Transparent;

            if (row.IsPlayer) return;

            // Calculate proximity distance
            float proximityDistance = Math.Min(
                Math.Abs(row.LapDistPct - playerRow.LapDistPct),
                1.0f - Math.Abs(row.LapDistPct - playerRow.LapDistPct));

            // Determine if car is ahead or behind
            bool isAhead = (row.LapDistPct - playerRow.LapDistPct + 1.5f) % 1.0f > 0.5f;
            Color alertColor = isAhead ? AheadAlertColor : BehindAlertColor;

            // DEBUG: Log proximity and segment activation
            int segmentsLit = 0;
            if (proximityDistance <= SEGMENT_1_THRESHOLD) segmentsLit = 1;
            if (proximityDistance <= SEGMENT_2_THRESHOLD) segmentsLit = 2;
            if (proximityDistance <= SEGMENT_3_THRESHOLD) segmentsLit = 3;
            if (proximityDistance <= SEGMENT_4_THRESHOLD) segmentsLit = 4;
            if (proximityDistance <= SEGMENT_5_THRESHOLD) segmentsLit = 5;

            if (segmentsLit > 0)
            {
                Log.Debug($"[ProximitySegments] Car {row.CarNum}: proximity={proximityDistance:F4}, segments={segmentsLit}, " +
                         $"S1={SEGMENT_1_THRESHOLD}, S2={SEGMENT_2_THRESHOLD}, S3={SEGMENT_3_THRESHOLD}, S4={SEGMENT_4_THRESHOLD}, S5={SEGMENT_5_THRESHOLD}");
            }

            // Light up segments based on proximity with gradual color fade
            if (proximityDistance <= SEGMENT_1_THRESHOLD)
            {
                row.Segment1Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.0));
            }
            if (proximityDistance <= SEGMENT_2_THRESHOLD)
            {
                row.Segment2Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.25));
            }
            if (proximityDistance <= SEGMENT_3_THRESHOLD)
            {
                row.Segment3Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.50));
            }
            if (proximityDistance <= SEGMENT_4_THRESHOLD)
            {
                row.Segment4Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.75));
            }
            if (proximityDistance <= SEGMENT_5_THRESHOLD)
            {
                row.Segment5Color = new SolidColorBrush(alertColor);
            }
        }

        private Color BlendColors(Color color1, Color color2, double ratio)
        {
            // ratio: 0.0 = full color1, 1.0 = full color2
            byte r = (byte)(color1.R + (color2.R - color1.R) * ratio);
            byte g = (byte)(color1.G + (color2.G - color1.G) * ratio);
            byte b = (byte)(color1.B + (color2.B - color1.B) * ratio);
            byte a = (byte)(color1.A + (color2.A - color1.A) * ratio);
            return Color.FromArgb(a, r, g, b);
        }
        #endregion
    }
}