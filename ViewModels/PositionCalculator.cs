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
    /// 1. HasEverHadValidData - gates entry to display
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
        private const int PACE_CAR_CLASS_ID = 11; // iRacing pace/safety car class - excluded from overall field positions
        private const int SESSION_STATE_RACING = 4; // irsdk SessionState: green flag is out (ParadeLaps=3 -> Racing=4)

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

        #region Private Fields - Logging State
        private readonly HashSet<int> _lastFrameValidCars = new();
        private readonly Dictionary<int, bool> _isCurrentlyPredicting = new();
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
        /// Update the position calculator with the latest telemetry snapshot.
        /// Processes every frame (60Hz) with prediction for smooth display.
        /// </summary>
        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider? sessionDataProvider)
        {
            if (snapshot == null || sessionDataProvider == null || !sessionDataProvider.IsDataReady)
                return;

            _globalFrameCounter++;
            ProcessUpdate(snapshot, sessionDataProvider);
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
        /// Check if a car has ever had valid telemetry data.
        /// Used as gate keeper - once true, always true for the session.
        /// </summary>
        public bool HasEverHadValidData(int carIdx)
        {
            return _carsWithValidDataHistory.Contains(carIdx);
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
            _isCurrentlyPredicting.Clear();
            _carsWithInvalidLapDistPctLogged.Clear();

            _finishingClassPositions.Clear();
            _finishingOverallPositions.Clear();
            _carsFinished.Clear();
            _isCheckeredFlag = false;
            _leaderHasFinished = false;
            _lastSessionNum = -1;
            _lastLapCompleted.Clear();
            _carsHavingTakenGreen.Clear();

            Log.Info("PositionCalculator reset - all state cleared");
        }
        #endregion

        #region Private Methods - Update Processing
        private void ProcessUpdate(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            DetectSessionTransition(snapshot, sessionDataProvider);
            TrackCheckeredFlagState(snapshot);
            FreezeFinishingPositions(snapshot, sessionDataProvider);

            UpdateValidCarTracking(sessionDataProvider);
            UpdatePredictiveCache(snapshot, sessionDataProvider);

            if (!sessionDataProvider.ShouldUseFastestLapPositioning())
            {
                UpdateGreenFlagStatus(snapshot, sessionDataProvider);
                CalculateRacePositions(snapshot, sessionDataProvider);
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
        private void DetectSessionTransition(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            int currentSessionNum = snapshot.SessionNum;

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
        private void TrackCheckeredFlagState(SVappsLABSnapshot snapshot)
        {
            int sessionState = snapshot.SessionState;
            bool wasCheckeredFlag = _isCheckeredFlag;

            _isCheckeredFlag = (sessionState == 5 || sessionState == 6);

            if (!wasCheckeredFlag && _isCheckeredFlag)
            {
                Log.Info($"Checkered flag detected (SessionState: {sessionState}), beginning finishing position tracking");
            }
        }

        /// <summary>
        /// Freeze class positions for cars as they take the checkered flag.
        /// Only begins freezing after the P1 car (class leader) crosses S/F.
        /// Monitors CarIdxLapCompleted increments during checkered flag state.
        /// </summary>
        private void FreezeFinishingPositions(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            if (!_isCheckeredFlag)
            {
                return;
            }

            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carNumbers = sessionDataProvider.CarNumbers;
            var carLapCompleted = snapshot.CarIdxLapCompleted;

            if (carClassIDs == null || carNumbers == null || carLapCompleted == null)
            {
                return;
            }

            for (int carIdx = 0; carIdx < carLapCompleted.Length; carIdx++)
            {
                if (_carsFinished.Contains(carIdx))
                {
                    continue;
                }

                if (carIdx >= carClassIDs.Length || carIdx >= carNumbers.Length)
                {
                    continue;
                }

                int currentLapCompleted = carLapCompleted[carIdx];

                if (!_lastLapCompleted.ContainsKey(carIdx))
                {
                    _lastLapCompleted[carIdx] = currentLapCompleted;
                    continue;
                }

                int lastLapCompleted = _lastLapCompleted[carIdx];
                if (currentLapCompleted > lastLapCompleted)
                {
                    int classId = carClassIDs[carIdx];
                    int currentPosition = GetClassPosition(carIdx, classId);
                    int currentOverall = GetOverallPosition(carIdx);

                    if (!_leaderHasFinished && currentPosition == 1)
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

                    _lastLapCompleted[carIdx] = currentLapCompleted;
                }
            }
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
        private void UpdateGreenFlagStatus(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            // No car can take the green before the green flag is out. Until then everyone stays on
            // grid order (handled by GetPreGreenSortKey), which is steady through the parade lap.
            if (snapshot.SessionState < SESSION_STATE_RACING)
                return;

            var lapCompleted = snapshot.CarIdxLapCompleted;
            var lapDistPct = snapshot.CarIdxLapDistPct;
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
                    Log.Debug($"Car #{carNumbers[carIdx]} (idx {carIdx}) took the green flag - switching to live position calc");
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
        private void UpdatePredictiveCache(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            var lapDistPct = snapshot.CarIdxLapDistPct;
            var currentLap = snapshot.CarIdxLap;
            var onPitRoad = snapshot.CarIdxOnPitRoad;
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
                    var trackSurface = snapshot.CarIdxTrackSurface;
                    var carLaps = snapshot.CarIdxLap;
                    var bestLaps = snapshot.CarIdxBestLapTime;
                    var estTime = snapshot.CarIdxEstTime;

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
                _isCurrentlyPredicting.Remove(carIdx);
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
                _isCurrentlyPredicting[carIdx] = true;
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
        /// it settles back at 0 the moment they agree again. Anything beyond +/-1 lap is a tow or a
        /// reset rather than a desync, so the correction is dropped instead of carried.
        /// </summary>
        private void UpdateLapNumberCorrection(int carIdx, float prevDist, float lapDist, int prevLap, int lap)
        {
            int offset = GetLapNumberCorrection(carIdx);

            offset -= (lap - prevLap);

            if (prevDist > SF_WRAP_HIGH && lapDist < SF_WRAP_LOW)
                offset += 1;
            else if (prevDist < SF_WRAP_LOW && lapDist > SF_WRAP_HIGH)
                offset -= 1;

            if (offset == 0 || offset > 1 || offset < -1)
                _lapNumberCorrection.Remove(carIdx);
            else
                _lapNumberCorrection[carIdx] = (offset, _globalFrameCounter);
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
        #endregion

        #region Private Methods - Race Position Calculation
        private void CalculateRacePositions(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var currentLap = snapshot.CarIdxLap;
            var lapDistPct = snapshot.CarIdxLapDistPct;

            if (carClassIDs == null || currentLap == null || lapDistPct == null)
                return;

            var carsWithPositions = new List<CarPositionData>();
            var preGreenCars = new List<int>();

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
                    effectiveCurrentLap = currentLap[carIdx];
                }
                else if (_lastValidCurrentLap.TryGetValue(carIdx, out int cachedLap))
                {
                    effectiveCurrentLap = cachedLap;
                }
                else
                {
                    continue;
                }

                if (effectiveLapDistPct < 0f)
                    continue;

                // CarIdxLap and CarIdxLapDistPct don't always tick over in the same frame at S/F;
                // realign them so a car crossing the line doesn't read a full lap out for a frame.
                effectiveCurrentLap += GetLapNumberCorrection(carIdx);

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
                    TrackPosition = trackPosition,
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
                var classGrid = SelectGridSource(snapshot.CarIdxClassPosition, qualPositions, preGreenCars);
                var overallGrid = SelectGridSource(snapshot.CarIdxPosition, qualPositions, preGreenCars);

                foreach (var car in carsWithPositions)
                {
                    if (car.HasTakenGreen)
                        continue;

                    car.SortKey = GetPreGreenSortKey(car.CarIdx, classGrid);
                    car.OverallSortKey = GetPreGreenSortKey(car.CarIdx, overallGrid);
                }
            }

            AssignOverallPositions(carsWithPositions);

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
                .Where(c => c.ClassId != PACE_CAR_CLASS_ID)
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
            public float TrackPosition { get; set; }
            public bool HasTakenGreen { get; set; }
            public float SortKey { get; set; }
            public float OverallSortKey { get; set; }
        }
        #endregion
    }
}