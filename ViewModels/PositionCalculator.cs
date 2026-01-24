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
        #endregion

        #region Private Fields - Core State
        private int _globalFrameCounter = 0;
        private readonly HashSet<int> _validCarIndices = new();
        private readonly Dictionary<(int carIdx, int classId), int> _cachedPositions = new();
        #endregion

        #region Private Fields - Finishing Position Tracking
        private readonly Dictionary<int, int> _finishingClassPositions = new();
        private readonly HashSet<int> _carsFinished = new();
        private bool _isCheckeredFlag = false;
        private bool _leaderHasFinished = false;
        private int _lastSessionNum = -1;
        private readonly Dictionary<int, int> _lastLapCompleted = new();
        #endregion

        #region Private Fields - Prediction System
        // Tier 1: Gate keeper
        private readonly HashSet<int> _carsWithValidDataHistory = new();

        // Tier 2: Velocity tracking for prediction
        private readonly Dictionary<int, float> _lastValidLapDistPct = new();
        private readonly Dictionary<int, float> _lapDistPctVelocity = new();
        private readonly Dictionary<int, int> _lastValidCurrentLap = new();

        // Tier 3: Cache management
        private readonly Dictionary<int, int> _framesSinceValidData = new();
        private readonly Dictionary<int, int> _predictionStartFrame = new();
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
        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
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
            // If we have current valid data, return it
            if (_framesSinceValidData.TryGetValue(carIdx, out int framesSinceValid) &&
                framesSinceValid == 0 &&
                _lastValidLapDistPct.TryGetValue(carIdx, out float currentValid))
            {
                return currentValid;
            }

            // If cache is still valid, return predicted value
            if (HasValidCache(carIdx))
            {
                return GetPredictedLapDistPct(carIdx);
            }

            // No valid data available
            return -1f;
        }

        /// <summary>
        /// Get the class position for a specific car.
        /// Returns frozen finishing position if car has finished, otherwise calculated position.
        /// Returns calculated position in race mode, YAML position in practice/qual.
        /// </summary>
        public int GetClassPosition(int carIdx, int classId)
        {
            // Return frozen finishing position if car has finished
            if (_finishingClassPositions.TryGetValue(carIdx, out int finishingPosition))
            {
                return finishingPosition;
            }

            // Otherwise return live calculated position
            if (_cachedPositions.TryGetValue((carIdx, classId), out int position))
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
            _carsWithValidDataHistory.Clear();
            _lastValidLapDistPct.Clear();
            _lapDistPctVelocity.Clear();
            _lastValidCurrentLap.Clear();
            _framesSinceValidData.Clear();
            _predictionStartFrame.Clear();
            _lastFrameValidCars.Clear();
            _isCurrentlyPredicting.Clear();
            _carsWithInvalidLapDistPctLogged.Clear();

            // Clear finishing position tracking
            _finishingClassPositions.Clear();
            _carsFinished.Clear();
            _isCheckeredFlag = false;
            _leaderHasFinished = false;
            _lastSessionNum = -1;
            _lastLapCompleted.Clear();

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
                CalculateRacePositions(snapshot, sessionDataProvider);
            }
            else
            {
                _cachedPositions.Clear();
            }
        }
        #endregion

        #region Private Methods - Finishing Position Tracking
        /// <summary>
        /// Detect session transitions and clear finishing positions when session changes.
        /// </summary>
        private void DetectSessionTransition(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);

            if (_lastSessionNum != -1 && currentSessionNum != _lastSessionNum)
            {
                Log.Info($"Session transition detected ({_lastSessionNum} -> {currentSessionNum}), clearing finishing positions");
                _finishingClassPositions.Clear();
                _carsFinished.Clear();
                _isCheckeredFlag = false;
                _leaderHasFinished = false;
                _lastLapCompleted.Clear();
            }

            _lastSessionNum = currentSessionNum;
        }

        /// <summary>
        /// Track checkered flag state based on SessionState.
        /// SessionState: 5 = Checkered, 6 = CoolDown
        /// </summary>
        private void TrackCheckeredFlagState(SVappsLABSnapshot snapshot)
        {
            int sessionState = snapshot.GetValue<int>("SessionState", -1);
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
            var carLapCompleted = snapshot.GetValue<int[]>("CarIdxLapCompleted");

            if (carClassIDs == null || carNumbers == null || carLapCompleted == null)
            {
                return;
            }

            // Check each car for lap completion increments
            for (int carIdx = 0; carIdx < carLapCompleted.Length; carIdx++)
            {
                // Skip if car already finished
                if (_carsFinished.Contains(carIdx))
                {
                    continue;
                }

                // Skip if car doesn't have valid YAML data
                if (carIdx >= carClassIDs.Length || carIdx >= carNumbers.Length)
                {
                    continue;
                }

                int currentLapCompleted = carLapCompleted[carIdx];

                // Check if this is the first time we're seeing this car
                if (!_lastLapCompleted.ContainsKey(carIdx))
                {
                    _lastLapCompleted[carIdx] = currentLapCompleted;
                    continue;
                }

                // Check if lap completed count has incremented (car crossed S/F)
                int lastLapCompleted = _lastLapCompleted[carIdx];
                if (currentLapCompleted > lastLapCompleted)
                {
                    int classId = carClassIDs[carIdx];
                    int currentPosition = GetClassPosition(carIdx, classId);

                    // Check if this is the P1 car (class leader)
                    if (!_leaderHasFinished && currentPosition == 1)
                    {
                        // This is the leader finishing - freeze their position and enable freezing for others
                        _finishingClassPositions[carIdx] = currentPosition;
                        _carsFinished.Add(carIdx);
                        _leaderHasFinished = true;

                        Log.Info($"LEADER Car #{carNumbers[carIdx]} (idx {carIdx}) took checkered flag - frozen at P{currentPosition} (LapCompleted: {lastLapCompleted} -> {currentLapCompleted})");
                    }
                    else if (_leaderHasFinished && currentPosition > 0)
                    {
                        // Leader has already finished, freeze this car's position
                        _finishingClassPositions[carIdx] = currentPosition;
                        _carsFinished.Add(carIdx);

                        Log.Info($"Car #{carNumbers[carIdx]} (idx {carIdx}) took checkered flag - frozen at P{currentPosition} (LapCompleted: {lastLapCompleted} -> {currentLapCompleted})");
                    }
                    // else: Leader hasn't finished yet, don't freeze this car (lapped traffic ahead of leader)

                    _lastLapCompleted[carIdx] = currentLapCompleted;
                }
            }
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
                // Car must have YAML data AND have valid history AND have valid cache
                bool hasYamlData = !string.IsNullOrEmpty(carNumbers[i]) &&
                                   !string.IsNullOrEmpty(userNames[i]);

                if (hasYamlData &&
                    _carsWithValidDataHistory.Contains(i) &&
                    HasValidCache(i))
                {
                    _validCarIndices.Add(i);
                }
            }

            // Log changes to valid car roster
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
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad");
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

                // Log invalid LapDistPct once per car per invalid stretch
                if (!hasValidData && !_carsWithInvalidLapDistPctLogged.Contains(i))
                {
                    var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
                    var carLaps = snapshot.GetValue<int[]>("CarIdxLap");
                    var bestLaps = snapshot.GetValue<float[]>("CarIdxBestLapTime");
                    var estTime = snapshot.GetValue<float[]>("CarIdxEstTime");

                    int surface = (trackSurface != null && i < trackSurface.Length) ? trackSurface[i] : -999;
                    int lap = (carLaps != null && i < carLaps.Length) ? carLaps[i] : -999;
                    float bestLap = (bestLaps != null && i < bestLaps.Length) ? bestLaps[i] : -999f;

                    Log.Info($"Car #{carNumbers[i]} (idx {i}) telemetry snapshot - LapDist:{lapDistPct[i]:F3}, EstTime:{estTime:F2}, Surface:{surface}, Lap:{lap}, BestLap:{bestLap:F2}, OnPit:{isOnPitRoad}");
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
            // Mark as having valid data history (Tier 1: Gate keeper)
            if (!_carsWithValidDataHistory.Contains(carIdx))
            {
                _carsWithValidDataHistory.Add(carIdx);
                Log.Info($"Car #{carNumbers[carIdx]} first valid data - added to history");
            }

            // Reset invalid logging flag so we can capture the NEXT time this car goes invalid
            // This allows us to log mid-race telemetry gaps, not just session startup
            _carsWithInvalidLapDistPctLogged.Remove(carIdx);

            // Calculate velocity for prediction (Tier 2: Prediction)
            if (_lastValidLapDistPct.TryGetValue(carIdx, out float lastDist))
            {
                float delta = lapDist - lastDist;

                // Handle lap boundary wrap-around
                if (delta < -0.5f) delta += 1.0f;
                if (delta > 0.5f) delta -= 1.0f;

                // Smooth velocity with exponential averaging (70% old, 30% new)
                float instantVelocity = delta;
                float smoothedVelocity = _lapDistPctVelocity.GetValueOrDefault(carIdx, 0f);
                smoothedVelocity = (instantVelocity * 0.3f) + (smoothedVelocity * 0.7f);

                // Zero out velocity if on pit road (don't predict through pits)
                if (isOnPit)
                {
                    smoothedVelocity = 0f;
                }

                _lapDistPctVelocity[carIdx] = smoothedVelocity;
            }

            // Update cache
            _lastValidLapDistPct[carIdx] = lapDist;
            _lastValidCurrentLap[carIdx] = lap;
            _framesSinceValidData[carIdx] = 0;

            // Log prediction recovery if we were predicting
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
            // Increment frames since valid data (Tier 3: Cache expiration)
            int framesSinceValid = _framesSinceValidData.GetValueOrDefault(carIdx, 0) + 1;
            _framesSinceValidData[carIdx] = framesSinceValid;

            // Start tracking prediction if this is the first invalid frame
            if (!_predictionStartFrame.ContainsKey(carIdx) &&
                _lastValidLapDistPct.ContainsKey(carIdx))
            {
                _predictionStartFrame[carIdx] = _globalFrameCounter;
                _isCurrentlyPredicting[carIdx] = true;
            }

            // Log when cache expires
            if (framesSinceValid == MAX_CACHE_AGE_FRAMES &&
                _carsWithValidDataHistory.Contains(carIdx))
            {
                Log.Info($"Car #{carNumbers[carIdx]} cache expired after {MAX_CACHE_AGE_FRAMES} frames (3 seconds)");
            }
        }

        private float GetPredictedLapDistPct(int carIdx)
        {
            if (!_lastValidLapDistPct.TryGetValue(carIdx, out float lastDist) ||
                !_lapDistPctVelocity.TryGetValue(carIdx, out float velocity) ||
                !_framesSinceValidData.TryGetValue(carIdx, out int framesSinceValid))
            {
                return -1f; // No data to predict from
            }

            // Don't predict if velocity is too small (stopped/pitting)
            if (Math.Abs(velocity) < MIN_VELOCITY_THRESHOLD)
            {
                return lastDist; // Return last known position (frozen)
            }

            // Predict position based on velocity
            float predictedDist = lastDist + (velocity * framesSinceValid);

            // Wrap around track (0.0 to 1.0)
            predictedDist = (predictedDist % 1.0f + 1.0f) % 1.0f;

            return predictedDist;
        }
        #endregion

        #region Private Methods - Race Position Calculation
        private void CalculateRacePositions(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");

            if (carClassIDs == null || currentLap == null || lapDistPct == null)
                return;

            var carsWithPositions = new List<CarPositionData>();

            foreach (int carIdx in _validCarIndices)
            {
                if (carIdx < 0 || carIdx >= carClassIDs.Length)
                    continue;

                // Use effective LapDistPct (current or predicted)
                float effectiveLapDistPct = GetEffectiveLapDistPct(carIdx);
                int effectiveCurrentLap;

                // Try current lap data
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
                    continue; // No lap data available
                }

                // Skip if no effective position available
                if (effectiveLapDistPct < 0f)
                    continue;

                carsWithPositions.Add(new CarPositionData
                {
                    CarIdx = carIdx,
                    ClassId = carClassIDs[carIdx],
                    CurrentLap = effectiveCurrentLap,
                    LapDistPct = effectiveLapDistPct,
                    TrackPosition = effectiveCurrentLap + effectiveLapDistPct
                });
            }

            // Calculate positions by class
            var classGroups = carsWithPositions.GroupBy(c => c.ClassId);

            foreach (var classGroup in classGroups)
            {
                var sortedCars = classGroup.OrderByDescending(c => c.TrackPosition).ToList();

                for (int i = 0; i < sortedCars.Count; i++)
                {
                    var car = sortedCars[i];
                    int position = i + 1;
                    _cachedPositions[(car.CarIdx, car.ClassId)] = position;
                }
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
        }
        #endregion
    }
}