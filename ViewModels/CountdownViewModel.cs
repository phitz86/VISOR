using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class CountdownViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
            Reset();

            if (sessionDataProvider.IsQualifyingSession(newSessionNum))
            {
                _totalQualifyingLaps = sessionDataProvider.GetSessionLaps(newSessionNum);
                Log.Info($"[Countdown] Qualifying session detected, total laps: {_totalQualifyingLaps}");
                if (_totalQualifyingLaps > 0)
                {
                    _isFirstQualiLap = true;
                    Log.Info($"[Countdown] Lap-limited qualifying initialized, will ignore out-lap");
                }
            }
        }

        /// <summary>
        /// Processes a new telemetry snapshot to update the timer display.
        /// </summary>
        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            int lapsRemaining = snapshot.SessionLapsRemain;
            double timeRemain = snapshot.SessionTimeRemain;
            int currentLap = snapshot.Lap;
            int sessionFlagsValue = snapshot.SessionFlags;

            bool isTimedSession = false;
            if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
            {
                int currentSessionNum = sessionDataProvider.CurrentSessionNum;
                int sessionLaps = sessionDataProvider.GetSessionLaps(currentSessionNum);
                isTimedSession = (sessionLaps == -1);
            }

            bool lapCompleted = currentLap > _lastLap;

            // Lap counter regressed — session restart.
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

            // Only count qualifying laps after green; consume the out-lap silently.
            if (_totalQualifyingLaps > 0 && lapCompleted && _greenFlagSeen)
            {
                if (_isFirstQualiLap)
                {
                    Log.Debug($"[Countdown] Out-lap completed, not counting");
                    _isFirstQualiLap = false;
                }
                else
                {
                    _qualifyingLapsCompleted++;
                    Log.Debug($"[Countdown] Flying lap completed, count: {_qualifyingLapsCompleted}/{_totalQualifyingLaps}");
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

                // Priority order: Finished > Final Lap > Quali lap counter > Race lap counter > Timed session.
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
                else if (_totalQualifyingLaps > 0 && _greenFlagSeen)
                {
                    newSymbol = "🏁";
                    int lapsToGo = _totalQualifyingLaps - _qualifyingLapsCompleted;

                    // Out-lap hasn't crossed S/F yet — show the full allotment.
                    if (_isFirstQualiLap)
                    {
                        lapsToGo = _totalQualifyingLaps;
                    }

                    newLapDisplay = lapsToGo == 1 ? "1 Lap" : $"{lapsToGo} Laps";
                }
                else if (!isTimedSession && lapsRemaining >= 0 && lapsRemaining < 10000)
                {
                    newSymbol = "🏁";
                    // iRacing's SessionLapsRemain counts down to 0 for the leader on the final lap, so add 1.
                    string latestLapDisplay = lapsRemaining == 0 ? "1 Lap" : $"{lapsRemaining + 1} Laps";
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