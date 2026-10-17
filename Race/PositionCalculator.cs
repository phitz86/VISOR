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

        public PositionCalculator()
        {
            _order = new RunningOrder(_tracking, _finishing);
            _finish = new FinishTracker(_finishing, _order, _tracking);
        }

        #region Public Properties
        /// <inheritdoc cref="CarTrackingCache.ValidCarIndices"/>
        public IReadOnlySet<int> ValidCarIndices => _tracking.ValidCarIndices;
        #endregion

        #region Public Methods
        /// <summary>
        /// Update the position calculator with the latest telemetry frame.
        /// Processes every frame (60Hz) with prediction for smooth display.
        /// </summary>
        public void Update(PositionFrame frame, ISessionDataProvider? sessionDataProvider)
        {
            if (sessionDataProvider == null || !sessionDataProvider.IsDataReady)
                return;

            _tracking.BeginFrame();
            ProcessUpdate(frame, sessionDataProvider);
        }

        /// <inheritdoc cref="CarTrackingCache.GetEffectiveLapDistPct"/>
        public float GetEffectiveLapDistPct(int carIdx) => _tracking.GetEffectiveLapDistPct(carIdx);

        /// <summary>
        /// The car's class position: its frozen finishing place once it has finished, otherwise its
        /// place in the running order, or -1. Practice and qualifying have no running order; the
        /// displays take those positions from the session's fastest laps instead.
        /// </summary>
        public int GetClassPosition(int carIdx, int classId) =>
            _finishing.ClassPositions.TryGetValue(carIdx, out int finished) ? finished : _order.GetClassPosition(carIdx, classId);

        /// <summary>As <see cref="GetClassPosition"/>, across the whole field.</summary>
        public int GetOverallPosition(int carIdx) =>
            _finishing.OverallPositions.TryGetValue(carIdx, out int finished) ? finished : _order.GetOverallPosition(carIdx);

        /// <inheritdoc cref="CarTrackingCache.GetFramesSinceValidData"/>
        public int GetFramesSinceValidData(int carIdx) => _tracking.GetFramesSinceValidData(carIdx);

        /// <summary>
        /// Forget everything. The owner calls this on every session change and disconnect: the
        /// calculator doesn't watch the session number itself.
        /// </summary>
        public void Reset()
        {
            _tracking.Reset();
            _order.Reset();
            _finishing.Clear();
            _finish.Reset();

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
            _finish.TrackCheckeredFlagState(frame);
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
        #endregion
    }
}
