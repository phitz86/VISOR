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
                if (_totalQualifyingLaps > 0)
                {
                    _isFirstQualiLap = true;
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

            if (currentLap < _lastLap)
            {
                _greenFlagSeen = false;
                _pendingWhiteFlag = false;
                _pendingCheckeredFlag = false;
            }

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
            if (_totalQualifyingLaps > 0 && lapCompleted && _greenFlagSeen)
            {
                if (_isFirstQualiLap)
                {
                    _isFirstQualiLap = false;
                }
                else
                {
                    _qualifyingLapsCompleted++;
                }
            }

            bool shouldShowTimer = _greenFlagSeen || timeRemain > 0;

            if (shouldShowTimer)
            {
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

                if (_finishedLatched)
                {
                    newSymbol = "🏁";
                    newLapDisplay = "FINISHED";
                }
                else if (_finalLapLatched)
                {
                    newSymbol = "🏁";
                    newLapDisplay = "Final Lap";
                }
                else if (_totalQualifyingLaps > 0 && _qualifyingLapsCompleted < _totalQualifyingLaps)
                {
                    newSymbol = "🏁";
                    int lapsToGo = _totalQualifyingLaps - _qualifyingLapsCompleted;
                    newLapDisplay = $"{lapsToGo} Laps";
                }
                else if (!isTimedSession && lapsRemaining >= 0 && lapsRemaining < 10000)
                {
                    newSymbol = "🏁";
                    string latestLapDisplay = $"{lapsRemaining + 1} Laps";
                    if (lapCompleted)
                    {
                        _currentLapDisplay = latestLapDisplay;
                    }
                    newLapDisplay = _currentLapDisplay;
                }
                else if (timeRemain > 0)
                {
                    newSymbol = "⏳";
                    TimeSpan remaining = TimeSpan.FromSeconds(timeRemain);
                    if (remaining.TotalHours >= 1.0)
                        newLapDisplay = $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    else
                        newLapDisplay = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
                }
                else
                {
                    newSymbol = TimeRemainingSymbol;
                    newLapDisplay = "--:--";
                }

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