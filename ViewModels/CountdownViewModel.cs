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

        // iRacing fills SessionTime with a 24h+ placeholder for sessions that run to a lap count
        // with no clock (lone qualifying, lap-limited races). A value below this is a real budget.
        private const double UNLIMITED_SESSION_TIME_SECONDS = 86400.0;

        private string _timeRemainingDisplay = "--:--";
        private string _timeRemainingSymbol = "⏳";
        private string _secondaryTimerDisplay = string.Empty;
        private bool _showSecondaryTimer;

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

        /// <summary>
        /// Session clock shown beneath the primary readout when that readout is counting laps but
        /// the session also runs to a time limit — standard open qualifying (so many flying laps,
        /// so many minutes to set them). Empty whenever <see cref="ShowSecondaryTimer"/> is false.
        /// </summary>
        public string SecondaryTimerDisplay
        {
            get => _secondaryTimerDisplay;
            private set { if (_secondaryTimerDisplay == value) return; _secondaryTimerDisplay = value; OnPropertyChanged(); }
        }
        public bool ShowSecondaryTimer
        {
            get => _showSecondaryTimer;
            private set { if (_showSecondaryTimer == value) return; _showSecondaryTimer = value; OnPropertyChanged(); }
        }

        private bool _greenFlagSeen;
        private int _lastLap;
        private string _currentLapDisplay = "-- Laps";
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
            SecondaryTimerDisplay = string.Empty;
            ShowSecondaryTimer = false;
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

            // Logged for every session so the laps/clock pair that drives the readout (and whether
            // the secondary clock will appear at all) is visible in the log without a repro.
            int laps = sessionDataProvider.GetSessionLaps(newSessionNum);
            double timeBudget = sessionDataProvider.GetSessionTimeSeconds(newSessionNum);
            Log.Info($"[Countdown] Session {newSessionNum}: laps {(laps == -1 ? "unlimited" : laps.ToString())}, " +
                     $"SessionTime {timeBudget:F0}s");

            if (sessionDataProvider.IsQualifyingSession(newSessionNum))
            {
                _totalQualifyingLaps = laps;
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
        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider? sessionDataProvider)
        {
            int lapsRemaining = snapshot.SessionLapsRemain;
            double timeRemain = snapshot.SessionTimeRemain;
            int currentLap = snapshot.Lap;
            int sessionFlagsValue = snapshot.SessionFlags;

            bool isTimedSession = false;
            bool hasTimeLimit = false;
            if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
            {
                int currentSessionNum = sessionDataProvider.CurrentSessionNum;
                int sessionLaps = sessionDataProvider.GetSessionLaps(currentSessionNum);
                isTimedSession = (sessionLaps == -1);

                // A lap-limited session can still be on a clock (open qualifying: 2 flying laps,
                // 8 minutes to set them). Those are the sessions where the laps-to-go readout
                // alone hides half the picture, so the secondary clock fills it in.
                double sessionTimeSeconds = sessionDataProvider.GetSessionTimeSeconds(currentSessionNum);
                hasTimeLimit = sessionTimeSeconds > 0.0 && sessionTimeSeconds < UNLIMITED_SESSION_TIME_SECONDS;
            }

            // The first observation of the lap counter only seeds it: _lastLap starts at -1, and
            // treating "anything > -1" as a completed lap would let the very first frame consume a
            // flag that is already flying (starting or reconnecting VISOR mid-race under the white
            // or checkered would latch Final Lap / FINISHED on the spot).
            bool lapCompleted = _lastLap >= 0 && currentLap > _lastLap;

            // A flag only counts toward a latch if it was already flying on an EARLIER frame than
            // the crossing that consumes it. Latching on flags raised in the same telemetry sample
            // would let a checkered that comes out as the player crosses S/F end their race a lap
            // early — the routine case in multiclass, where the overall leader is lapping traffic
            // and finishes alongside a car from a slower class.
            bool whiteWasAlreadyFlying = _pendingWhiteFlag;
            bool checkeredWasAlreadyFlying = _pendingCheckeredFlag;

            // Lap counter regressed — session restart.
            if (currentLap < _lastLap)
            {
                _greenFlagSeen = false;
                _pendingWhiteFlag = false;
                _pendingCheckeredFlag = false;
                whiteWasAlreadyFlying = false;
                checkeredWasAlreadyFlying = false;
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
            // True when the primary readout is spending itself on something other than the clock in a
            // lap-limited session: the lap counter, Final Lap, or FINISHED. Those are the states that
            // hide the deadline, so they are the ones the secondary clock has to cover. A timed
            // session's primary is the clock itself, so there is nothing to add there.
            bool primaryShowsLaps = false;

            if (shouldShowTimer)
            {
                if (whiteWasAlreadyFlying && lapCompleted && !_finalLapLatched)
                {
                    _finalLapLatched = true;
                    LogLatch("Final Lap", snapshot, currentLap, lapsRemaining, timeRemain);
                }
                if (checkeredWasAlreadyFlying && lapCompleted && !_finishedLatched)
                {
                    _finishedLatched = true;
                    LogLatch("FINISHED", snapshot, currentLap, lapsRemaining, timeRemain);
                }

                // The one case the "flag must already be flying" rule can get wrong: a flag raised on
                // the very sample of the crossing. Logged so that, if it ever happens, it is visible
                // rather than showing up as a Final Lap / FINISHED that is simply late or missing.
                if (lapCompleted && !whiteWasAlreadyFlying && _pendingWhiteFlag && !_finalLapLatched)
                {
                    Log.Info($"[Countdown] White flag raised on the same sample as the lap {currentLap} crossing - Final Lap NOT latched");
                }
                if (lapCompleted && !checkeredWasAlreadyFlying && _pendingCheckeredFlag && !_finishedLatched)
                {
                    Log.Info($"[Countdown] Checkered flag raised on the same sample as the lap {currentLap} crossing - FINISHED NOT latched");
                }

                string newLapDisplay;
                string newSymbol;

                // Priority order: Finished > Final Lap > Quali lap counter > Race lap counter > Timed session.
                if (_finishedLatched)
                {
                    newSymbol = "🏁";
                    newLapDisplay = "FINISHED";
                    primaryShowsLaps = !isTimedSession;
                }
                else if (_finalLapLatched)
                {
                    newSymbol = "🏁";
                    newLapDisplay = "Final Lap";
                    primaryShowsLaps = !isTimedSession;
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
                    primaryShowsLaps = true;
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
                    primaryShowsLaps = true;
                }
                else if (timeRemain > 0)
                {
                    newSymbol = "⏳";
                    newLapDisplay = FormatSessionClock(timeRemain);
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

            // The clock only earns its own line when the primary readout is spending itself on a lap
            // count and the session is genuinely on a timer as well. The timeRemain bound is a second
            // guard: SessionTimeRemain reports a placeholder of its own in untimed sessions, and the
            // secondary must never show a 23-hour countdown.
            bool showSecondary = primaryShowsLaps && hasTimeLimit
                && timeRemain > 0 && timeRemain < UNLIMITED_SESSION_TIME_SECONDS;

            SecondaryTimerDisplay = showSecondary ? FormatSessionClock(timeRemain) : string.Empty;
            ShowSecondaryTimer = showSecondary;

            _lastLap = currentLap;
        }

        /// <summary>
        /// Record the moment Final Lap or FINISHED latches, with the state that caused it. These
        /// readouts are driven entirely by per-car SessionFlags bits and lap crossings, and nothing
        /// else in the log shows which crossing consumed which flag.
        /// </summary>
        private static void LogLatch(string what, SVappsLABSnapshot snapshot, int lap, int lapsRemaining, double timeRemain)
        {
            Log.Info($"[Countdown] {what} latched on lap {lap}: SessionState {snapshot.SessionState}, " +
                     $"flags 0x{snapshot.SessionFlags & IRacingIds.FinishFlagsMask:X}, lapsRemain {lapsRemaining}, timeRemain {timeRemain:F0}s");
        }

        /// <summary>
        /// Session clock as h:mm:ss past the hour, m:ss below it.
        /// </summary>
        private static string FormatSessionClock(double secondsRemaining)
        {
            TimeSpan remaining = TimeSpan.FromSeconds(secondsRemaining);
            return remaining.TotalHours >= 1.0
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
        }
    }
}