using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.Race
{
    /// <summary>
    /// Which cars to show, and where each one is on the lap, through short telemetry gaps.
    /// Three tiers:
    /// 1. Valid-data history - gates entry to the roster
    /// 2. Predictive LapDistPct - smooth extrapolation during brief gaps
    /// 3. Cache expiration - removes truly disconnected cars after timeout
    ///
    /// Also bridges the frame or two at S/F where CarIdxLap and CarIdxLapDistPct disagree about
    /// which lap a car is on. Part of <see cref="PositionCalculator"/>, which sets the frame order.
    /// </summary>
    internal sealed class CarTrackingCache
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

        #region Private Fields - Roster
        private int _globalFrameCounter = 0;
        private readonly HashSet<int> _validCarIndices = new();
        private readonly HashSet<int> _lastFrameValidCars = new();
        private readonly HashSet<int> _carsWithInvalidLapDistPctLogged = new();
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

        #region Roster and Lookups
        /// <summary>
        /// Set of car indices that have valid YAML data, have ever had valid telemetry,
        /// AND have valid cache (not expired). This is the filtered roster for displays.
        /// </summary>
        public IReadOnlySet<int> ValidCarIndices => _validCarIndices;

        /// <summary>
        /// The previous frame's roster. <see cref="UpdateValidCarTracking"/> copies it before
        /// rebuilding <see cref="ValidCarIndices"/>, so the difference is this frame's departures.
        /// </summary>
        public IReadOnlySet<int> PreviousValidCarIndices => _lastFrameValidCars;

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
        /// The CarIdxLap from the car's last valid frame. It stops updating when the telemetry
        /// does, so while a car is predicted it is the baseline the running order adds to.
        /// </summary>
        public bool TryGetLastValidLap(int carIdx, out int lap) => _lastValidCurrentLap.TryGetValue(carIdx, out lap);
        #endregion

        #region Frame and Session
        /// <summary>
        /// Advance the frame clock that prediction lengths and lap-correction expiry are counted in.
        /// </summary>
        public void BeginFrame() => _globalFrameCounter++;

        /// <summary>
        /// A new session: lap corrections from the last one no longer mean anything.
        /// </summary>
        public void StartSession() => _lapNumberCorrection.Clear();

        public void Reset()
        {
            _globalFrameCounter = 0;
            _validCarIndices.Clear();
            _carsWithValidDataHistory.Clear();
            _lastValidLapDistPct.Clear();
            _lapDistPctVelocity.Clear();
            _lastValidCurrentLap.Clear();
            _framesSinceValidData.Clear();
            _predictionStartFrame.Clear();
            _lapNumberCorrection.Clear();
            _lastFrameValidCars.Clear();
            _carsWithInvalidLapDistPctLogged.Clear();
        }
        #endregion

        #region Valid Car Tracking
        public void UpdateValidCarTracking(ISessionDataProvider sessionDataProvider)
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

        #region Predictive Cache
        public void UpdatePredictiveCache(PositionFrame frame, ISessionDataProvider sessionDataProvider)
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
        public int GetLapNumberCorrection(int carIdx)
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
        public int GetPredictedLapOffset(int carIdx)
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
    }
}
