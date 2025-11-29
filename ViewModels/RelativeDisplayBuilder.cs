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
    /// Uses "Synthetic Time" logic to normalize gaps across different tracks and car classes.
    /// </summary>
    public class RelativeDisplayBuilder
    {
        #region Constants

        // "Synthetic Time" Thresholds (Seconds)
        // Normalized for reaction time + physics, regardless of track length.
        private const float TIME_SEG5_CRITICAL = 0.6f;  // < 0.6s (Contact Imminent / Netcode zone)
        private const float TIME_SEG4_DANGER = 1.5f;  // < 1.5s (Drafting / Attack Range)
        private const float TIME_SEG3_WARNING = 3.0f;  // < 3.0s (Hunting / Mirrors Full)
        private const float TIME_SEG2_AWARE = 5.0f;  // < 5.0s (Traffic Monitoring)
        private const float TIME_SEG1_INFO = 8.0f;  // < 8.0s (On Radar)

        // Minimum speed floor to prevent "Limp Mode" false security.
        // 35 m/s is approx 78 mph / 126 kph.
        private const float MIN_SPEED_FLOOR_MS = 35.0f;

        private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#80404040");
        private static readonly Color AheadAlertColor = (Color)ColorConverter.ConvertFromString("#FF00FFFF");
        private static readonly Color BehindAlertColor = (Color)ColorConverter.ConvertFromString("#FFFF9900");
        #endregion

        #region Private Fields
        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;
        private readonly PositionCalculator _positionCalculator;
        private readonly ClassPaceManager _paceManager;
        #endregion

        #region Constructor
        public RelativeDisplayBuilder(
            Dictionary<int, RelativeRowViewModel> carCache,
            ClassColorManager classColorManager,
            PositionCalculator positionCalculator,
            ClassPaceManager paceManager)
        {
            _carCache = carCache;
            _classColorManager = classColorManager;
            _positionCalculator = positionCalculator;
            _paceManager = paceManager;
        }
        #endregion

        #region Public Methods
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

            var validCarIndices = _positionCalculator.ValidCarIndices;

            var allValidCars = BuildValidCarsList(validCarIndices, carNumbers, userNames, carIsAI,
                carClassIDs, incidentCounts, currentLap, onPitRoad, playerCarIdx);

            if (!allValidCars.Any())
            {
                return new List<RelativeRowViewModel>();
            }

            List<RelativeRowViewModel> finalRows = BuildProximityBasedRows(allValidCars);

            bool useFastestLap = dataProvider.ShouldUseFastestLapPositioning();
            ApplyDisplayLogic(finalRows, useFastestLap, dataProvider, carClassColors, carClassIDs, snapshot);

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

                    float effectiveLapDistPct = _positionCalculator.GetEffectiveLapDistPct(i);

                    row.CarIdx = i;
                    row.IsPlayer = (i == playerCarIdx);
                    row.CurrentLap = currentLap[i];
                    row.LapDistPct = effectiveLapDistPct;
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
            int[] carClassIDs,
            SVappsLABSnapshot snapshot)
        {
            var playerRow = displayRows.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            foreach (var row in displayRows)
            {
                AssignClassPositionDisplay(row, isFastestLapMode, dataProvider);
                AssignNameColor(row, playerRow);
                AssignClassBackgroundColor(row, playerRow, carClassColors, carClassIDs);
                AssignFontStyle(row);
                AssignProximitySegments(row, playerRow, snapshot);
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

        private void AssignProximitySegments(RelativeRowViewModel row, RelativeRowViewModel playerRow, SVappsLABSnapshot snapshot)
        {
            // Reset Segments
            row.Segment1Color = Brushes.Transparent;
            row.Segment2Color = Brushes.Transparent;
            row.Segment3Color = Brushes.Transparent;
            row.Segment4Color = Brushes.Transparent;
            row.Segment5Color = Brushes.Transparent;

            if (row.IsPlayer) return;

            // 1. Get Data Points
            float trackLength = snapshot.GetValue<float>("TrackLength", 0f);
            float playerSpeed = snapshot.GetValue<float>("Speed", 0f);

            // Safety Check: If track length is missing, we can't do math.
            if (trackLength <= 0) return;

            // 2. Calculate Distance (Meters)
            float deltaPct = row.LapDistPct - playerRow.LapDistPct;

            // Handle Wrap-Around (Ring Logic)
            if (deltaPct > 0.5f) deltaPct -= 1.0f;
            else if (deltaPct < -0.5f) deltaPct += 1.0f;

            float distanceMeters = Math.Abs(deltaPct * trackLength);
            bool isAhead = deltaPct > 0; // True if they are physically ahead of us on track

            // 3. Determine Threat Speed (The Scalar Magic)
            // Goal: How fast is the "Gap" closing/opening relative to race pace?
            float calculationSpeed;

            if (isAhead)
            {
                // If they are ahead, we use OUR speed (We are catching them).
                // This naturally solves the "Accordion Effect" when we brake for corners.
                calculationSpeed = playerSpeed;
            }
            else
            {
                // If they are behind, we use THEIR estimated speed (They are catching us).
                // Use ClassPaceManager to get the relative performance scalar (e.g., GTP vs Miata).
                float scalar = _paceManager.GetThreatScalar(playerRow.ClassID, row.ClassID);

                // "Estimated Threat Speed" = My Speed * Scalar
                // Example: If I'm Miata (50mps) and they are GTP (Scalar 1.45), result is 72.5mps.
                float estimatedThreatSpeed = playerSpeed * scalar;

                // Use the higher of the two to be safe.
                // If I brake to 50mph, use their estimated 180mph (derived from scalar).
                calculationSpeed = Math.Max(playerSpeed, estimatedThreatSpeed);
            }

            // 4. Apply Speed Floor (Limp Mode Protection)
            // Ensures that even if we are stopped (0 mps), we calculate gap based on racing speed.
            calculationSpeed = Math.Max(calculationSpeed, MIN_SPEED_FLOOR_MS);

            // 5. Calculate Synthetic Time Gap
            // This converts Meters into Seconds-to-Impact
            float syntheticTimeGap = distanceMeters / calculationSpeed;

            // Pass this raw time to the RowViewModel for smoothing (prevents visual flicker)
            row.UpdateSmoothedGap(syntheticTimeGap);
            float displayGap = row.SmoothedGap;

            // 6. Light up Segments based on Time Thresholds
            Color alertColor = isAhead ? AheadAlertColor : BehindAlertColor;

            if (displayGap <= TIME_SEG1_INFO)
                row.Segment1Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.0));

            if (displayGap <= TIME_SEG2_AWARE)
                row.Segment2Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.25));

            if (displayGap <= TIME_SEG3_WARNING)
                row.Segment3Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.50));

            if (displayGap <= TIME_SEG4_DANGER)
                row.Segment4Color = new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.75));

            if (displayGap <= TIME_SEG5_CRITICAL)
                row.Segment5Color = new SolidColorBrush(alertColor);
        }

        private Color BlendColors(Color color1, Color color2, double ratio)
        {
            byte r = (byte)(color1.R + (color2.R - color1.R) * ratio);
            byte g = (byte)(color1.G + (color2.G - color1.G) * ratio);
            byte b = (byte)(color1.B + (color2.B - color1.B) * ratio);
            byte a = (byte)(color1.A + (color2.A - color1.A) * ratio);
            return Color.FromArgb(a, r, g, b);
        }
        #endregion
    }
}