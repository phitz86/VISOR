using System;
using System.Collections.Generic;
using System.Linq;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Calculates race positions with predictive position tracking for data resilience.
    /// Three-tier approach:
    /// 1. Valid-data history - gates entry to display
    /// 2. Predictive LapDistPct - smooth extrapolation during brief gaps
    /// 3. Cache expiration - removes truly disconnected cars after timeout
    /// 
    /// Exports clean, validated data to consumers via ValidCarIndices and GetEffectiveLapDistPct.
    /// 
    /// NEW: Finishing Position Freezing
    /// - Detects checkered flag (SessionState = 5 or 6)
    /// - Freezes class positions when cars cross S/F during checkered
    /// - Maintains finishing order even as cars exit session
    /// </summary>
    public class PositionCalculator
    {
        #region Constants
        private const int MAX_CACHE_AGE_FRAMES = 180; // 3 seconds at 60Hz
        private const int LOG_PREDICTION_THRESHOLD = 30; // Log predictions lasting >30 frames
        private const float MIN_VELOCITY_THRESHOLD = 0.00001f; // Minimum velocity to use prediction

        // S/F crossing detection for the lap-number desync correction below.
        private const float SF_WRAP_HIGH = 0.9f;
        private const float SF_WRAP_LOW = 0.1f;
        private const int LAP_DESYNC_MAX_FRAMES = 30; // ~0.5s at 60Hz - the correction is a bridge, not a state
        #endregion

        #region Private Fields - Core State
        private int _globalFrameCounter = 0;
        private readonly HashSet<int> _validCarIndices = new();
        private readonly Dictionary<(int carIdx, int classId), int> _cachedPositions = new();
        private readonly Dictionary<int, int> _cachedOverallPositions = new();
        #endregion

        #region Private Fields - Finishing Position Tracking
        private readonly Dictionary<int, int> _finishingClassPositions = new();
        private readonly Dictionary<int, int> _finishingOverallPositions = new();
        private readonly HashSet<int> _carsFinished = new();
        private bool _isCheckeredFlag = false;
        private bool _leaderHasFinished = false;
        private int _lastSessionNum = -1;
        private readonly Dictionary<int, int> _lastLapCompleted = new();
        #endregion

        #region Private Fields - Green Flag Tracking
        // Per-car latch: a car enters here once it has taken the green flag. Until then its live
        // LapDistPct is ambiguous on the starting grid (cars staged ahead of S/F read ~0.99 while
        // cars behind it read ~0.01, all on lap 0), so the running-order sort uses grid order for
        // any car not yet in this set. Latched for the session; cleared on reset/session change.
        private readonly HashSet<int> _carsHavingTakenGreen = new();
        #endregion

        #region Private Fields - Prediction System
        // Tier 1: gate keeper — once a car has valid data it stays eligible for display.
        private readonly HashSet<int> _carsWithValidDataHistory = new();

        // Tier 2: velocity tracking drives short-gap extrapolation.
        private readonly Dictionary<int, float> _lastValidLapDistPct = new();
        private readonly Dictionary<int, float> _lapDistPctVelocity = new();
        private readonly Dictionary<int, int> _lastValidCurrentLap = new();

        // Tier 3: cache expiration removes cars after prolonged invalid data.
        private readonly Dictionary<int, int> _framesSinceValidData = new();
        private readonly Dictionary<int, int> _predictionStartFrame = new();
        #endregion

        #region Private Fields - Lap Number Desync
        // iRacing does not always tick CarIdxLap and CarIdxLapDistPct over in the same telemetry
        // frame at S/F. Whichever lags, the derived track position (lap + LapDistPct) is a full lap
        // out for a frame or two, which drops a leader to the tail of the running order and back.
        // This holds a per-car +1/-1 lap correction plus the frame it was raised, so a correction
        // that never resolves expires instead of persisting as a bogus lap.
        private readonly Dictionary<int, (int Offset, int Frame)> _lapNumberCorrection = new();
        #endregion

        #region Private Fields - Finish Diagnostics
        private int _lastLoggedFinishFlags = -1;
        private int _lastLoggedSessionState = -999;
        private int _lastLoggedLeaderIdx = -1;
        #endregion

        #region Private Fields - Logging State
        private readonly HashSet<int> _lastFrameValidCars = new();
        private readonly HashSet<int> _carsWithInvalidLapDistPctLogged = new();
        #endregion

        #region Public Properties
        /// <summary>
        /// Set of car indices that have valid YAML data, have ever had valid telemetry,
        /// AND have valid cache (not expired). This is the filtered roster for displays.
        /// </summary>
        public IReadOnlySet<int> ValidCarIndices => _validCarIndices;
        #endregion

        #region Public Methods
        /// <summary>
        /// Update the position calculator with the latest telemetry frame.
        /// Processes every frame (60Hz) with prediction for smooth display.
        /// </summary>
        public void Update(PositionFrame frame, ISessionDataProvider? sessionDataProvider)
        {
            if (frame == null || sessionDataProvider == null || !sessionDataProvider.IsDataReady)
                return;

            _globalFrameCounter++;
            ProcessUpdate(frame, sessionDataProvider);
        }

        /// <summary>
        /// Get the effective LapDistPct for a car (current valid data OR predicted value).
        /// Returns -1 if no valid data or cache expired.
        /// This is the ONLY method consumers should use to get car positions.
        /// </summary>
        public float GetEffectiveLapDistPct(int carIdx)
        {
            if (_framesSinceValidData.TryGetValue(carIdx, out int framesSinceValid) &&
                framesSinceValid == 0 &&
                _lastValidLapDistPct.TryGetValue(carIdx, out float currentValid))
            {
                return currentValid;
            }

            if (HasValidCache(carIdx))
            {
                return GetPredictedLapDistPct(carIdx);
            }

            return -1f;
        }

        /// <summary>
        /// Get the class position for a specific car.
        /// Returns frozen finishing position if car has finished, otherwise calculated position.
        /// Returns calculated position in race mode, YAML position in practice/qual.
        /// </summary>
        public int GetClassPosition(int carIdx, int classId)
        {
            if (_finishingClassPositions.TryGetValue(carIdx, out int finishingPosition))
            {
                return finishingPosition;
            }

            if (_cachedPositions.TryGetValue((carIdx, classId), out int position))
            {
                return position;
            }
            return -1;
        }

        /// <summary>
        /// Get the overall (field-wide) position for a specific car.
        /// Returns frozen finishing position if car has finished, otherwise calculated position.
        /// Mirrors <see cref="GetClassPosition"/> but ignores class boundaries.
        /// </summary>
        public int GetOverallPosition(int carIdx)
        {
            if (_finishingOverallPositions.TryGetValue(carIdx, out int finishingPosition))
            {
                return finishingPosition;
            }

            if (_cachedOverallPositions.TryGetValue(carIdx, out int position))
            {
                return position;
            }
            return -1;
        }

        /// <summary>
        /// Check if cached data is still valid for a car (within expiration window).
        /// </summary>
        public bool HasValidCache(int carIdx)
        {
            return _framesSinceValidData.TryGetValue(carIdx, out int frames) &&
                   frames <= MAX_CACHE_AGE_FRAMES;
        }

        /// <summary>
        /// Get the number of frames since valid data was received for a car.
        /// Returns int.MaxValue if car has never had valid data.
        /// Used to detect cars returning from telemetry gaps.
        /// </summary>
        public int GetFramesSinceValidData(int carIdx)
        {
            return _framesSinceValidData.GetValueOrDefault(carIdx, int.MaxValue);
        }

        /// <summary>
        /// Reset all tracking state when session changes or connection lost.
        /// </summary>
        public void Reset()
        {
            _globalFrameCounter = 0;
            _validCarIndices.Clear();
            _cachedPositions.Clear();
            _cachedOverallPositions.Clear();
            _carsWithValidDataHistory.Clear();
            _lastValidLapDistPct.Clear();
            _lapDistPctVelocity.Clear();
            _lastValidCurrentLap.Clear();
            _framesSinceValidData.Clear();
            _predictionStartFrame.Clear();
            _lapNumberCorrection.Clear();
            _lastFrameValidCars.Clear();
            _carsWithInvalidLapDistPctLogged.Clear();

            _finishingClassPositions.Clear();
            _finishingOverallPositions.Clear();
            _carsFinished.Clear();
            _isCheckeredFlag = false;
            _leaderHasFinished = false;
            _lastSessionNum = -1;
            _lastLapCompleted.Clear();
            _carsHavingTakenGreen.Clear();
            _lastLoggedFinishFlags = -1;
            _lastLoggedSessionState = -999;
            _lastLoggedLeaderIdx = -1;

            Log.Info("PositionCalculator reset - all state cleared");
        }
        #endregion

        #region Private Methods - Update Processing
        private void ProcessUpdate(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            DetectSessionTransition(frame);
            TrackCheckeredFlagState(frame);
            LogFinishPhaseTransitions(frame, sessionDataProvider);
            FreezeFinishingPositions(frame, sessionDataProvider);

            UpdateValidCarTracking(sessionDataProvider);
            FreezeDepartedCars(sessionDataProvider);
            UpdatePredictiveCache(frame, sessionDataProvider);

            if (!sessionDataProvider.ShouldUseFastestLapPositioning())
            {
                UpdateGreenFlagStatus(frame, sessionDataProvider);
                CalculateRacePositions(frame, sessionDataProvider);
            }
            else
            {
                _cachedPositions.Clear();
                _cachedOverallPositions.Clear();
            }
        }
        #endregion

        #region Private Methods - Finishing Position Tracking
        /// <summary>
        /// Detect session transitions and clear finishing positions when session changes.
        /// </summary>
        private void DetectSessionTransition(PositionFrame frame)
        {
            int currentSessionNum = frame.SessionNum;

            if (_lastSessionNum != -1 && currentSessionNum != _lastSessionNum)
            {
                Log.Info($"Session transition detected ({_lastSessionNum} -> {currentSessionNum}), clearing finishing positions");
                _finishingClassPositions.Clear();
                _finishingOverallPositions.Clear();
                _carsFinished.Clear();
                _isCheckeredFlag = false;
                _leaderHasFinished = false;
                _lastLapCompleted.Clear();
                _carsHavingTakenGreen.Clear();
                _lapNumberCorrection.Clear();
            }

            _lastSessionNum = currentSessionNum;
        }

        /// <summary>
        /// Track checkered flag state based on SessionState.
        /// SessionState: 5 = Checkered, 6 = CoolDown
        /// </summary>
        private void TrackCheckeredFlagState(PositionFrame frame)
        {
            int sessionState = frame.SessionState;
            bool wasCheckeredFlag = _isCheckeredFlag;

            _isCheckeredFlag = SessionStates.IsCheckered(sessionState);

            if (!wasCheckeredFlag && _isCheckeredFlag)
            {
                Log.Info($"Checkered flag detected (SessionState: {sessionState}), beginning finishing position tracking");
            }
        }

        /// <summary>
        /// Record the frame-accurate ordering between the session flags / SessionState changing and
        /// cars actually crossing S/F. Everything the finish logic does hangs off that ordering, and
        /// nothing else in the log can see it: SessionFlags is never logged, and a lap-completed
        /// increment only surfaces when a freeze succeeds — i.e. it is missing in exactly the case
        /// where the feature failed.
        ///
        /// The leader's LapDistPct is the measurement that matters. If the flags are raised as the
        /// leader exits the final corner, this logs a pct short of 1.0 (0.95-ish); if they are raised
        /// at the line, it logs ~0.999 or a value just past the wrap. Positions come from the
        /// previous frame's sort, which is the same data the freeze reads.
        /// </summary>
        private void LogFinishPhaseTransitions(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            // Masked so the finish diagnostics don't log on every caution or start-light change.
            int finishFlags = frame.SessionFlags & IRacingIds.FinishFlagsMask;
            int sessionState = frame.SessionState;

            if (finishFlags == _lastLoggedFinishFlags && sessionState == _lastLoggedSessionState)
            {
                return;
            }

            _lastLoggedFinishFlags = finishFlags;
            _lastLoggedSessionState = sessionState;

            Log.Info($"[Finish] SessionState {sessionState}, flags [{DescribeFinishFlags(finishFlags)}] - " +
                     $"{DescribeLeader(frame, sessionDataProvider)}; {DescribePlayer(frame)}");
        }

        /// <summary>
        /// The player's own track position. The white and checkered SessionFlags bits are raised per
        /// car, a fixed lead ahead of that car's own line, so the player's position at the moment a
        /// bit changes (not the leader's) is what shows whether that lead is a distance or a time.
        /// </summary>
        private static string DescribePlayer(PositionFrame frame)
        {
            int playerIdx = frame.PlayerCarIdx;
            var lapDistPct = frame.CarIdxLapDistPct;
            var lapCompleted = frame.CarIdxLapCompleted;

            if (playerIdx < 0 || playerIdx >= lapDistPct.Length || playerIdx >= lapCompleted.Length)
                return "player n/a";

            return $"player at LapDistPct {lapDistPct[playerIdx]:F4}, LapCompleted {lapCompleted[playerIdx]}, " +
                   $"CarIdxLap {FormatLap(frame, playerIdx)}";
        }

        /// <summary>
        /// CarIdxLap, which is what the running-order sort adds to LapDistPct. It is the one input
        /// to the sort that none of the other logging shows, and it is what a leader that is
        /// impossible by LapDistPct/LapCompleted would have to be disagreeing about.
        /// </summary>
        private static string FormatLap(PositionFrame frame, int carIdx)
        {
            var lap = frame.CarIdxLap;
            return (carIdx >= 0 && carIdx < lap.Length) ? lap[carIdx].ToString() : "n/a";
        }

        private static string DescribeFinishFlags(int finishFlags)
        {
            if (finishFlags == 0)
                return "none";

            var parts = new List<string>(3);
            if ((finishFlags & (int)SessionFlags.Green) != 0) parts.Add("Green");
            if ((finishFlags & (int)SessionFlags.White) != 0) parts.Add("White");
            if ((finishFlags & (int)SessionFlags.Checkered) != 0) parts.Add("Checkered");
            return string.Join("|", parts);
        }

        /// <summary>
        /// The current overall leader with the two variables the freeze depends on. Reads the
        /// position cache, so it reports whoever the running order currently has at P1 — which is
        /// itself worth seeing, since a wrong leader is one of the ways the freeze goes astray.
        /// </summary>
        private string DescribeLeader(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            foreach (var entry in _cachedOverallPositions)
            {
                if (entry.Value != 1)
                    continue;

                int carIdx = entry.Key;
                var carNumbers = sessionDataProvider.CarNumbers;
                var lapDistPct = frame.CarIdxLapDistPct;
                var lapCompleted = frame.CarIdxLapCompleted;

                string number = (carNumbers != null && carIdx < carNumbers.Length) ? carNumbers[carIdx] : "?";
                float pct = (lapDistPct != null && carIdx < lapDistPct.Length) ? lapDistPct[carIdx] : -1f;
                int laps = (lapCompleted != null && carIdx < lapCompleted.Length) ? lapCompleted[carIdx] : -1;

                return $"leader #{number} (idx {carIdx}) at LapDistPct {pct:F4}, LapCompleted {laps}, CarIdxLap {FormatLap(frame, carIdx)}";
            }

            // Finished cars are excluded from the live sort, so once the leader freezes there is no
            // live P1 even though the race has a leader. Report the frozen one rather than "none".
            foreach (var entry in _finishingOverallPositions)
            {
                if (entry.Value != 1)
                    continue;

                var carNumbers = sessionDataProvider.CarNumbers;
                string number = (carNumbers != null && entry.Key < carNumbers.Length) ? carNumbers[entry.Key] : "?";
                return $"leader #{number} (idx {entry.Key}) already frozen at P1";
            }

            return "no leader in the running order yet";
        }

        /// <summary>
        /// Freeze class positions for cars as they take the checkered flag.
        /// Only begins freezing after the P1 car (class leader) crosses S/F.
        /// Monitors CarIdxLapCompleted increments during checkered flag state.
        ///
        /// The CarIdxLapCompleted baseline is tracked from the moment the session starts, NOT from
        /// the first checkered frame. iRacing flips SessionState to Checkered *because* the leader
        /// crossed S/F, so the leader's lap-completed increment lands in the same telemetry sample
        /// as the state change. Seeding the baseline on that frame swallowed the increment, the
        /// leader never latched, and with the leader gate never opening nothing was ever frozen —
        /// so every car that then logged out handed a free position to everyone behind it.
        /// </summary>
        private void FreezeFinishingPositions(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carNumbers = sessionDataProvider.CarNumbers;
            var carLapCompleted = frame.CarIdxLapCompleted;

            if (carClassIDs == null || carNumbers == null || carLapCompleted == null)
            {
                return;
            }

            for (int carIdx = 0; carIdx < carLapCompleted.Length; carIdx++)
            {
                if (carIdx >= carClassIDs.Length || carIdx >= carNumbers.Length)
                {
                    continue;
                }

                int currentLapCompleted = carLapCompleted[carIdx];

                // A car with no telemetry reports -1. Keep the last real baseline rather than
                // storing the sentinel, or the car's return would read as a lap completion.
                if (currentLapCompleted < 0)
                {
                    continue;
                }

                bool isFirstObservation = !_lastLapCompleted.TryGetValue(carIdx, out int lastLapCompleted);
                _lastLapCompleted[carIdx] = currentLapCompleted;

                if (!_isCheckeredFlag || isFirstObservation || _carsFinished.Contains(carIdx))
                {
                    continue;
                }

                if (currentLapCompleted > lastLapCompleted)
                {
                    int classId = carClassIDs[carIdx];
                    int currentPosition = GetClassPosition(carIdx, classId);
                    int currentOverall = GetOverallPosition(carIdx);

                    // The gate is the OVERALL leader finishing, not any class leader: iRacing ends the
                    // race for everyone when the overall winner takes the flag, and a slower class's
                    // P1 crossing in the few seconds between the state flip and the winner's crossing
                    // would otherwise open the gate early and freeze cars that are still racing.
                    if (!_leaderHasFinished && currentOverall == 1)
                    {
                        _finishingClassPositions[carIdx] = currentPosition;
                        _finishingOverallPositions[carIdx] = currentOverall;
                        _carsFinished.Add(carIdx);
                        _leaderHasFinished = true;

                        Log.Info($"LEADER Car #{carNumbers[carIdx]} (idx {carIdx}) took checkered flag - frozen at P{currentPosition} (overall P{currentOverall}) (LapCompleted: {lastLapCompleted} -> {currentLapCompleted})");
                    }
                    else if (_leaderHasFinished && currentPosition > 0)
                    {
                        // Don't freeze lapped traffic ahead of the class leader until the leader has crossed.
                        _finishingClassPositions[carIdx] = currentPosition;
                        _finishingOverallPositions[carIdx] = currentOverall;
                        _carsFinished.Add(carIdx);

                        Log.Info($"Car #{carNumbers[carIdx]} (idx {carIdx}) took checkered flag - frozen at P{currentPosition} (overall P{currentOverall}) (LapCompleted: {lastLapCompleted} -> {currentLapCompleted})");
                    }
                    else if (!_leaderHasFinished)
                    {
                        // Expected: the car crossed after the state flip but before the overall leader
                        // did, so it has not been given the flag and races another lap. Routine in a
                        // multiclass field, so it stays out of the Info log.
                        Log.Debug($"Car #{carNumbers[carIdx]} (idx {carIdx}) crossed before the overall leader - not frozen, " +
                                  $"P{currentPosition} (overall P{currentOverall}) (LapCompleted: {lastLapCompleted} -> {currentLapCompleted})");
                    }
                    else
                    {
                        // Unexpected: the leader has finished but this car has no computed position, so
                        // no slot could be taken. Without this line a failed freeze is completely
                        // silent, which is what made the original leader-latch failure so hard to place.
                        Log.Info($"Car #{carNumbers[carIdx]} (idx {carIdx}) completed a lap under the checkered but was NOT frozen - " +
                                 $"P{currentPosition} (overall P{currentOverall}) (LapCompleted: {lastLapCompleted} -> {currentLapCompleted})");
                    }
                }
            }
        }

        /// <summary>
        /// Hold the finishing slot of a car that drops out of the session during the checkered
        /// instead of letting everyone behind it slide up a place. Cars in an offline or AI race
        /// leave the moment they finish, so the crossing-based freeze above can miss them. A car
        /// whose telemetry stops is held by <see cref="HoldCarsWhoseTelemetryStopped"/> instead,
        /// a frame before it would leave the roster.
        ///
        /// Only runs under the checkered, so a mid-race telemetry dropout still recovers normally.
        /// A car that leaves and rejoins during the checkered keeps the slot it left on, which is
        /// the right answer once the race is decided.
        /// </summary>
        private void FreezeDepartedCars(ISessionDataProvider sessionDataProvider)
        {
            if (!_isCheckeredFlag)
            {
                return;
            }

            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carNumbers = sessionDataProvider.CarNumbers;

            if (carClassIDs == null || carNumbers == null)
            {
                return;
            }

            // _lastFrameValidCars holds the previous frame's roster (UpdateValidCarTracking copies
            // it before rebuilding _validCarIndices), so the difference is this frame's departures.
            foreach (int carIdx in _lastFrameValidCars)
            {
                if (_validCarIndices.Contains(carIdx) || _carsFinished.Contains(carIdx))
                {
                    continue;
                }

                if (carIdx >= carClassIDs.Length || carIdx >= carNumbers.Length)
                {
                    continue;
                }

                if (TryHoldLastPlace(carIdx, carClassIDs[carIdx], out int classPosition, out int overallPosition))
                {
                    Log.Info($"Car #{carNumbers[carIdx]} (idx {carIdx}) left during the checkered - holding P{classPosition} (overall P{overallPosition})");
                }
            }
        }

        /// <summary>
        /// Hold the finishing slot of a car whose telemetry has stopped for longer than the
        /// prediction covers (<see cref="MAX_CACHE_AGE_FRAMES"/>), during the checkered. Called
        /// from the sort on the frame the car drops out of it, which is one frame before it leaves
        /// the roster: by then <see cref="FreezeDepartedCars"/> would find no position to hold,
        /// and the car behind would already have taken its place. The positions held are the
        /// previous frame's, since this frame's sort hasn't replaced them yet.
        /// </summary>
        private void HoldCarsWhoseTelemetryStopped(List<int> carIndices, int[] carClassIDs, string[] carNumbers)
        {
            foreach (int carIdx in carIndices)
            {
                if (TryHoldLastPlace(carIdx, carClassIDs[carIdx], out int classPosition, out int overallPosition))
                {
                    Log.Info($"Car #{carNumbers[carIdx]} (idx {carIdx}) telemetry stopped during the checkered - holding P{classPosition} (overall P{overallPosition})");
                }
            }
        }

        /// <summary>
        /// Freeze a car that has gone during the checkered at the place it had in the last sort.
        /// False when it had none (the pace car has no overall place, a car never placed has neither).
        /// </summary>
        private bool TryHoldLastPlace(int carIdx, int classId, out int classPosition, out int overallPosition)
        {
            classPosition = GetClassPosition(carIdx, classId);
            overallPosition = GetOverallPosition(carIdx);

            if (classPosition <= 0 || overallPosition <= 0)
            {
                return false;
            }

            _finishingClassPositions[carIdx] = classPosition;
            _finishingOverallPositions[carIdx] = overallPosition;
            _carsFinished.Add(carIdx);

            // Keep the leader gate coherent: if the car that left was holding overall P1, the
            // winner is home and the crossing-based freeze can start on everyone else. Overall,
            // not class: a slower class's leader leaving says nothing about the race winner.
            if (overallPosition == 1)
            {
                _leaderHasFinished = true;
            }

            return true;
        }
        #endregion

        #region Private Methods - Green Flag Tracking
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
        private void UpdateGreenFlagStatus(PositionFrame frame, ISessionDataProvider sessionDataProvider)
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

        #region Private Methods - Valid Car Tracking
        private void UpdateValidCarTracking(ISessionDataProvider sessionDataProvider)
        {
            var carNumbers = sessionDataProvider.CarNumbers;
            var userNames = sessionDataProvider.UserNames;

            if (carNumbers == null || userNames == null)
                return;

            _lastFrameValidCars.Clear();
            foreach (var carIdx in _validCarIndices)
            {
                _lastFrameValidCars.Add(carIdx);
            }

            _validCarIndices.Clear();
            for (int i = 0; i < 64; i++)
            {
                bool hasYamlData = !string.IsNullOrEmpty(carNumbers[i]) &&
                                   !string.IsNullOrEmpty(userNames[i]);

                if (hasYamlData &&
                    _carsWithValidDataHistory.Contains(i) &&
                    HasValidCache(i))
                {
                    _validCarIndices.Add(i);
                }
            }

            var newCars = _validCarIndices.Except(_lastFrameValidCars).ToList();
            var removedCars = _lastFrameValidCars.Except(_validCarIndices).ToList();

            foreach (var carIdx in newCars)
            {
                Log.Debug($"Car #{carNumbers[carIdx]} added to valid roster");
            }

            foreach (var carIdx in removedCars)
            {
                Log.Debug($"Car #{carNumbers[carIdx]} removed from valid roster");
            }
        }
        #endregion

        #region Private Methods - Predictive Cache
        private void UpdatePredictiveCache(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            var lapDistPct = frame.CarIdxLapDistPct;
            var currentLap = frame.CarIdxLap;
            var onPitRoad = frame.CarIdxOnPitRoad;
            var carNumbers = sessionDataProvider.CarNumbers;

            if (lapDistPct == null || currentLap == null || onPitRoad == null || carNumbers == null)
                return;

            for (int i = 0; i < 64; i++)
            {
                if (string.IsNullOrEmpty(carNumbers[i]))
                    continue;

                bool isOnPitRoad = (i < onPitRoad.Length) && onPitRoad[i];
                bool hasValidData = (i < lapDistPct.Length) &&
                                    lapDistPct[i] >= 0f &&
                                    lapDistPct[i] <= 1f;

                // Log once per invalid stretch so mid-race telemetry gaps are captured without spamming.
                if (!hasValidData && !_carsWithInvalidLapDistPctLogged.Contains(i))
                {
                    var trackSurface = frame.CarIdxTrackSurface;
                    var carLaps = frame.CarIdxLap;
                    var bestLaps = frame.CarIdxBestLapTime;
                    var estTime = frame.CarIdxEstTime;

                    int surface = (trackSurface != null && i < trackSurface.Length) ? trackSurface[i] : -999;
                    int lap = (carLaps != null && i < carLaps.Length) ? carLaps[i] : -999;
                    float bestLap = (bestLaps != null && i < bestLaps.Length) ? bestLaps[i] : -999f;

                    Log.Debug($"Car #{carNumbers[i]} (idx {i}) telemetry snapshot - LapDist:{lapDistPct[i]:F3}, EstTime:{estTime:F2}, Surface:{surface}, Lap:{lap}, BestLap:{bestLap:F2}, OnPit:{isOnPitRoad}");
                    _carsWithInvalidLapDistPctLogged.Add(i);
                }

                if (hasValidData)
                {
                    ProcessValidData(i, lapDistPct[i], currentLap[i], isOnPitRoad, carNumbers);
                }
                else
                {
                    ProcessInvalidData(i, carNumbers);
                }
            }
        }

        private void ProcessValidData(int carIdx, float lapDist, int lap, bool isOnPit, string[] carNumbers)
        {
            if (!_carsWithValidDataHistory.Contains(carIdx))
            {
                _carsWithValidDataHistory.Add(carIdx);
                Log.Debug($"Car #{carNumbers[carIdx]} first valid data - added to history");
            }

            _carsWithInvalidLapDistPctLogged.Remove(carIdx);

            // Only meaningful against the immediately preceding frame. Across a telemetry gap the
            // car may have crossed S/F unobserved, so the lap counter legitimately jumps without a
            // LapDistPct wrap to pair it with — reading that as a desync would subtract a real lap.
            bool followsValidFrame = _framesSinceValidData.GetValueOrDefault(carIdx, int.MaxValue) == 0;

            if (_lastValidLapDistPct.TryGetValue(carIdx, out float lastDist))
            {
                if (followsValidFrame)
                {
                    UpdateLapNumberCorrection(carIdx, lastDist, lapDist,
                        _lastValidCurrentLap.GetValueOrDefault(carIdx, lap), lap);
                }
                else
                {
                    _lapNumberCorrection.Remove(carIdx);
                }

                float delta = lapDist - lastDist;

                // Lap boundary wrap-around.
                if (delta < -0.5f) delta += 1.0f;
                if (delta > 0.5f) delta -= 1.0f;

                // Exponential smoothing (70% old, 30% new).
                float instantVelocity = delta;
                float smoothedVelocity = _lapDistPctVelocity.GetValueOrDefault(carIdx, 0f);
                smoothedVelocity = (instantVelocity * 0.3f) + (smoothedVelocity * 0.7f);

                // Don't predict through pit lane.
                if (isOnPit)
                {
                    smoothedVelocity = 0f;
                }

                _lapDistPctVelocity[carIdx] = smoothedVelocity;
            }

            _lastValidLapDistPct[carIdx] = lapDist;
            _lastValidCurrentLap[carIdx] = lap;
            _framesSinceValidData[carIdx] = 0;

            if (_predictionStartFrame.Remove(carIdx, out int startFrame))
            {
                int predictionDuration = _globalFrameCounter - startFrame;
                if (predictionDuration > LOG_PREDICTION_THRESHOLD)
                {
                    Log.Debug($"Car #{carNumbers[carIdx]} prediction ended after {predictionDuration} frames");
                }
            }
        }

        private void ProcessInvalidData(int carIdx, string[] carNumbers)
        {
            int framesSinceValid = _framesSinceValidData.GetValueOrDefault(carIdx, 0) + 1;
            _framesSinceValidData[carIdx] = framesSinceValid;

            if (!_predictionStartFrame.ContainsKey(carIdx) &&
                _lastValidLapDistPct.ContainsKey(carIdx))
            {
                _predictionStartFrame[carIdx] = _globalFrameCounter;
            }

            if (framesSinceValid == MAX_CACHE_AGE_FRAMES &&
                _carsWithValidDataHistory.Contains(carIdx))
            {
                Log.Debug($"Car #{carNumbers[carIdx]} cache expired after {MAX_CACHE_AGE_FRAMES} frames (3 seconds)");
            }
        }

        /// <summary>
        /// Track whether CarIdxLap and CarIdxLapDistPct currently disagree about which lap a car is
        /// on. They normally tick over together at S/F, but either can lead the other by a frame or
        /// two; while they disagree, lap + LapDistPct is a full lap off. The correction is a running
        /// balance: a forward LapDistPct wrap adds a lap, the lap counter advancing removes one, so
        /// it settles back at 0 the moment they agree again, and it is only ever kept while the car
        /// is actually sitting at the line.
        /// </summary>
        private void UpdateLapNumberCorrection(int carIdx, float prevDist, float lapDist, int prevLap, int lap)
        {
            int offset = GetLapNumberCorrection(carIdx);

            offset -= (lap - prevLap);

            if (prevDist > SF_WRAP_HIGH && lapDist < SF_WRAP_LOW)
                offset += 1;
            else if (prevDist < SF_WRAP_LOW && lapDist > SF_WRAP_HIGH)
                offset -= 1;

            // A correction only means anything within sight of the line: +1 while LapDistPct has
            // wrapped and the counter hasn't (the car reads just past S/F), -1 while the counter
            // has moved and LapDistPct hasn't (it reads just short of S/F). Anywhere else the
            // pairing is coincidental — a car towed across the line moves its lap counter with no
            // wrap to match — and the raw lap number is the better bet. This also covers the
            // settled case, where the two agree again and the offset is back to 0.
            bool isPlausibleDesync = (offset == 1 && lapDist < SF_WRAP_LOW)
                                  || (offset == -1 && lapDist > SF_WRAP_HIGH);

            if (isPlausibleDesync)
                _lapNumberCorrection[carIdx] = (offset, _globalFrameCounter);
            else
                _lapNumberCorrection.Remove(carIdx);
        }

        /// <summary>
        /// Lap-number correction for a car, or 0 once it has expired. Expiry matters because the
        /// correction is only ever meant to bridge the frames between the two telemetry variables
        /// agreeing; if the lap counter never catches up (an invalidated lap, a car towed to the
        /// pits), holding it would put the car a whole lap out of position indefinitely.
        /// </summary>
        private int GetLapNumberCorrection(int carIdx)
        {
            if (_lapNumberCorrection.TryGetValue(carIdx, out var correction) &&
                _globalFrameCounter - correction.Frame <= LAP_DESYNC_MAX_FRAMES)
            {
                return correction.Offset;
            }
            return 0;
        }

        private float GetPredictedLapDistPct(int carIdx)
        {
            if (!_lastValidLapDistPct.TryGetValue(carIdx, out float lastDist) ||
                !_lapDistPctVelocity.TryGetValue(carIdx, out float velocity) ||
                !_framesSinceValidData.TryGetValue(carIdx, out int framesSinceValid))
            {
                return -1f;
            }

            // Stopped/pitting — hold last known position instead of drifting.
            if (Math.Abs(velocity) < MIN_VELOCITY_THRESHOLD)
            {
                return lastDist;
            }

            float predictedDist = lastDist + (velocity * framesSinceValid);

            // Wrap to [0, 1).
            predictedDist = (predictedDist % 1.0f + 1.0f) % 1.0f;

            return predictedDist;
        }

        /// <summary>
        /// Whole laps the prediction has carried a car past S/F while its telemetry was invalid.
        /// <see cref="GetEffectiveLapDistPct"/> wraps its result into [0,1) because the relative
        /// display needs a track position, but the running-order sort pairs that with a cached
        /// CarIdxLap that stopped updating when the telemetry did. Without adding these laps back,
        /// a car predicted across the line reads a full lap down and drops to the tail of the
        /// order — which is the single-frame position flicker seen as cars cross S/F, since
        /// LapDistPct reads marginally outside [0,1] for a frame right at the line.
        /// </summary>
        private int GetPredictedLapOffset(int carIdx)
        {
            if (!HasValidCache(carIdx) ||
                !_lastValidLapDistPct.TryGetValue(carIdx, out float lastDist) ||
                !_lapDistPctVelocity.TryGetValue(carIdx, out float velocity) ||
                !_framesSinceValidData.TryGetValue(carIdx, out int framesSinceValid))
            {
                return 0;
            }

            // Mirrors GetPredictedLapDistPct: a stopped car holds its last position, no wrap.
            if (Math.Abs(velocity) < MIN_VELOCITY_THRESHOLD)
            {
                return 0;
            }

            return (int)Math.Floor(lastDist + (velocity * framesSinceValid));
        }
        #endregion

        #region Private Methods - Race Position Calculation
        private void CalculateRacePositions(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var currentLap = frame.CarIdxLap;
            var lapDistPct = frame.CarIdxLapDistPct;

            if (carClassIDs == null || currentLap == null || lapDistPct == null)
                return;

            var carsWithPositions = new List<CarPositionData>();
            var preGreenCars = new List<int>();
            List<int>? stoppedUnderCheckered = null;

            foreach (int carIdx in _validCarIndices)
            {
                if (carIdx < 0 || carIdx >= carClassIDs.Length)
                    continue;

                // Already frozen — don't include in the live sort.
                if (_carsFinished.Contains(carIdx))
                    continue;

                float effectiveLapDistPct = GetEffectiveLapDistPct(carIdx);
                int effectiveCurrentLap;

                if (lapDistPct[carIdx] >= 0f && lapDistPct[carIdx] <= 1f)
                {
                    // CarIdxLap and CarIdxLapDistPct don't always tick over in the same frame at
                    // S/F; realign them so a car crossing the line doesn't read a full lap out.
                    effectiveCurrentLap = currentLap[carIdx] + GetLapNumberCorrection(carIdx);
                }
                else if (_lastValidCurrentLap.TryGetValue(carIdx, out int cachedLap))
                {
                    // Predicting: the cached lap number froze with the telemetry, so add the laps
                    // the prediction has since wrapped through. The correction still applies —
                    // it fixes the cached baseline, the offset covers motion since.
                    effectiveCurrentLap = cachedLap + GetLapNumberCorrection(carIdx) + GetPredictedLapOffset(carIdx);
                }
                else
                {
                    continue;
                }

                if (effectiveLapDistPct < 0f)
                {
                    // The prediction has run out: the car leaves the roster next frame.
                    if (_isCheckeredFlag)
                        (stoppedUnderCheckered ??= new List<int>()).Add(carIdx);
                    continue;
                }

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

            // Before the overall sort replaces last frame's positions, which are the ones to hold.
            if (stoppedUnderCheckered != null)
                HoldCarsWhoseTelemetryStopped(stoppedUnderCheckered, carClassIDs, sessionDataProvider.CarNumbers);

            AssignOverallPositions(carsWithPositions);
            LogLeaderChange(carsWithPositions, frame, sessionDataProvider);

            var classGroups = carsWithPositions.GroupBy(c => c.ClassId);

            foreach (var classGroup in classGroups)
            {
                // Collect position numbers already taken by frozen (finished) cars in this class.
                var frozenPositions = new HashSet<int>();
                foreach (var kv in _finishingClassPositions)
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

            var frozenPositions = new HashSet<int>(_finishingOverallPositions.Values);

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