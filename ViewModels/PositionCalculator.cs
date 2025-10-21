using System;
using System.Collections.Generic;
using System.Linq;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Calculates race positions and tracks valid cars for the relative display.
    /// Updates every 30 frames (~500ms at 60Hz) to filter out disconnected cars
    /// and provide stable position data to the UI.
    /// </summary>
    public class PositionCalculator
    {
        #region Constants
        private const int UPDATE_INTERVAL_FRAMES = 30; // ~500ms at 60Hz
        // CHANGED: Increased the timeout for better data resilience
        private const int VALIDITY_TIMEOUT_FRAMES = 120; // Remove cars after 120 frames (~2 seconds) of invalid data
        // ADDED: A threshold to prevent logging insignificant data blips
        private const int INTERRUPTION_LOG_THRESHOLD_FRAMES = 15; // ~250ms at 60Hz
        #endregion

        #region Private Fields
        private int _frameCounter = 0;
        private int _globalFrameCounter = 0;

        // ADDED: Tracks the start frame of a data interruption for logging purposes
        private readonly Dictionary<int, int> _carInterruptionStartFrame = new();

        // Valid car tracking
        private readonly Dictionary<int, int> _carLastValidFrame = new();
        private readonly HashSet<int> _validCarIndices = new();

        // Race position calculation results
        private readonly Dictionary<(int carIdx, int classId), int> _classPositions = new();

        // For logging state changes
        private readonly HashSet<int> _lastFrameValidCars = new();
        #endregion

        #region Public Properties
        /// <summary>
        /// Set of car indices that have had valid LapDistPct data within the last 120 frames.
        /// This is used by RelativeDisplayBuilder to filter which cars to display.
        /// </summary>
        public IReadOnlySet<int> ValidCarIndices => _validCarIndices;
        #endregion

        #region Public Methods
        /// <summary>
        /// Update the position calculator with the latest telemetry snapshot.
        /// Should be called every frame (60Hz) from MainViewModel.
        /// Internal logic throttles to process every 30 frames.
        /// </summary>
        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            if (snapshot == null || sessionDataProvider == null || !sessionDataProvider.IsDataReady)
                return;

            _frameCounter++;
            _globalFrameCounter++;

            // Only process every 30 frames (~500ms)
            if (_frameCounter >= UPDATE_INTERVAL_FRAMES)
            {
                ProcessUpdate(snapshot, sessionDataProvider);
                _frameCounter = 0;
            }
        }

        /// <summary>
        /// Get the class position for a specific car.
        /// In race mode, returns calculated position based on lap + track position.
        /// In practice/qualifying mode, returns position from YAML fastest lap data.
        /// </summary>
        public int GetClassPosition(int carIdx, int classId)
        {
            // Return calculated race position
            if (_classPositions.TryGetValue((carIdx, classId), out int position))
            {
                return position;
            }

            return -1; // Unknown/invalid position
        }

        /// <summary>
        /// Reset all tracking state when session changes or connection lost.
        /// </summary>
        public void Reset()
        {
            _frameCounter = 0;
            _globalFrameCounter = 0;
            _carLastValidFrame.Clear();
            _validCarIndices.Clear();
            _classPositions.Clear();
            _lastFrameValidCars.Clear();
            // ADDED: Ensure the new interruption tracker is also cleared
            _carInterruptionStartFrame.Clear();
        }
        #endregion

        #region Private Methods - Update Processing
        private void ProcessUpdate(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            // Step 1: Update which cars have valid data
            UpdateValidCarTracking(snapshot);

            // Step 2: Calculate race positions (only in race mode)
            if (!sessionDataProvider.ShouldUseFastestLapPositioning())
            {
                CalculateRacePositions(snapshot, sessionDataProvider);
            }
            else
            {
                // Clear race positions in practice/qual modes
                _classPositions.Clear();
            }
        }
        #endregion

        #region Private Methods - Valid Car Tracking
        private void UpdateValidCarTracking(SVappsLABSnapshot snapshot)
        {
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var carNumbers = snapshot.GetValue<string[]>("CarIdxCarNumber");
            var userNames = snapshot.GetValue<string[]>("CarIdxUserName");

            if (lapDistPct == null || carNumbers == null || userNames == null)
                return;

            // REPLACED BLOCK: Swapped simple check with intelligent interruption tracking and logging
            for (int i = 0; i < 64; i++)
            {
                if (string.IsNullOrEmpty(carNumbers[i]) || string.IsNullOrEmpty(userNames[i]))
                    continue;

                bool hasValidData = lapDistPct[i] >= 0f && lapDistPct[i] <= 1f;

                if (hasValidData)
                {
                    // Car has valid data. Update its last known valid frame.
                    _carLastValidFrame[i] = _globalFrameCounter;

                    // Now, check if it just recovered from a tracked interruption.
                    if (_carInterruptionStartFrame.Remove(i, out int startFrame))
                    {
                        int interruptionDuration = _globalFrameCounter - startFrame;
                        if (interruptionDuration > INTERRUPTION_LOG_THRESHOLD_FRAMES)
                        {
                            // This is our key metric!
                            System.Diagnostics.Debug.WriteLine(
                                $"[PositionCalc-Metrics] Car #{carNumbers[i]} data recovered after an interruption of {interruptionDuration} frames.");
                        }
                    }
                }
                else
                {
                    // Car has invalid data. If we aren't already tracking it,
                    // add it to the dictionary to mark the start of the interruption.
                    if (!_carInterruptionStartFrame.ContainsKey(i))
                    {
                        _carInterruptionStartFrame.Add(i, _globalFrameCounter);
                    }
                }
            }

            // Store previous state for logging
            _lastFrameValidCars.Clear();
            foreach (var carIdx in _validCarIndices)
            {
                _lastFrameValidCars.Add(carIdx);
            }

            // Rebuild valid car set based on timeout window
            _validCarIndices.Clear();
            foreach (var kvp in _carLastValidFrame)
            {
                int carIdx = kvp.Key;
                int lastValidFrame = kvp.Value;

                // Include cars that had valid data within the timeout window
                if (_globalFrameCounter - lastValidFrame <= VALIDITY_TIMEOUT_FRAMES)
                {
                    _validCarIndices.Add(carIdx);

                    // Log if this car was just re-added
                    if (!_lastFrameValidCars.Contains(carIdx))
                    {
                        LogCarAdded(carIdx, carNumbers, userNames);
                    }
                }
                else
                {
                    // Log if this car was just removed
                    if (_lastFrameValidCars.Contains(carIdx))
                    {
                        LogCarRemoved(carIdx, carNumbers, userNames);
                    }
                }
            }
        }

        private void LogCarAdded(int carIdx, string[] carNumbers, string[] userNames)
        {
            if (carIdx >= 0 && carIdx < carNumbers.Length && carIdx < userNames.Length)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PositionCalc] Car #{carNumbers[carIdx]} ({userNames[carIdx]}) re-added with valid LapDistPct");
            }
        }

        private void LogCarRemoved(int carIdx, string[] carNumbers, string[] userNames)
        {
            if (carIdx >= 0 && carIdx < carNumbers.Length && carIdx < userNames.Length)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PositionCalc] Car #{carNumbers[carIdx]} ({userNames[carIdx]}) removed - no valid LapDistPct for {VALIDITY_TIMEOUT_FRAMES} frames");
            }
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

            // Build list of valid cars with their position data
            var carsWithPositions = new List<CarPositionData>();

            foreach (int carIdx in _validCarIndices)
            {
                if (carIdx >= 0 && carIdx < carClassIDs.Length)
                {
                    carsWithPositions.Add(new CarPositionData
                    {
                        CarIdx = carIdx,
                        ClassId = carClassIDs[carIdx],
                        CurrentLap = currentLap[carIdx],
                        LapDistPct = lapDistPct[carIdx],
                        TrackPosition = currentLap[carIdx] + lapDistPct[carIdx]
                    });
                }
            }

            // Clear previous positions
            _classPositions.Clear();

            // Group by class and calculate positions within each class
            var clasGroups = carsWithPositions.GroupBy(c => c.ClassId);

            foreach (var classGroup in clasGroups)
            {
                // Sort by track position (lap + lap distance percentage) descending
                var sortedCars = classGroup.OrderByDescending(c => c.TrackPosition).ToList();

                // Assign positions (1st, 2nd, 3rd, etc.)
                for (int i = 0; i < sortedCars.Count; i++)
                {
                    var car = sortedCars[i];
                    int position = i + 1;
                    _classPositions[(car.CarIdx, car.ClassId)] = position;
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