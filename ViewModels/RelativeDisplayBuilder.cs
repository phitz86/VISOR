using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VISOR.Diagnostics;
using VISOR.Settings;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Builds the 7-row proximity-based relative display with visual properties.
    /// Uses Historical Ring Buffer for gap calculation: measures the actual elapsed time
    /// between two cars occupying the same physical track position, like a real transponder loop.
    /// </summary>
    public class RelativeDisplayBuilder
    {
        #region Constants

        // Time gap thresholds (seconds) for proximity segment coloring.
        private const float TIME_SEG5_CRITICAL = 0.6f;
        private const float TIME_SEG4_DANGER = 1.5f;
        private const float TIME_SEG3_WARNING = 3.0f;
        private const float TIME_SEG2_AWARE = 5.0f;
        private const float TIME_SEG1_INFO = 8.0f;

        // Deactivation requires exceeding the threshold by this margin to prevent flicker.
        private const float SEGMENT_HYSTERESIS = 0.2f;

        private const float METERS_TO_FEET = 3.28084f;
        private const int MAX_DISTANCE_FEET = 999;

        // Upper sanity bound for the time-gap readout. The history buffer is the real limiter
        // (it can only return a gap as deep as the history it stores); beyond this is noise.
        private const float MAX_TIME_GAP_SECONDS = 600f;

        private const int DEBUG_LOG_INTERVAL = 60; // ~1s at 60Hz

        private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#80404040");
        private static readonly Color AheadAlertColor = (Color)ColorConverter.ConvertFromString("#FF00FFFF");
        private static readonly Color BehindAlertColor = (Color)ColorConverter.ConvertFromString("#FFFF9900");
        private static readonly Color PitGrayColor = (Color)ColorConverter.ConvertFromString("#60808080");
        private static readonly SolidColorBrush PitGrayBrush = new SolidColorBrush(PitGrayColor);
        private static readonly SolidColorBrush StationaryYellowBrush = new SolidColorBrush(Colors.Yellow);
        #endregion

        #region Private Fields
        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;
        private readonly PositionCalculator _positionCalculator;
        private readonly PositionHistoryManager _historyManager;

#if DEBUG
        private readonly RelativeGapLogger _gapLogger;
#endif

        private int _debugFrameCounter = 0;
        #endregion

        #region Constructor
        public RelativeDisplayBuilder(
            Dictionary<int, RelativeRowViewModel> carCache,
            ClassColorManager classColorManager,
            PositionCalculator positionCalculator,
            PositionHistoryManager historyManager)
        {
            _carCache = carCache;
            _classColorManager = classColorManager;
            _positionCalculator = positionCalculator;
            _historyManager = historyManager;
#if DEBUG
            _gapLogger = new RelativeGapLogger();
#endif
        }
        #endregion

        #region Public Methods
        public List<RelativeRowViewModel> Calculate(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            _debugFrameCounter++;
#if DEBUG
            _gapLogger?.BeginFrame();
#endif

            _historyManager.Update(snapshot, dataProvider);

            var playerCarIdx = snapshot.PlayerCarIdx;

            var carClassIDs = dataProvider.CarClassIDs;
            var carClassColors = dataProvider.CarClassColors;
            var userNames = dataProvider.UserNames;
            var carNumbers = dataProvider.CarNumbers;
            var carIsAI = dataProvider.CarIsAI;
            var incidentCounts = dataProvider.CurDriverIncidentCount;
            var currentLap = snapshot.CarIdxLap;
            var onPitRoad = snapshot.CarIdxOnPitRoad;

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
            _historyManager.Reset();
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

            // When the user is on pit road, show pit-road cars even if "hide cars in pits"
            // is enabled, so you can see who you're stopped near during your own stop.
            bool playerOnPitRoad = (onPitRoad != null && playerCarIdx >= 0
                && playerCarIdx < onPitRoad.Length) && onPitRoad[playerCarIdx];

            foreach (int i in validCarIndices)
            {
                if (i >= 0 && i < carNumbers.Length &&
                    !string.IsNullOrEmpty(carNumbers[i]) &&
                    !string.IsNullOrEmpty(userNames[i]))
                {
                    // Class ID 11 is the pace car. Show it only while it's actively pacing
                    // the field (on track and moving); hide it once it parks. Keying on motion
                    // rather than location handles parking anywhere — in pit lane, past the
                    // pit-exit line, on the apron — since CarIdxOnPitRoad goes false the moment
                    // it rolls beyond the pit surface.
                    bool isPaceCar = (carClassIDs[i] == 11);
                    bool isOnPitRoad = (onPitRoad != null && i < onPitRoad.Length) && onPitRoad[i];

                    // Optionally hide pit-road cars from the relative display only. Read live so the
                    // config toggle takes effect immediately. The player's own row is never hidden,
                    // so you still see yourself while serving your own stop. Pit-road cars also stay
                    // visible whenever the player is on pit road.
                    if (isOnPitRoad && i != playerCarIdx && !playerOnPitRoad
                        && Settings.UserSettings.Instance.HideCarsInPits)
                    {
                        continue;
                    }

                    if (isPaceCar)
                    {
                        var paceBuffer = _historyManager.GetBuffer(i);
                        bool isStationary = paceBuffer != null && paceBuffer.IsStationary();
                        if (isOnPitRoad || isStationary)
                        {
                            continue;
                        }
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

            var otherCars = allCars.Where(c => !c.IsPlayer).Select(car =>
            {
                float directDistance = Math.Abs(car.LapDistPct - playerTrackPercent);
                float proximity = Math.Min(directDistance, 1.0f - directDistance);
                bool isAhead = (car.LapDistPct - playerTrackPercent + 1.5f) % 1.0f > 0.5f;
                return new { Car = car, Proximity = proximity, IsAhead = isAhead };
            }).ToList();

            // Tie-break on CarIdx so sort order is deterministic when gaps are nearly equal.
            var carsAhead = otherCars.Where(x => x.IsAhead).OrderBy(x => x.Proximity).ThenBy(x => x.Car.CarIdx).Select(x => x.Car).ToList();
            var carsBehind = otherCars.Where(x => !x.IsAhead).OrderBy(x => x.Proximity).ThenBy(x => x.Car.CarIdx).Select(x => x.Car).ToList();

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

            for (int i = 0; i < displayRows.Count; i++)
            {
                var row = displayRows[i];
                AssignClassPositionDisplay(row, isFastestLapMode, dataProvider);
                AssignNameColor(row, playerRow);
                AssignClassBackgroundColor(row, carClassColors, carClassIDs);
                AssignFontStyle(row);
                AssignProximitySegments(row, playerRow, snapshot, i);
            }
        }

        private void AssignClassPositionDisplay(
            RelativeRowViewModel row,
            bool isFastestLapMode,
            ISessionDataProvider dataProvider)
        {
            bool useOverall = SettingsManager.Instance.Settings.PositionDisplayMode == PositionDisplayMode.Overall;

            if (isFastestLapMode)
            {
                var fastestLapData = dataProvider.GetFastestLapPositioning();
                var carData = fastestLapData.FirstOrDefault(d => d.carIdx == row.CarIdx);
                int position = useOverall ? carData.overallPosition : carData.classPosition;
                row.ClassPos = (carData.fastestTime > 0) ? $"{position}" : "--";
            }
            else
            {
                int position = useOverall
                    ? _positionCalculator.GetOverallPosition(row.CarIdx)
                    : _positionCalculator.GetClassPosition(row.CarIdx, row.ClassID);
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

        private void AssignProximitySegments(RelativeRowViewModel row, RelativeRowViewModel playerRow, SVappsLABSnapshot snapshot, int slotIndex)
        {
            if (row.IsPlayer)
            {
                row.GapText = string.Empty;
                ClearSegments(row);
                return;
            }

            // Wrap to the shortest arc around the lap (-0.5 to +0.5).
            float distDelta = row.LapDistPct - playerRow.LapDistPct;
            if (distDelta > 0.5f) distDelta -= 1.0f;
            else if (distDelta < -0.5f) distDelta += 1.0f;

            bool isGeometricallyAhead = distDelta > 0;

            if (row.IsOnPitRoad)
            {
                row.GapText = "PIT";
                row.GapColor = new SolidColorBrush(PitGrayColor);
                row.Segment1Color = PitGrayBrush;
                row.Segment2Color = PitGrayBrush;
                row.Segment3Color = PitGrayBrush;
                row.Segment4Color = PitGrayBrush;
                row.Segment5Color = PitGrayBrush;

#if DEBUG
                LogDiagnosticRow(snapshot, slotIndex, row, playerRow, distDelta, isGeometricallyAhead, "pit", 0f, row.GapText);
#endif
                return;
            }

            // --- 3. STATIONARY CHECK ---
            var oppBuffer = _historyManager.GetBuffer(row.CarIdx);
            if (oppBuffer != null && oppBuffer.IsStationary())
            {
                float trackLengthMeters = _historyManager.TrackLengthMeters;
                if (trackLengthMeters > 0)
                {
                    float distanceMeters = Math.Abs(distDelta) * trackLengthMeters;
                    int distanceFeet = Math.Min((int)(distanceMeters * METERS_TO_FEET), MAX_DISTANCE_FEET);

                    row.GapText = $"{distanceFeet}ft";
                    row.GapColor = StationaryYellowBrush;
                    row.GapFontWeight = FontWeights.Bold;
                }
                else
                {
                    row.GapText = string.Empty;
                }
                ClearSegments(row);

#if DEBUG
                LogDiagnosticRow(snapshot, slotIndex, row, playerRow, distDelta, isGeometricallyAhead, "stationary", 0f, row.GapText);
#endif
                return;
            }

            // Buffer crossing lookup: for a car ahead, search the opponent's buffer for the player's
            // current track position; for a car behind, search the player's buffer for the opponent's.
            // Fallback to the other buffer when the primary search misses — at close range the
            // ahead/behind flag can flip frame-to-frame, making the "wrong" buffer get tried first.
            double sessionTime = snapshot.SessionTime;
            float nativeTimeGap = 0f;
            bool isAhead = isGeometricallyAhead;
#if DEBUG
            string gapSource = "buffer";
#endif

            double? crossingTime = null;
            var playerBuffer = _historyManager.GetBuffer(playerRow.CarIdx);

            if (isGeometricallyAhead)
            {
                if (oppBuffer != null)
                    crossingTime = oppBuffer.FindCrossingTime(playerRow.LapDistPct);
                if (!crossingTime.HasValue && playerBuffer != null)
                    crossingTime = playerBuffer.FindCrossingTime(row.LapDistPct);
            }
            else
            {
                if (playerBuffer != null)
                    crossingTime = playerBuffer.FindCrossingTime(row.LapDistPct);
                if (!crossingTime.HasValue && oppBuffer != null)
                    crossingTime = oppBuffer.FindCrossingTime(playerRow.LapDistPct);
            }

            if (crossingTime.HasValue && sessionTime > crossingTime.Value)
            {
                nativeTimeGap = (float)(sessionTime - crossingTime.Value);
            }
            else
            {
                // Buffer miss — hold previous display state instead of blanking the row.
#if DEBUG
                LogDiagnosticRow(snapshot, slotIndex, row, playerRow, distDelta, isGeometricallyAhead, "none", 0f, row.GapText);
#endif
                return;
            }

            // Drop smoothing if the car disappeared from telemetry for 1-2 seconds.
            int framesSinceValid = _positionCalculator.GetFramesSinceValidData(row.CarIdx);
            if (framesSinceValid > 60 && framesSinceValid < 120)
            {
                row.ResetSmoothing();
            }

            row.UpdateSmoothedGap(nativeTimeGap);
            float displayGap = row.SmoothedGap;

            // Show the gap whenever it's in a sane range. The history buffer naturally limits how
            // far back a crossing can be found, so this just guards against garbage values.
            if (displayGap > 0 && displayGap < MAX_TIME_GAP_SECONDS)
            {
                string sign = isAhead ? "+" : "-";
                row.GapText = $"{sign}{displayGap:F1}";
            }
            else
            {
                row.GapText = string.Empty;
            }

            row.GapColor = Brushes.White;
            row.GapFontWeight = FontWeights.SemiBold;

#if DEBUG
            LogDiagnosticRow(snapshot, slotIndex, row, playerRow, distDelta, isGeometricallyAhead, gapSource, displayGap, row.GapText);
#endif

            if (_debugFrameCounter % DEBUG_LOG_INTERVAL == 0 && displayGap <= TIME_SEG2_AWARE)
            {
                string relation = isAhead ? "AHEAD" : "BEHIND";
                Log.Debug($"[Buffer] #{row.CarNum} ({relation}): Gap={displayGap:F2}s");
            }

            Color alertColor = isAhead ? AheadAlertColor : BehindAlertColor;

            // Count active segments. Hysteresis: once lit, a segment stays on until
            // the gap exceeds its threshold + margin, preventing flicker at boundaries.
            int prevSegments = row._lastActiveSegmentCount;
            int activeSegments = 0;

            float[] thresholds = { TIME_SEG1_INFO, TIME_SEG2_AWARE, TIME_SEG3_WARNING, TIME_SEG4_DANGER, TIME_SEG5_CRITICAL };
            for (int s = 0; s < thresholds.Length; s++)
            {
                float deactivateAt = (s < prevSegments) ? thresholds[s] + SEGMENT_HYSTERESIS : thresholds[s];
                if (displayGap <= deactivateAt)
                    activeSegments = s + 1;
            }

            // Explicitly set every segment — either colored or Transparent — so segments
            // that are no longer active get cleared without needing a blanket reset at the top.
            row.Segment1Color = (activeSegments >= 1) ? new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.0)) : Brushes.Transparent;
            row.Segment2Color = (activeSegments >= 2) ? new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.25)) : Brushes.Transparent;
            row.Segment3Color = (activeSegments >= 3) ? new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.50)) : Brushes.Transparent;
            row.Segment4Color = (activeSegments >= 4) ? new SolidColorBrush(BlendColors(NeutralColor, alertColor, 0.75)) : Brushes.Transparent;
            row.Segment5Color = (activeSegments >= 5) ? new SolidColorBrush(alertColor) : Brushes.Transparent;

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

#if DEBUG
        private void LogDiagnosticRow(SVappsLABSnapshot snapshot, int slotIndex, RelativeRowViewModel row,
            RelativeRowViewModel playerRow, float distDelta, bool isGeometricallyAhead, string gapSource,
            float displayGap, string gapText)
        {
            float sessionTime = (float)snapshot.SessionTime;
            _gapLogger?.LogRow(
                sessionTime, slotIndex, row.CarNum,
                playerRow.LapDistPct, row.LapDistPct,
                distDelta, isGeometricallyAhead,
                gapSource, displayGap, gapText);
        }
#endif

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
