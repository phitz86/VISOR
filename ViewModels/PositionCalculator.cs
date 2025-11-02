using System;
using System.Collections.Generic;
using System.Linq;
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
        /// Returns calculated position in race mode, YAML position in practice/qual.
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

            System.Diagnostics.Debug.WriteLine("[PositionCalc] Reset - all state cleared");
        }
        #endregion

        #region Private Methods - Update Processing
        private void ProcessUpdate(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
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
                // This ensures expired cars (disconnected >3 seconds) are removed from the roster
                if (!string.IsNullOrEmpty(carNumbers[i]) &&
                    !string.IsNullOrEmpty(userNames[i]) &&
                    HasEverHadValidData(i) &&
                    HasValidCache(i))
                {
                    _validCarIndices.Add(i);

                    if (!_lastFrameValidCars.Contains(i))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[PositionCalc] Car #{carNumbers[i]} ({userNames[i]}) added to valid roster");
                    }
                }
                else if (_lastFrameValidCars.Contains(i))
                {
                    // Car removed from roster (disconnected or cache expired)
                    System.Diagnostics.Debug.WriteLine(
                        $"[PositionCalc] Car #{carNumbers[i]} ({userNames[i]}) removed from roster (cache expired or left session)");
                }
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

            if (lapDistPct == null || currentLap == null || carNumbers == null)
                return;

            // Process all cars with YAML data
            for (int i = 0; i < 64; i++)
            {
                if (string.IsNullOrEmpty(carNumbers[i]))
                    continue;

                bool hasValidData = lapDistPct[i] >= 0f && lapDistPct[i] <= 1f;
                bool isOnPitRoad = onPitRoad != null && i < onPitRoad.Length && onPitRoad[i];

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
                System.Diagnostics.Debug.WriteLine(
                    $"[PositionCalc] Car #{carNumbers[carIdx]} first valid data - added to history");
            }

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
                    System.Diagnostics.Debug.WriteLine(
                        $"[PositionCalc-Predict] Car #{carNumbers[carIdx]} prediction ended after {predictionDuration} frames");
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
                System.Diagnostics.Debug.WriteLine(
                    $"[PositionCalc-Cache] Car #{carNumbers[carIdx]} cache expired after {MAX_CACHE_AGE_FRAMES} frames");
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