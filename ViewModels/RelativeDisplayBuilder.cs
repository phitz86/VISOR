using System;
using System.Collections.Generic;
using System.Linq;
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

        // Row colours, segment thresholds and the gap readout's limits are in RelativeRowStyler.

        // Ahead/behind hysteresis: a car must separate from the player by this much before its row
        // slot (and gap sign) flips. Expressed in metres (converted via track length) so it stays a
        // fixed physical distance on every track. ~2 m clears the real side-by-side longitudinal
        // surge (~1-2 m) without lagging reality. Falls back to a pct when track length is unknown.
        private const float AHEAD_BEHIND_HYSTERESIS_METERS = 2.0f;
        private const float AHEAD_BEHIND_HYSTERESIS_DEFAULT_PCT = 0.0004f; // ~2 m on a ~5 km track

        private const int DEBUG_LOG_INTERVAL = 60; // ~1s at 60Hz
        #endregion

        #region Private Fields
        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;
        private readonly PositionCalculator _positionCalculator;
        private readonly PositionHistoryManager _historyManager;

        private int _debugFrameCounter = 0;

        // Practice/qualifying positions for this frame, by car. Rebuilt once per frame rather
        // than fetched (a copied list) and searched again for every row.
        private readonly Dictionary<int, (float FastestTime, int ClassPosition, int OverallPosition)> _fastestLapByCar = new();
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
        }
        #endregion

        #region Public Methods
        public List<RelativeRowViewModel> Calculate(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            _debugFrameCounter++;

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
            if (useFastestLap)
            {
                _fastestLapByCar.Clear();
                foreach (var entry in dataProvider.GetFastestLapPositioning())
                    _fastestLapByCar.TryAdd(entry.carIdx, (entry.fastestTime, entry.classPosition, entry.overallPosition));
            }
            ApplyDisplayLogic(finalRows, useFastestLap, carClassColors, carClassIDs, snapshot);

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
                    bool isPaceCar = carClassIDs[i] == IRacingIds.PaceCarClassId;
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
            float hysteresisBandPct = GetHysteresisBandPct();

            var otherCars = allCars.Where(c => !c.IsPlayer).Select(car =>
            {
                float directDistance = Math.Abs(car.LapDistPct - playerTrackPercent);
                float proximity = Math.Min(directDistance, 1.0f - directDistance);
                bool isAhead = ResolveAheadHysteretic(car, playerTrackPercent, hysteresisBandPct);
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

        /// <summary>Hysteresis dead-band as a LapDistPct fraction, converted from metres via track length.</summary>
        private float GetHysteresisBandPct()
        {
            float trackLengthMeters = _historyManager.TrackLengthMeters;
            return (trackLengthMeters > 0f)
                ? AHEAD_BEHIND_HYSTERESIS_METERS / trackLengthMeters
                : AHEAD_BEHIND_HYSTERESIS_DEFAULT_PCT;
        }

        /// <summary>
        /// Resolve whether a car is ahead of the player, with a small dead-band so a car running
        /// dead-even doesn't flip slot/sign every frame. Flip to ahead only once it leads by +band,
        /// to behind only once it trails by -band; hold the latched side in between. Seeds from raw
        /// geometry the first time (or after a telemetry gap clears the latch).
        /// </summary>
        private bool ResolveAheadHysteretic(RelativeRowViewModel car, float playerTrackPercent, float bandPct)
        {
            // Shortest signed arc to the player: + = ahead, - = behind.
            float signedDelta = car.LapDistPct - playerTrackPercent;
            if (signedDelta > 0.5f) signedDelta -= 1.0f;
            else if (signedDelta < -0.5f) signedDelta += 1.0f;

            if (signedDelta > bandPct) car._proximitySide = 1;
            else if (signedDelta < -bandPct) car._proximitySide = -1;
            else if (car._proximitySide == 0) car._proximitySide = (sbyte)(signedDelta >= 0f ? 1 : -1);
            // else: inside the dead-band — hold the previously latched side.

            return car._proximitySide == 1;
        }
        #endregion

        #region Private Methods - Display Styling
        private void ApplyDisplayLogic(
            List<RelativeRowViewModel> displayRows,
            bool isFastestLapMode,
            int[] carClassColors,
            int[] carClassIDs,
            SVappsLABSnapshot snapshot)
        {
            var playerRow = displayRows.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            bool useOverall = SettingsManager.Instance.Settings.PositionDisplayMode == PositionDisplayMode.Overall;

            foreach (var row in displayRows)
            {
                AssignClassPositionDisplay(row, isFastestLapMode, useOverall);
                RelativeRowStyler.AssignNameColor(row, playerRow);
                AssignClassBackgroundColor(row, carClassColors, carClassIDs);
                RelativeRowStyler.AssignFontStyle(row);
                AssignProximitySegments(row, playerRow, snapshot);
            }
        }

        private void AssignClassPositionDisplay(RelativeRowViewModel row, bool isFastestLapMode, bool useOverall)
        {
            if (isFastestLapMode)
            {
                if (_fastestLapByCar.TryGetValue(row.CarIdx, out var carData) && carData.FastestTime > 0)
                    row.ClassPos = $"{(useOverall ? carData.OverallPosition : carData.ClassPosition)}";
                else
                    row.ClassPos = "--";
            }
            else
            {
                int position = useOverall
                    ? _positionCalculator.GetOverallPosition(row.CarIdx)
                    : _positionCalculator.GetClassPosition(row.CarIdx, row.ClassID);
                row.ClassPos = (position > 0) ? $"{position}" : "--";
            }
        }

        private void AssignClassBackgroundColor(
            RelativeRowViewModel row,
            int[] carClassColors,
            int[] carClassIDs)
        {
            // No ClassID == 0 guard: that is the real class in single-class sessions (online Test,
            // offline custom races), and skipping it left the swatch at its Transparent default.
            // ClassColorManager resolves an unknown or uncoloured class to a legible light grey.
            row.ClassBackground = _classColorManager.GetClassColor(row.ClassID, carClassColors, carClassIDs);
        }

        private void AssignProximitySegments(RelativeRowViewModel row, RelativeRowViewModel playerRow, SVappsLABSnapshot snapshot)
        {
            if (row.IsPlayer)
            {
                RelativeRowStyler.ShowNoGap(row);
                return;
            }

            // Wrap to the shortest arc around the lap (-0.5 to +0.5). Used for the stationary feet
            // readout; direction (ahead/behind) comes from the hysteretic side latched in
            // BuildProximityBasedRows so the slot and the gap sign agree and don't twitch dead-even.
            float distDelta = row.LapDistPct - playerRow.LapDistPct;
            if (distDelta > 0.5f) distDelta -= 1.0f;
            else if (distDelta < -0.5f) distDelta += 1.0f;

            bool isAhead = row._proximitySide >= 0;

            if (row.IsOnPitRoad)
            {
                RelativeRowStyler.ShowInPits(row);
                return;
            }

            // --- 3. STATIONARY CHECK ---
            var oppBuffer = _historyManager.GetBuffer(row.CarIdx);
            if (oppBuffer != null && oppBuffer.IsStationary())
            {
                float trackLengthMeters = _historyManager.TrackLengthMeters;
                RelativeRowStyler.ShowStationary(row,
                    trackLengthMeters > 0 ? Math.Abs(distDelta) * trackLengthMeters : null);
                return;
            }

            // Gap via the history buffers, measured BOTH ways, keeping the believable (smaller) one:
            //   - "ahead" measure: how long ago the opponent passed the player's current spot.
            //   - "behind" measure: how long ago the player passed the opponent's current spot.
            // The current-lap crossing is always the small number; a lap-stale crossing (the
            // side-by-side singularity, when a car sits right on the player's position) is always
            // ~a full lap larger, so Min() discards it without having to guess direction. This is
            // continuous through dead-even, killing the 0.0 <-> full-lap flicker.
            double sessionTime = snapshot.SessionTime;
            var playerBuffer = _historyManager.GetBuffer(playerRow.CarIdx);

            float aheadGap = CrossingGap(oppBuffer, playerRow.LapDistPct, sessionTime);
            float behindGap = CrossingGap(playerBuffer, row.LapDistPct, sessionTime);

            float nativeTimeGap;
            if (aheadGap >= 0f && behindGap >= 0f)
                nativeTimeGap = Math.Min(aheadGap, behindGap);
            else if (aheadGap >= 0f)
                nativeTimeGap = aheadGap;
            else if (behindGap >= 0f)
                nativeTimeGap = behindGap;
            else
            {
                // Both buffers missed — hold previous display state instead of blanking the row.
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

            if (_debugFrameCounter % DEBUG_LOG_INTERVAL == 0 && displayGap <= RelativeRowStyler.TIME_SEG2_AWARE)
            {
                string relation = isAhead ? "AHEAD" : "BEHIND";
                Log.Debug($"[Buffer] #{row.CarNum} ({relation}): Gap={displayGap:F2}s");
            }

            RelativeRowStyler.ShowGap(row, displayGap, isAhead);
        }

        /// <summary>
        /// Time since a car was last at <paramref name="targetPct"/>, per its history buffer.
        /// Returns -1 when the buffer is missing or no usable crossing exists (caller treats as miss).
        /// </summary>
        private static float CrossingGap(PositionHistoryBuffer? buffer, float targetPct, double sessionTime)
        {
            if (buffer == null)
                return -1f;
            double? crossingTime = buffer.FindCrossingTime(targetPct);
            if (crossingTime.HasValue && sessionTime > crossingTime.Value)
                return (float)(sessionTime - crossingTime.Value);
            return -1f;
        }
        #endregion
    }
}
