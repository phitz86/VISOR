using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.Race
{
    /// <summary>
    /// Race positions for the overlay and the relative: which cars to show, where each one is on
    /// the lap, and its class and overall position, held through short telemetry gaps and frozen
    /// at the finish. This is the front door the view models call; the work is split between:
    /// - <see cref="CarTrackingCache"/>: the roster, prediction through gaps, and the lap-counter
    ///   correction at S/F
    /// - <see cref="RunningOrder"/>: the green-flag latch, grid order before it, and the sort
    /// - <see cref="FinishTracker"/>: the checkered, and freezing finishing positions into
    ///   <see cref="FinishingPositions"/>
    ///
    /// Each frame runs them in the order set out in <see cref="ProcessUpdate"/>.
    /// </summary>
    public class PositionCalculator
    {
        private readonly CarTrackingCache _tracking = new();
        private readonly FinishingPositions _finishing = new();
        private readonly RunningOrder _order;
        private readonly FinishTracker _finish;
        private int _lastSessionNum = -1;

        public PositionCalculator()
        {
            _order = new RunningOrder(_tracking, _finishing);
            _finish = new FinishTracker(_finishing, _order, _tracking);
        }

        #region Public Properties
        /// <summary>
        /// Set of car indices that have valid YAML data, have ever had valid telemetry,
        /// AND have valid cache (not expired). This is the filtered roster for displays.
        /// </summary>
        public IReadOnlySet<int> ValidCarIndices => _tracking.ValidCarIndices;
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

            _tracking.BeginFrame();
            ProcessUpdate(frame, sessionDataProvider);
        }

        /// <summary>
        /// Get the effective LapDistPct for a car (current valid data OR predicted value).
        /// Returns -1 if no valid data or cache expired.
        /// This is the ONLY method consumers should use to get car positions.
        /// </summary>
        public float GetEffectiveLapDistPct(int carIdx) => _tracking.GetEffectiveLapDistPct(carIdx);

        /// <summary>
        /// Get the class position for a specific car.
        /// Returns frozen finishing position if car has finished, otherwise calculated position.
        /// Returns calculated position in race mode, YAML position in practice/qual.
        /// </summary>
        public int GetClassPosition(int carIdx, int classId)
        {
            if (_finishing.ClassPositions.TryGetValue(carIdx, out int finishingPosition))
            {
                return finishingPosition;
            }

            return _order.GetClassPosition(carIdx, classId);
        }

        /// <summary>
        /// Get the overall (field-wide) position for a specific car.
        /// Returns frozen finishing position if car has finished, otherwise calculated position.
        /// Mirrors <see cref="GetClassPosition"/> but ignores class boundaries.
        /// </summary>
        public int GetOverallPosition(int carIdx)
        {
            if (_finishing.OverallPositions.TryGetValue(carIdx, out int finishingPosition))
            {
                return finishingPosition;
            }

            return _order.GetOverallPosition(carIdx);
        }

        /// <summary>
        /// Get the number of frames since valid data was received for a car.
        /// Returns int.MaxValue if car has never had valid data.
        /// Used to detect cars returning from telemetry gaps.
        /// </summary>
        public int GetFramesSinceValidData(int carIdx) => _tracking.GetFramesSinceValidData(carIdx);

        /// <summary>
        /// Reset all tracking state when session changes or connection lost.
        /// </summary>
        public void Reset()
        {
            _tracking.Reset();
            _order.Reset();
            _finishing.Clear();
            _finish.Reset();
            _lastSessionNum = -1;

            Log.Info("PositionCalculator reset - all state cleared");
        }
        #endregion

        #region Frame Order
        /// <summary>
        /// One frame, in an order that matters:
        /// - The finish goes first, on the previous frame's positions: a car that crossed S/F
        ///   this frame is frozen in the place it crossed in.
        /// - The roster is rebuilt before departures are looked for, since the difference between
        ///   the two rosters is this frame's departures.
        /// - The prediction is updated before the sort, which reads it.
        /// - The telemetry-stop hold runs before the sort replaces the previous frame's positions,
        ///   which are the ones it holds.
        /// </summary>
        private void ProcessUpdate(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            DetectSessionTransition(frame);
            _finish.TrackCheckeredFlagState(frame);
            _finish.LogFinishPhaseTransitions(frame, sessionDataProvider);
            _finish.FreezeFinishingPositions(frame, sessionDataProvider);

            _tracking.UpdateValidCarTracking(sessionDataProvider);
            _finish.FreezeDepartedCars(sessionDataProvider);
            _tracking.UpdatePredictiveCache(frame, sessionDataProvider);

            if (!sessionDataProvider.ShouldUseFastestLapPositioning())
            {
                _order.UpdateGreenFlagStatus(frame, sessionDataProvider);
                _finish.HoldCarsWhoseTelemetryStopped(sessionDataProvider);
                _order.CalculateRacePositions(frame, sessionDataProvider);
            }
            else
            {
                _order.Clear();
            }
        }

        /// <summary>
        /// Detect session transitions and clear finishing positions when session changes.
        /// </summary>
        private void DetectSessionTransition(PositionFrame frame)
        {
            int currentSessionNum = frame.SessionNum;

            if (_lastSessionNum != -1 && currentSessionNum != _lastSessionNum)
            {
                Log.Info($"Session transition detected ({_lastSessionNum} -> {currentSessionNum}), clearing finishing positions");
                _finishing.Clear();
                _finish.StartSession();
                _order.StartSession();
                _tracking.StartSession();
            }

            _lastSessionNum = currentSessionNum;
        }
        #endregion
    }
}
