using System;
using System.Collections.Generic;
using System.Linq;
using VISOR.Diagnostics;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Tracks each car's rolling race pace and provides a per-car reference lap time
    /// for use in the S/F line gap correction in RelativeDisplayBuilder.
    ///
    /// Uses CarIdxLastLapTime to accumulate a rolling window of clean laps per car,
    /// with outlier filtering to reject pit laps and caution-pace laps, and a
    /// pace-shift detection system to handle genuine step changes in pace.
    ///
    /// Bootstrap seeds are drawn from qualifying times or CarClassEstLapTime until
    /// real race laps accumulate.
    /// </summary>
    public class PaceReferenceLapManager
    {
        #region Constants

        private const int MAX_ROLLING_LAPS = 5;
        private const float FAST_OUTLIER_THRESHOLD = 5.0f;  // Reject if >5s faster than ref
        private const float SLOW_OUTLIER_THRESHOLD = 10.0f; // Reject if >10s slower than ref
        private const int WAITING_ROOM_CAPACITY = 3;
        private const float FALLBACK_LAP_TIME = 120f;

        #endregion

        #region Per-Car State

        private struct CarPaceState
        {
            public int LastKnownLapCompleted;
            public Queue<float> RollingLaps;
            public float BootstrapLap;
            public bool BootstrapActive;
            public Queue<float> WaitingRoom;
        }

        private readonly CarPaceState[] _cars = new CarPaceState[64];

        #endregion

        #region Fields

        private readonly SessionDataCoordinator _coordinator;

        #endregion

        #region Constructor

        public PaceReferenceLapManager(SessionDataCoordinator coordinator)
        {
            _coordinator = coordinator;
            Reset();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Called once per frame from RelativeDisplayBuilder.Calculate(), before the row loop.
        /// Detects lap completions via CarIdxLapCompleted and records clean lap times.
        /// </summary>
        public void Update(SVappsLABSnapshot snapshot)
        {
            var lapCompleted = snapshot.GetValue<int[]>("CarIdxLapCompleted");
            var lastLapTime = snapshot.GetValue<float[]>("CarIdxLastLapTime");

            if (lapCompleted == null || lastLapTime == null) return;

            int count = Math.Min(lapCompleted.Length, 64);

            for (int carIdx = 0; carIdx < count; carIdx++)
            {
                int completed = lapCompleted[carIdx];

                if (completed <= 0) continue;

                ref var car = ref _cars[carIdx];

                // First time seeing this car: seed the counter, don't record a lap
                if (car.LastKnownLapCompleted == -1)
                {
                    car.LastKnownLapCompleted = completed;
                    continue;
                }

                int delta = completed - car.LastKnownLapCompleted;
                car.LastKnownLapCompleted = completed;

                if (delta == 0) continue;

                // Multi-lap jump (disconnect/reconnect/reset): skip recording
                if (delta != 1) continue;

                if (carIdx >= lastLapTime.Length) continue;
                float candidate = lastLapTime[carIdx];
                if (candidate <= 0f) continue;

                ProcessCandidateLap(carIdx, candidate);
            }
        }

        /// <summary>
        /// Returns the best available pace estimate for a car.
        /// Priority: rolling average > bootstrap seed > hardcoded fallback (120s).
        /// </summary>
        public float GetReferenceLap(int carIdx)
        {
            if (carIdx < 0 || carIdx >= 64) return FALLBACK_LAP_TIME;

            ref var car = ref _cars[carIdx];

            if (car.RollingLaps.Count > 0)
                return car.RollingLaps.Average();

            if (car.BootstrapLap > 0f)
                return car.BootstrapLap;

            // Lazy bootstrap: attempt to seed from session data on first call
            SeedBootstrap(carIdx);

            return car.BootstrapLap > 0f ? car.BootstrapLap : FALLBACK_LAP_TIME;
        }

        /// <summary>
        /// Clears all per-car data. Called on session transitions and disconnect.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < 64; i++)
            {
                _cars[i] = new CarPaceState
                {
                    LastKnownLapCompleted = -1,
                    RollingLaps = new Queue<float>(),
                    BootstrapLap = 0f,
                    BootstrapActive = true,
                    WaitingRoom = new Queue<float>()
                };
            }

            Log.Info("[PaceManager] State reset");
        }

        #endregion

        #region Private Methods

        private void ProcessCandidateLap(int carIdx, float candidate)
        {
            ref var car = ref _cars[carIdx];
            float currentRef = GetReferenceLap(carIdx);

            bool isFastOutlier = candidate < (currentRef - FAST_OUTLIER_THRESHOLD);
            bool isSlowOutlier = candidate > (currentRef + SLOW_OUTLIER_THRESHOLD);

            if (!isFastOutlier && !isSlowOutlier)
            {
                AcceptLap(carIdx, candidate, currentRef);
            }
            else
            {
                RejectLap(carIdx, candidate, currentRef);
            }
        }

        private void AcceptLap(int carIdx, float candidate, float currentRef)
        {
            ref var car = ref _cars[carIdx];

            // Check for pace shift: accepted lap is closer to waiting room average than rolling average
            if (car.WaitingRoom.Count > 0 && car.RollingLaps.Count > 0)
            {
                float waitingAvg = car.WaitingRoom.Average();
                float rollingAvg = car.RollingLaps.Average();

                if (Math.Abs(candidate - waitingAvg) < Math.Abs(candidate - rollingAvg))
                {
                    Log.Info($"[PaceManager] Car {carIdx}: pace shift confirmed by accepted lap (waiting avg={waitingAvg:F2}s, rolling avg={rollingAvg:F2}s)");
                    FlushAndReseed(carIdx, candidate);
                    return;
                }
            }

            // On bootstrap->rolling transition, seed the rolling window with the bootstrap
            // value so the first real lap doesn't cause a sudden reference shift.
            // The bootstrap gradually phases out as real laps fill the window.
            if (car.BootstrapActive)
            {
                car.BootstrapActive = false;

                if (car.BootstrapLap > 0f)
                {
                    car.RollingLaps.Enqueue(car.BootstrapLap);
                    float blended = (car.BootstrapLap + candidate) / 2f;
                    Log.Info($"[PaceManager] Car {carIdx}: bootstrap -> rolling (first lap={candidate:F2}s, bootstrap={car.BootstrapLap:F2}s, blended ref={blended:F2}s)");
                }
                else
                {
                    Log.Info($"[PaceManager] Car {carIdx}: bootstrap -> rolling (first lap={candidate:F2}s, no bootstrap to blend)");
                }
            }

            // Normal acceptance
            car.RollingLaps.Enqueue(candidate);
            if (car.RollingLaps.Count > MAX_ROLLING_LAPS)
                car.RollingLaps.Dequeue();

            car.WaitingRoom.Clear();

            Log.Debug($"[PaceManager] Car {carIdx}: lap recorded {candidate:F2}s (ref={currentRef:F2}s, window={car.RollingLaps.Count})");
        }

        private void RejectLap(int carIdx, float candidate, float currentRef)
        {
            ref var car = ref _cars[carIdx];

            car.WaitingRoom.Enqueue(candidate);
            if (car.WaitingRoom.Count > WAITING_ROOM_CAPACITY)
                car.WaitingRoom.Dequeue();

            Log.Debug($"[PaceManager] Car {carIdx}: lap rejected {candidate:F2}s (ref={currentRef:F2}s, waiting={car.WaitingRoom.Count})");

            // Pace shift: three consecutive outliers confirm a genuine pace change
            if (car.WaitingRoom.Count >= WAITING_ROOM_CAPACITY)
            {
                Log.Info($"[PaceManager] Car {carIdx}: pace shift detected, re-seeding from waiting room (avg={car.WaitingRoom.Average():F2}s)");
                FlushAndReseed(carIdx, null);
            }
        }

        /// <summary>
        /// Flushes the rolling window and replaces it with waiting room entries.
        /// If a confirmed accepted lap is provided, it is appended after the waiting room entries.
        /// </summary>
        private void FlushAndReseed(int carIdx, float? confirmedLap)
        {
            ref var car = ref _cars[carIdx];

            car.RollingLaps.Clear();
            foreach (float t in car.WaitingRoom)
                car.RollingLaps.Enqueue(t);

            if (confirmedLap.HasValue)
                car.RollingLaps.Enqueue(confirmedLap.Value);

            car.WaitingRoom.Clear();
        }

        private void SeedBootstrap(int carIdx)
        {
            ref var car = ref _cars[carIdx];

            if (!_coordinator.IsDataReady) return;

            int sessionNum = _coordinator.CurrentSessionNum;

            // Race session: prefer qualifying lap time as a current-conditions proxy
            if (_coordinator.IsRaceSession(sessionNum))
            {
                float[] qualTimes = _coordinator.GetQualifyResultsFastestTimes();
                if (qualTimes != null && carIdx < qualTimes.Length && qualTimes[carIdx] > 0f)
                {
                    car.BootstrapLap = qualTimes[carIdx];
                    Log.Debug($"[PaceManager] Car {carIdx}: bootstrap from qualifying time ({car.BootstrapLap:F2}s)");
                    return;
                }
            }

            // Practice, Qualifying, or Race fallback: use class estimated lap time
            var staticData = _coordinator.GetStaticEventData();
            if (staticData?.Drivers != null && staticData.Drivers.TryGetValue(carIdx, out var driver)
                && driver.CarClassEstLapTime > 0f)
            {
                car.BootstrapLap = driver.CarClassEstLapTime;
                Log.Debug($"[PaceManager] Car {carIdx}: bootstrap from CarClassEstLapTime ({car.BootstrapLap:F2}s)");
                return;
            }

            Log.Debug($"[PaceManager] Car {carIdx}: no bootstrap data available, will use fallback {FALLBACK_LAP_TIME}s");
        }

        #endregion
    }
}
