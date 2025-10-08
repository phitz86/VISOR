using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class CountdownViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // --- Public Properties for UI Binding ---
        private string _timeRemainingDisplay = "--:--";
        private string _timeRemainingSymbol = "⏳";

        public string TimeRemainingDisplay
        {
            get => _timeRemainingDisplay;
            private set { _timeRemainingDisplay = value; OnPropertyChanged(); }
        }
        public string TimeRemainingSymbol
        {
            get => _timeRemainingSymbol;
            private set { _timeRemainingSymbol = value; OnPropertyChanged(); }
        }

        // --- Internal State Fields ---
        private bool _greenFlagSeen;
        private int _lastLap;
        private string _currentLapDisplay;
        private int _totalQualifyingLaps;
        private int _qualifyingLapsCompleted;
        private bool _isFirstQualiLap;
        private bool _finalLapLatched;
        private bool _finishedLatched;
        private bool _pendingWhiteFlag;
        private bool _pendingCheckeredFlag;

        public CountdownViewModel()
        {
            Reset();
        }

        /// <summary>
        /// Resets all timer-related state for a new session.
        /// </summary>
        public void Reset()
        {
            TimeRemainingDisplay = "--:--";
            TimeRemainingSymbol = "⏳";
            _greenFlagSeen = false;
            _lastLap = -1;
            _currentLapDisplay = "-- Laps";
            _totalQualifyingLaps = 0;
            _qualifyingLapsCompleted = 0;
            _isFirstQualiLap = false;
            _finalLapLatched = false;
            _finishedLatched = false;
            _pendingWhiteFlag = false;
            _pendingCheckeredFlag = false;
        }

        /// <summary>
        /// Initializes state based on the type of session that is starting.
        /// </summary>
        public void OnSessionTransition(ISessionDataProvider sessionDataProvider, int newSessionNum)
        {
            // Reset state for the new session
            Reset();

            // Check if this is a lap-limited qualifying session and set up state
            if (sessionDataProvider.IsQualifyingSession(newSessionNum))
            {
                _totalQualifyingLaps = sessionDataProvider.GetSessionLaps(newSessionNum);
                System.Diagnostics.Debug.WriteLine($"[Countdown] Qualifying session detected, total laps: {_totalQualifyingLaps}");
                if (_totalQualifyingLaps > 0)
                {
                    _isFirstQualiLap = true;
                    System.Diagnostics.Debug.WriteLine($"[Countdown] Lap-limited qualifying initialized, will ignore out-lap");
                }
            }
        }

        /// <summary>
        /// Processes a new telemetry snapshot to update the timer display.
        /// </summary>
        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            int lapsRemaining = snapshot.GetValue<int>("SessionLapsRemain", 0);
            double timeRemain = snapshot.GetValue<double>("SessionTimeRemain", 0.0);
            int currentLap = snapshot.GetValue<int>("Lap", 0);
            int sessionFlagsValue = snapshot.GetValue<int>("SessionFlags", 0);

            bool isTimedSession = false;
            if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
            {
                int currentSessionNum = sessionDataProvider.CurrentSessionNum;
                int sessionLaps = sessionDataProvider.GetSessionLaps(currentSessionNum);
                isTimedSession = (sessionLaps == -1);
            }

            bool lapCompleted = currentLap > _lastLap;

            // Reset flags when lap counter resets (session restart)
            if (currentLap < _lastLap)
            {
                _greenFlagSeen = false;
                _pendingWhiteFlag = false;
                _pendingCheckeredFlag = false;
            }

            // Track session flags
            if ((sessionFlagsValue & (int)SessionFlags.Green) == (int)SessionFlags.Green)
            {
                _greenFlagSeen = true;
            }

            if ((sessionFlagsValue & (int)SessionFlags.White) == (int)SessionFlags.White)
            {
                _pendingWhiteFlag = true;
            }

            if ((sessionFlagsValue & (int)SessionFlags.Checkered) == (int)SessionFlags.Checkered)
            {
                _pendingCheckeredFlag = true;
            }

            // Increment qualifying lap counter if applicable
            // Only count laps AFTER we've seen the green flag
            if (_totalQualifyingLaps > 0 && lapCompleted && _greenFlagSeen)
            {
                if (_isFirstQualiLap)
                {
                    // This is the out-lap, consume it without counting
                    System.Diagnostics.Debug.WriteLine($"[Countdown] Out-lap completed, not counting");
                    _isFirstQualiLap = false;
                }
                else
                {
                    // This is a flying lap, count it
                    _qualifyingLapsCompleted++;
                    System.Diagnostics.Debug.WriteLine($"[Countdown] Flying lap completed, count: {_qualifyingLapsCompleted}/{_totalQualifyingLaps}");
                }
            }

            bool shouldShowTimer = _greenFlagSeen || timeRemain > 0;

            if (shouldShowTimer)
            {
                // Latch final lap and finished states
                if (_pendingWhiteFlag && lapCompleted)
                {
                    _finalLapLatched = true;
                }
                if (_pendingCheckeredFlag && lapCompleted)
                {
                    _finishedLatched = true;
                }

                string newLapDisplay;
                string newSymbol;

                // Priority 1: Finished
                if (_finishedLatched)
                {
                    newSymbol = "🏁";
                    newLapDisplay = "FINISHED";
                }
                // Priority 2: Final Lap
                else if (_finalLapLatched)
                {
                    newSymbol = "🏁";
                    newLapDisplay = "Final Lap";
                }
                // Priority 3: Qualifying lap counter (personal lap tracking)
                else if (_totalQualifyingLaps > 0 && _greenFlagSeen)
                {
                    newSymbol = "🏁";
                    int lapsToGo = _totalQualifyingLaps - _qualifyingLapsCompleted;

                    // If still on out-lap (haven't crossed line yet after green), show total laps
                    if (_isFirstQualiLap)
                    {
                        lapsToGo = _totalQualifyingLaps;
                    }

                    newLapDisplay = lapsToGo == 1 ? "1 Lap" : $"{lapsToGo} Laps";
                }
                // Priority 4: Race lap counter (leader-based tracking)
                else if (!isTimedSession && lapsRemaining >= 0 && lapsRemaining < 10000)
                {
                    newSymbol = "🏁";
                    // +1 because iRacing's lapsRemaining counts down to 0 (leader on final lap shows 0)
                    string latestLapDisplay = lapsRemaining == 0 ? "1 Lap" : $"{lapsRemaining + 1} Laps";
                    if (lapCompleted)
                    {
                        _currentLapDisplay = latestLapDisplay;
                    }
                    newLapDisplay = _currentLapDisplay;
                }
                // Priority 5: Timed session
                else if (timeRemain > 0)
                {
                    newSymbol = "⏳";
                    TimeSpan remaining = TimeSpan.FromSeconds(timeRemain);
                    if (remaining.TotalHours >= 1.0)
                        newLapDisplay = $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    else
                        newLapDisplay = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
                }
                // Fallback
                else
                {
                    newSymbol = TimeRemainingSymbol;
                    newLapDisplay = "--:--";
                }

                // Update display if changed
                if (TimeRemainingDisplay != newLapDisplay)
                {
                    TimeRemainingDisplay = newLapDisplay;
                }
                if (TimeRemainingSymbol != newSymbol)
                {
                    TimeRemainingSymbol = newSymbol;
                }
            }

            _lastLap = currentLap;
        }
    }
}