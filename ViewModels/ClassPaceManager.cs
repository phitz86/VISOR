using System;
using System.Collections.Generic;
using System.Linq;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class ClassPaceManager
    {
        // Cache the "Truth" lap time for each class (ClassID -> Seconds)
        private readonly Dictionary<int, float> _classBestLaps = new();

        // Track the source quality for debugging/logging
        private readonly Dictionary<int, string> _classDataSource = new();

        public void Update(LiveSessionData liveData, StaticEventData staticData)
        {
            // 1. Gold Standard: Live Session Fastest Laps
            // This captures what drivers are actually doing right now (weather/track conditions)
            foreach (var kvp in liveData.SessionFastestLaps)
            {
                // liveData.SessionFastestLaps is Dictionary<SessionNum, List<FastestLapResult>>
                // We only care about the list of results
                foreach (var result in kvp.Value)
                {
                    // Find the class for this car
                    if (staticData.Drivers.TryGetValue(result.CarIdx, out var driver))
                    {
                        if (result.FastestTime > 0)
                        {
                            UpdateClassPace(driver.CarClassID, result.FastestTime, "LiveSession");
                        }
                    }
                }
            }

            // 2. Silver Standard: Qualifying Results (from YAML)
            // Useful for Race Start before anyone completes Lap 1
            if (staticData.Weekend.TrackLength > 0 && _classBestLaps.Count == 0)
            {
                // Scan all drivers to find the best qualify time per class
                foreach (var driver in staticData.Drivers.Values)
                {
                    if (liveData.QualifyFastestTimes.TryGetValue(driver.CarNumberRaw, out float qualTime))
                    {
                        if (qualTime > 0)
                            UpdateClassPace(driver.CarClassID, qualTime, "Qualifying");
                    }
                }
            }

            // 3. Bronze Standard: iRacing Estimates (Cold Start / Practice)
            // Useful when you first join a practice session and NOBODY has set a time yet
            if (_classBestLaps.Count == 0)
            {
                foreach (var driver in staticData.Drivers.Values)
                {
                    if (driver.CarClassEstLapTime > 0)
                    {
                        UpdateClassPace(driver.CarClassID, driver.CarClassEstLapTime, "iRacingEstimate");
                    }
                }
            }
        }

        private void UpdateClassPace(int classId, float time, string source)
        {
            if (time <= 0) return;

            // Simple logic: We always want the FASTEST known time for the class
            // because that represents the "Pace Potential" of the threat.
            if (!_classBestLaps.TryGetValue(classId, out float currentBest) || time < currentBest)
            {
                _classBestLaps[classId] = time;
                _classDataSource[classId] = source;
                // Log only on significant changes to avoid spam
                Log.Info($"[ClassPaceManager] Class {classId} Pace Updated: {time:F3}s (Source: {source})");
            }
        }

        public float GetThreatScalar(int playerClassID, int opponentClassID)
        {
            // Same class is always 1:1
            if (playerClassID == opponentClassID) return 1.0f;

            // If we have data for both, calculate the scalar
            if (_classBestLaps.TryGetValue(playerClassID, out float myClassTime) &&
                _classBestLaps.TryGetValue(opponentClassID, out float oppClassTime))
            {
                if (oppClassTime > 0)
                {
                    // Example: Me (100s) / Threat (80s) = 1.25x Scalar
                    // Example: Me (100s) / Slower (120s) = 0.83x Scalar
                    return myClassTime / oppClassTime;
                }
            }

            // Fallback: If we know nothing, assume equality (safest default)
            return 1.0f;
        }

        public void Reset()
        {
            _classBestLaps.Clear();
            _classDataSource.Clear();
        }
    }
}