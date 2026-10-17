using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.Race
{
    /// <summary>
    /// Finishing positions: watches for the checkered, freezes each car's place as it takes the
    /// flag (once the overall leader has), and holds the place of a car that leaves the session
    /// or whose telemetry stops under it. Writes <see cref="FinishingPositions"/>; reads the
    /// places from <see cref="RunningOrder"/> and the roster from <see cref="CarTrackingCache"/>.
    /// Part of <see cref="PositionCalculator"/>.
    /// </summary>
    internal sealed class FinishTracker
    {
        private readonly FinishingPositions _finishing;
        private readonly RunningOrder _order;
        private readonly CarTrackingCache _tracking;

        #region Private Fields - Finishing Position Tracking
        private bool _isCheckeredFlag = false;
        private bool _leaderHasFinished = false;
        private readonly Dictionary<int, int> _lastLapCompleted = new();
        #endregion

        public FinishTracker(FinishingPositions finishing, RunningOrder order, CarTrackingCache tracking)
        {
            _finishing = finishing;
            _order = order;
            _tracking = tracking;
        }

        #region Session
        public void Reset()
        {
            _isCheckeredFlag = false;
            _leaderHasFinished = false;
            _lastLapCompleted.Clear();
        }
        #endregion

        #region Checkered Flag
        /// <summary>
        /// Track checkered flag state based on SessionState.
        /// SessionState: 5 = Checkered, 6 = CoolDown
        /// </summary>
        public void TrackCheckeredFlagState(PositionFrame frame)
        {
            int sessionState = frame.SessionState;
            bool wasCheckeredFlag = _isCheckeredFlag;

            _isCheckeredFlag = SessionStates.IsCheckered(sessionState);

            if (!wasCheckeredFlag && _isCheckeredFlag)
            {
                Log.Info($"Checkered flag detected (SessionState: {sessionState}), beginning finishing position tracking");
            }
        }
        #endregion

        #region Freezing Finishing Positions
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
        public void FreezeFinishingPositions(PositionFrame frame, ISessionDataProvider sessionDataProvider)
        {
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carNumbers = sessionDataProvider.CarNumbers;
            var carLapCompleted = frame.CarIdxLapCompleted;

            for (int carIdx = 0; carIdx < PositionFrame.CarCount; carIdx++)
            {
                int currentLapCompleted = carLapCompleted[carIdx];

                // A car with no telemetry reports -1. Keep the last real baseline rather than
                // storing the sentinel, or the car's return would read as a lap completion.
                if (currentLapCompleted < 0)
                {
                    continue;
                }

                bool isFirstObservation = !_lastLapCompleted.TryGetValue(carIdx, out int lastLapCompleted);
                _lastLapCompleted[carIdx] = currentLapCompleted;

                if (!_isCheckeredFlag || isFirstObservation || _finishing.IsFinished(carIdx))
                {
                    continue;
                }

                if (currentLapCompleted > lastLapCompleted)
                {
                    int classId = carClassIDs[carIdx];
                    int currentPosition = _order.GetClassPosition(carIdx, classId);
                    int currentOverall = _order.GetOverallPosition(carIdx);

                    // The gate is the OVERALL leader finishing, not any class leader: iRacing ends the
                    // race for everyone when the overall winner takes the flag, and a slower class's
                    // P1 crossing in the few seconds between the state flip and the winner's crossing
                    // would otherwise open the gate early and freeze cars that are still racing.
                    if (!_leaderHasFinished && currentOverall == 1)
                    {
                        _finishing.Freeze(carIdx, currentPosition, currentOverall);
                        _leaderHasFinished = true;

                        Log.Info($"LEADER Car #{carNumbers[carIdx]} (idx {carIdx}) took checkered flag - frozen at P{currentPosition} (overall P{currentOverall}) (LapCompleted: {lastLapCompleted} -> {currentLapCompleted})");
                    }
                    else if (_leaderHasFinished && currentPosition > 0)
                    {
                        // Don't freeze lapped traffic ahead of the class leader until the leader has crossed.
                        _finishing.Freeze(carIdx, currentPosition, currentOverall);

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
                        Log.Warning($"Car #{carNumbers[carIdx]} (idx {carIdx}) completed a lap under the checkered but was NOT frozen - " +
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
        public void FreezeDepartedCars(ISessionDataProvider sessionDataProvider)
        {
            if (!_isCheckeredFlag)
            {
                return;
            }

            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carNumbers = sessionDataProvider.CarNumbers;

            // The previous frame's roster, less this frame's: this frame's departures.
            foreach (int carIdx in _tracking.PreviousValidCarIndices)
            {
                if (_tracking.ValidCarIndices.Contains(carIdx) || _finishing.IsFinished(carIdx))
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
        /// prediction covers (three seconds), during the checkered. Runs just before the sort, on
        /// the frame the car's prediction runs out. The sort drops the car on that frame, one
        /// frame before it leaves the roster; by then <see cref="FreezeDepartedCars"/> would find
        /// no position to hold, and the car behind would already have taken its place. The
        /// positions held are the previous frame's, since this frame's sort hasn't replaced them.
        /// </summary>
        public void HoldCarsWhoseTelemetryStopped(ISessionDataProvider sessionDataProvider)
        {
            if (!_isCheckeredFlag)
            {
                return;
            }

            var carClassIDs = sessionDataProvider.CarClassIDs;
            var carNumbers = sessionDataProvider.CarNumbers;

            foreach (int carIdx in _tracking.ValidCarIndices)
            {
                if (_finishing.IsFinished(carIdx))
                {
                    continue;
                }

                // Live or still predicted: the sort places it as usual.
                if (_tracking.GetEffectiveLapDistPct(carIdx) >= 0f)
                {
                    continue;
                }

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
            classPosition = _order.GetClassPosition(carIdx, classId);
            overallPosition = _order.GetOverallPosition(carIdx);

            if (classPosition <= 0 || overallPosition <= 0)
            {
                return false;
            }

            _finishing.Freeze(carIdx, classPosition, overallPosition);

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
    }
}
