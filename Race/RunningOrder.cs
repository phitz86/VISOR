using System.Collections.Generic;
using System.Linq;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.Race
{
    /// <summary>
    /// The live running order: class and overall positions sorted by lap + LapDistPct, on grid
    /// order until each car takes the green, with live cars skipping the slots of finished ones.
    /// Reads car positions from <see cref="CarTrackingCache"/> and the frozen slots from
    /// <see cref="FinishingPositions"/>. Part of <see cref="PositionCalculator"/>.
    /// </summary>
    internal sealed class RunningOrder
    {
        private readonly CarTrackingCache _tracking;
        private readonly FinishingPositions _finishing;

        #region Private Fields
        private readonly Dictionary<(int carIdx, int classId), int> _cachedPositions = new();
        private readonly Dictionary<int, int> _cachedOverallPositions = new();
        private int _lastLoggedLeaderIdx = -1;
        #endregion

        #region Private Fields - Green Flag Tracking
        // Per-car latch: a car enters here once it has taken the green flag. Until then its live
        // LapDistPct is ambiguous on the starting grid (cars staged ahead of S/F read ~0.99 while
        // cars behind it read ~0.01, all on lap 0), so the running-order sort uses grid order for
        // any car not yet in this set. Latched for the session; cleared on reset/session change.
        private readonly HashSet<int> _carsHavingTakenGreen = new();
        #endregion

        public RunningOrder(CarTrackingCache tracking, FinishingPositions finishing)
        {
            _tracking = tracking;
            _finishing = finishing;
        }

        #region Lookups
        /// <summary>
        /// The car's class position from the last sort, or -1. Frozen finishing positions aren't
        /// included; <see cref="PositionCalculator.GetClassPosition"/> reports those first.
        /// </summary>
        public int GetClassPosition(int carIdx, int classId)
        {
            if (_cachedPositions.TryGetValue((carIdx, classId), out int position))
            {
                return position;
            }
            return -1;
        }

        /// <summary>
        /// The car's overall position from the last sort, or -1. The pace car never has one.
        /// </summary>
        public int GetOverallPosition(int carIdx)
        {
            if (_cachedOverallPositions.TryGetValue(carIdx, out int position))
            {
                return position;
            }
            return -1;
        }

        /// <summary>
        /// The overall P1 of the last sort. There is none once the leader has finished, since
        /// finished cars aren't sorted.
        /// </summary>
        public bool TryGetLeader(out int carIdx)
        {
            foreach (var entry in _cachedOverallPositions)
            {
                if (entry.Value == 1)
                {
                    carIdx = entry.Key;
                    return true;
                }
            }
            carIdx = -1;
            return false;
        }

        /// <summary>
        /// CarIdxLap, which is what the running-order sort adds to LapDistPct. It is the one input
        /// to the sort that none of the other logging shows, and it is what a leader that is
        /// impossible by LapDistPct/LapCompleted would have to be disagreeing about.
        /// </summary>
        internal static string FormatLap(PositionFrame frame, int carIdx)
        {
            var lap = frame.CarIdxLap;
            return (carIdx >= 0 && carIdx < lap.Length) ? lap[carIdx].ToString() : "n/a";
        }
        #endregion

        #region Frame and Session
        /// <summary>
        /// Practice and qualifying: positions come from the session's fastest laps instead.
        /// </summary>
        public void Clear()
        {
            _cachedPositions.Clear();
            _cachedOverallPositions.Clear();
        }

        /// <summary>
        /// A new session: every car takes the green again.
        /// </summary>
        public void StartSession() => _carsHavingTakenGreen.Clear();

        public void Reset()
        {
            _cachedPositions.Clear();
            _cachedOverallPositions.Clear();
            _carsHavingTakenGreen.Clear();
            _lastLoggedLeaderIdx = -1;
        }
        #endregion

        #region Green Flag Tracking
        /// <summary>
        /// Latch each car the first time it takes the green flag. Two conditions must both hold:
        /// the session itself must be green (SessionState >= Racing), AND the car must have crossed
        /// S/F (CarIdxLapCompleted >= 0 with a valid track position).
        ///
        /// The SessionState gate matters because CarIdxLapCompleted ticks to 0 during the *parade*
        /// lap too - without the gate, cars latch one-by-one as they cross S/F under formation and
        /// flip from stable grid order to live (and on-grid-ambiguous) LapDistPct sorting, which
        /// makes the running order churn before the race has even started.
        ///
        /// The per-car half of the latch still earns its keep once green: a large multiclass field
        /// takes the green many seconds apart, so leaders latch and race while the tail stays on grid
        /// order until each car actually reaches S/F.
        /// </summary>
        public void UpdateGreenFlagStatus(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            // No car can take the green before the green flag is out. Until then everyone stays on
            // grid order (handled by GetPreGreenSortKey), which is steady through the parade lap.
            if (frame.SessionState < SessionStates.Racing)
                return;

            var lapCompleted = frame.CarIdxLapCompleted;
            var lapDistPct = frame.CarIdxLapDistPct;
            var carNumbers = sessionDataProvider.CarNumbers;

            if (lapCompleted == null || lapDistPct == null || carNumbers == null)
                return;

            for (int carIdx = 0; carIdx < lapCompleted.Length; carIdx++)
            {
                if (_carsHavingTakenGreen.Contains(carIdx))
                    continue;

                if (carIdx >= carNumbers.Length || string.IsNullOrEmpty(carNumbers[carIdx]))
                    continue;

                bool validDist = carIdx < lapDistPct.Length &&
                                 lapDistPct[carIdx] >= 0f &&
                                 lapDistPct[carIdx] <= 1f;

                if (lapCompleted[carIdx] >= 0 && validDist)
                {
                    _carsHavingTakenGreen.Add(carIdx);
                    Log.Debug($"Car #{carNumbers[carIdx]} (idx {carIdx}) took the green flag - switching to live position calc " +
                              $"(CarIdxLap {FormatLap(frame, carIdx)}, LapCompleted {lapCompleted[carIdx]}, LapDistPct {lapDistPct[carIdx]:F4})");
                }
            }
        }

        /// <summary>
        /// Sort key for a car that has not yet taken the green flag. Returns a negative value derived
        /// from grid order so the whole pre-green block sorts behind any car already racing (whose key
        /// is currentLap + LapDistPct, i.e. >= ~1), while preserving grid order within the block.
        /// </summary>
        private static float GetPreGreenSortKey(int carIdx, int[]? gridPositions)
        {
            int grid = (gridPositions != null && carIdx < gridPositions.Length) ? gridPositions[carIdx] : 0;

            if (grid <= 0)
            {
                // No grid info at all - keep deterministic and dump to the back of the pre-green block.
                return -1000f - carIdx;
            }

            // Lower grid position should sort ahead; negate so P1 (-1) outranks P2 (-2) under
            // OrderByDescending, and the whole block stays below any green car's positive key.
            return -grid;
        }

        /// <summary>
        /// Pick ONE grid-order source for the whole pre-green block. The live arrays are per-class
        /// (CarIdxClassPosition) or field-wide (CarIdxPosition) and 1-based with 0 meaning "unknown";
        /// qualifying order is field-wide and 1-based on the same terms. Both are valid orderings on
        /// their own, but a per-car fallback would compare a 1..n class number against a 1..N field
        /// number and scramble the block, so the source covering the most pre-green cars is used for
        /// all of them and the rest fall to the back of the block.
        /// </summary>
        private static int[]? SelectGridSource(int[]? livePositions, int[]? qualPositions, List<int> preGreenCars)
        {
            int liveCoverage = CountCoveredCars(livePositions, preGreenCars);
            if (liveCoverage == preGreenCars.Count)
                return livePositions;

            return (CountCoveredCars(qualPositions, preGreenCars) > liveCoverage) ? qualPositions : livePositions;
        }

        private static int CountCoveredCars(int[]? positions, List<int> carIndices)
        {
            if (positions == null)
                return 0;

            int covered = 0;
            foreach (int carIdx in carIndices)
            {
                if (carIdx < positions.Length && positions[carIdx] > 0)
                    covered++;
            }
            return covered;
        }
        #endregion

        #region Race Position Calculation
        public void CalculateRacePositions(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var currentLap = frame.CarIdxLap;
            var lapDistPct = frame.CarIdxLapDistPct;

            if (carClassIDs == null || currentLap == null || lapDistPct == null)
                return;

            var carsWithPositions = new List<CarPositionData>();
            var preGreenCars = new List<int>();

            foreach (int carIdx in _tracking.ValidCarIndices)
            {
                if (carIdx < 0 || carIdx >= carClassIDs.Length)
                    continue;

                // Already frozen — don't include in the live sort.
                if (_finishing.IsFinished(carIdx))
                    continue;

                float effectiveLapDistPct = _tracking.GetEffectiveLapDistPct(carIdx);
                int effectiveCurrentLap;

                if (lapDistPct[carIdx] >= 0f && lapDistPct[carIdx] <= 1f)
                {
                    // CarIdxLap and CarIdxLapDistPct don't always tick over in the same frame at
                    // S/F; realign them so a car crossing the line doesn't read a full lap out.
                    effectiveCurrentLap = currentLap[carIdx] + _tracking.GetLapNumberCorrection(carIdx);
                }
                else if (_tracking.TryGetLastValidLap(carIdx, out int cachedLap))
                {
                    // Predicting: the cached lap number froze with the telemetry, so add the laps
                    // the prediction has since wrapped through. The correction still applies —
                    // it fixes the cached baseline, the offset covers motion since.
                    effectiveCurrentLap = cachedLap + _tracking.GetLapNumberCorrection(carIdx) + _tracking.GetPredictedLapOffset(carIdx);
                }
                else
                {
                    continue;
                }

                // The prediction has run out: the car leaves the roster next frame. Under the
                // checkered, FinishTracker.HoldCarsWhoseTelemetryStopped has already held its place.
                if (effectiveLapDistPct < 0f)
                    continue;

                float trackPosition = effectiveCurrentLap + effectiveLapDistPct;

                bool hasTakenGreen = _carsHavingTakenGreen.Contains(carIdx);
                if (!hasTakenGreen)
                    preGreenCars.Add(carIdx);

                carsWithPositions.Add(new CarPositionData
                {
                    CarIdx = carIdx,
                    ClassId = carClassIDs[carIdx],
                    CurrentLap = effectiveCurrentLap,
                    LapDistPct = effectiveLapDistPct,
                    HasTakenGreen = hasTakenGreen,
                    SortKey = trackPosition,
                    OverallSortKey = trackPosition
                });
            }

            // Until a car takes the green flag, its on-grid LapDistPct is ambiguous across the S/F
            // line, so order it by grid position instead of live track position. Class and overall
            // use the same track position once green, but fall back to their respective grid orders
            // (per-class vs field-wide) while pre-green.
            if (preGreenCars.Count > 0)
            {
                var qualPositions = sessionDataProvider.GetQualifyResultsPositions();
                var classGrid = SelectGridSource(frame.CarIdxClassPosition, qualPositions, preGreenCars);
                var overallGrid = SelectGridSource(frame.CarIdxPosition, qualPositions, preGreenCars);

                foreach (var car in carsWithPositions)
                {
                    if (car.HasTakenGreen)
                        continue;

                    car.SortKey = GetPreGreenSortKey(car.CarIdx, classGrid);
                    car.OverallSortKey = GetPreGreenSortKey(car.CarIdx, overallGrid);
                }
            }

            AssignOverallPositions(carsWithPositions);
            LogLeaderChange(carsWithPositions, frame, sessionDataProvider);

            var classGroups = carsWithPositions.GroupBy(c => c.ClassId);

            foreach (var classGroup in classGroups)
            {
                // Collect position numbers already taken by frozen (finished) cars in this class.
                var frozenPositions = new HashSet<int>();
                foreach (var kv in _finishing.ClassPositions)
                {
                    if (kv.Key < carClassIDs.Length && carClassIDs[kv.Key] == classGroup.Key)
                        frozenPositions.Add(kv.Value);
                }

                // Assign live cars to the next available slot, skipping frozen positions.
                // This prevents a lapped car frozen at P5 from pushing P3/P4 down.
                var sortedCars = classGroup.OrderByDescending(c => c.SortKey).ToList();
                int nextPosition = 1;

                for (int i = 0; i < sortedCars.Count; i++)
                {
                    while (frozenPositions.Contains(nextPosition))
                        nextPosition++;

                    var car = sortedCars[i];
                    _cachedPositions[(car.CarIdx, car.ClassId)] = nextPosition;
                    nextPosition++;
                }
            }
        }

        /// <summary>
        /// Log each change of overall leader with the inputs the sort used for it. The overall P1
        /// is the one car whose sort key can be checked against the real race at a glance, so a
        /// leader that is physically impossible (a car just off the line leading the field) shows
        /// up here with the CarIdxLap/LapDistPct that produced it. Leader changes are rare, so
        /// this stays quiet.
        /// </summary>
        private void LogLeaderChange(List<CarPositionData> carsWithPositions, PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            if (!_cachedOverallPositions.Any(kv => kv.Value == 1))
                return;

            int leaderIdx = _cachedOverallPositions.First(kv => kv.Value == 1).Key;
            if (leaderIdx == _lastLoggedLeaderIdx)
                return;

            _lastLoggedLeaderIdx = leaderIdx;

            var leader = carsWithPositions.FirstOrDefault(c => c.CarIdx == leaderIdx);
            if (leader == null)
                return;

            var carNumbers = sessionDataProvider.CarNumbers;
            var lapCompleted = frame.CarIdxLapCompleted;
            string number = (carNumbers != null && leaderIdx < carNumbers.Length) ? carNumbers[leaderIdx] : "?";
            int completed = leaderIdx < lapCompleted.Length ? lapCompleted[leaderIdx] : -1;

            Log.Debug($"[Leader] Overall leader is now #{number} (idx {leaderIdx}) - effective lap {leader.CurrentLap} " +
                      $"(raw CarIdxLap {FormatLap(frame, leaderIdx)}), LapCompleted {completed}, LapDistPct {leader.LapDistPct:F4}, " +
                      $"sort key {leader.OverallSortKey:F4}, green latched {leader.HasTakenGreen}");
        }

        /// <summary>
        /// Assign field-wide overall positions across all classes in a single sort.
        /// Mirrors the per-class assignment: live cars fill the next available slot,
        /// skipping positions already frozen by finished cars so a lapped car frozen
        /// at P15 doesn't push the cars ahead of it down a spot.
        /// </summary>
        private void AssignOverallPositions(List<CarPositionData> carsWithPositions)
        {
            _cachedOverallPositions.Clear();

            var frozenPositions = new HashSet<int>(_finishing.OverallPositions.Values);

            // Exclude the pace car so it never consumes a field slot and shifts the real cars down.
            // (The per-class path isolates it in its own class group, which the global sort can't.)
            var sortedCars = carsWithPositions
                .Where(c => c.ClassId != IRacingIds.PaceCarClassId)
                .OrderByDescending(c => c.OverallSortKey)
                .ToList();
            int nextPosition = 1;

            for (int i = 0; i < sortedCars.Count; i++)
            {
                while (frozenPositions.Contains(nextPosition))
                    nextPosition++;

                _cachedOverallPositions[sortedCars[i].CarIdx] = nextPosition;
                nextPosition++;
            }
        }
        #endregion

        #region Helper Classes
        private class CarPositionData
        {
            public int CarIdx { get; set; }
            public int ClassId { get; set; }
            public int CurrentLap { get; set; }
            public float LapDistPct { get; set; }
            public bool HasTakenGreen { get; set; }
            public float SortKey { get; set; }
            public float OverallSortKey { get; set; }
        }
        #endregion
    }
}
