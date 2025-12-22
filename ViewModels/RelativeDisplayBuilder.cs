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
    /// Uses Native CarIdxEstTime for accurate time gap calculations.
    /// </summary>
    public class RelativeDisplayBuilder
    {
        #region Constants

        // Time Gap Thresholds (Seconds) - Based on Native CarIdxEstTime
        private const float TIME_SEG5_CRITICAL = 0.6f;  // < 0.6s (Contact Imminent / Netcode zone)
        private const float TIME_SEG4_DANGER = 1.5f;    // < 1.5s (Drafting / Attack Range)
        private const float TIME_SEG3_WARNING = 3.0f;   // < 3.0s (Hunting / Mirrors Full)
        private const float TIME_SEG2_AWARE = 5.0f;     // < 5.0s (Traffic Monitoring)
        private const float TIME_SEG1_INFO = 8.0f;      // < 8.0s (On Radar)

        // Debug constants
        private const int DEBUG_LOG_INTERVAL = 60; // Log every ~1 second (assuming 60Hz)

        private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#80404040");
        private static readonly Color AheadAlertColor = (Color)ColorConverter.ConvertFromString("#FF00FFFF");
        private static readonly Color BehindAlertColor = (Color)ColorConverter.ConvertFromString("#FFFF9900");
        #endregion

        #region Private Fields
        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;
        private readonly PositionCalculator _positionCalculator;

        // Rolling counter for throttling debug logs
        private int _debugFrameCounter = 0;
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
        public List<RelativeRowViewModel> Calculate(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            // Increment frame counter for logging throttle
            _debugFrameCounter++;

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

        public void Reset()
        {
            _debugFrameCounter = 0;
        }
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
                    // Check if this is the pace car (class ID 11)
                    bool isPaceCar = (carClassIDs[i] == 11);
                    bool isOnPitRoad = (onPitRoad != null && i < onPitRoad.Length) && onPitRoad[i];

                    // Skip pace car if it's on pit road
                    if (isPaceCar && isOnPitRoad)
                    {
                        continue;
                    }

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
                    row.IsOnPitRoad = isOnPitRoad;

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
            int playerLap = playerRow.CurrentLap;

            var otherCars = allCars.Where(c => !c.IsPlayer).Select(car =>
            {
                float proximity;
                bool isAhead;
                int lapDelta = car.CurrentLap - playerLap;

                if (lapDelta == 0)
                {
                    // Same lap - use wrap-around distance
                    float deltaPct = car.LapDistPct - playerTrackPercent;

                    // Handle wrap-around (ring logic)
                    if (deltaPct > 0.5f) deltaPct -= 1.0f;
                    else if (deltaPct < -0.5f) deltaPct += 1.0f;

                    proximity = Math.Abs(deltaPct);
                    isAhead = deltaPct > 0;
                }
                else
                {
                    // Different laps - they're far away
                    // Use a large proximity value so same-lap cars are prioritized
                    proximity = 10.0f + Math.Abs(lapDelta); // Far away, scaled by lap difference
                    isAhead = lapDelta > 0; // Higher lap number = ahead
                }

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

            if (row.IsPlayer)
            {
                // Player row - no gap display
                row.GapText = string.Empty;
                return;
            }

            // --- NATIVE TIME GAP CALCULATION ---
            // Use iRacing's CarIdxEstTime directly - it already accounts for:
            // - Actual current speeds (not lap averages)
            // - Class differences
            // - Damage/fuel/penalties
            // - Acceleration/deceleration

            var estTimes = snapshot.GetValue<float[]>("CarIdxEstTime");
            if (estTimes == null)
            {
                row.GapText = string.Empty;
                return;
            }

            float playerEstTime = estTimes[playerRow.CarIdx];
            float opponentEstTime = estTimes[row.CarIdx];

            float nativeTimeGap = 0f;
            bool isAhead = false;

            // Calculate time gap using Native EstTime
            if (playerEstTime > 0 && opponentEstTime > 0)
            {
                float rawDelta = opponentEstTime - playerEstTime;
                nativeTimeGap = Math.Abs(rawDelta);
                isAhead = rawDelta > 0; // Positive delta = opponent ahead
            }
            else
            {
                // Fallback: If EstTime unavailable, use distance/speed approximation
                float trackLength = snapshot.GetValue<float>("TrackLength", 0f);
                float playerSpeed = snapshot.GetValue<float>("Speed", 0f);

                if (trackLength > 0 && playerSpeed > 1.0f)
                {
                    float deltaPct = row.LapDistPct - playerRow.LapDistPct;

                    // Handle Wrap-Around (Ring Logic)
                    if (deltaPct > 0.5f) deltaPct -= 1.0f;
                    else if (deltaPct < -0.5f) deltaPct += 1.0f;

                    float distanceMeters = Math.Abs(deltaPct * trackLength);
                    isAhead = deltaPct > 0;

                    // Simple distance/speed approximation
                    nativeTimeGap = distanceMeters / Math.Max(playerSpeed, 1.0f);
                }
                else
                {
                    // No valid data - skip this car
                    row.GapText = string.Empty;
                    return;
                }
            }

            // Reset smoothing if car was recently absent from telemetry (between 1-2 seconds)
            int framesSinceValid = _positionCalculator.GetFramesSinceValidData(row.CarIdx);
            if (framesSinceValid > 60 && framesSinceValid < 120)
            {
                row.ResetSmoothing();
            }

            // Update smoothed gap in ViewModel (for display stability)
            row.UpdateSmoothedGap(nativeTimeGap);
            float displayGap = row.SmoothedGap;

            // --- SET GAP TEXT FOR DISPLAY ---
            if (displayGap > 0 && displayGap < 100f) // Only show reasonable gaps
            {
                string sign = isAhead ? "+" : "-";
                row.GapText = $"{sign}{displayGap:F1}";
            }
            else
            {
                row.GapText = string.Empty;
            }

            // --- DEBUG LOGGING ---
            // Log once per second for cars within awareness window
            if (_debugFrameCounter % DEBUG_LOG_INTERVAL == 0 && displayGap <= TIME_SEG2_AWARE)
            {
                string relation = isAhead ? "AHEAD" : "BEHIND";
                Log.Debug($"[Native] #{row.CarNum} ({relation}): Gap={displayGap:F2}s (EstTime)");
            }

            // --- LIGHT UP SEGMENTS ---
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