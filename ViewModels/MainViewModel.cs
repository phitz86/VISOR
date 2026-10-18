using VISOR.Diagnostics;
using VISOR.Race;
using VISOR.Telemetry;
using VISOR.Settings;

namespace VISOR.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        public FuelViewModel FuelVM { get; private set; }
        public RelativeViewModel RelativeVM { get; private set; }
        public DeltaBarViewModel DeltaBarVM { get; private set; }
        public WarningsViewModel WarningsVM { get; private set; }
        public CountdownViewModel CountdownVM { get; private set; }
        public TrackTempViewModel TrackTempVM { get; private set; }
        public TrackLocationViewModel TrackLocationVM { get; private set; }

        private readonly ClassColorManager _classColorManager;
        private readonly SettingsManager _settingsManager;
        private readonly PositionCalculator _positionCalculator;
        private readonly ShiftPointProvider _shiftPoints = new();

        private int _lastSessionNum = -1;
        private int _lastSessionState = -999;
        private string _lastSubSessionId = string.Empty;
        private bool? _playerWasOnPitRoad = null;

        private const string LapTimePlaceholder = "-:--.---";

        public string ClassPositionNumber { get; private set; } = "--";
        public string GearDisplay { get; private set; } = "N";
        public ShiftState ShiftState { get; private set; } = ShiftState.Normal;
        public ShiftCalibration ShiftCalibration { get; private set; } = ShiftCalibration.None;
        private readonly ShiftCue _shiftCue = new();
        public string LastLapTime { get; private set; } = LapTimePlaceholder;
        public string BestLapTime { get; private set; } = LapTimePlaceholder;

        public bool ShowGear => _settingsManager.Settings.ShowRow0;
        public bool ShowPosition => _settingsManager.Settings.ShowRow0;
        public bool ShowTimeRemaining => _settingsManager.Settings.ShowRow1;
        public bool ShowFuelRemaining => _settingsManager.Settings.ShowRow1;
        public bool ShowLapDelta => _settingsManager.Settings.ShowRow2;
        public bool ShowLapTimes => _settingsManager.Settings.ShowRow3;
        public bool ShowRelative => _settingsManager.Settings.ShowRow4;
        public bool ShowWarnings => _settingsManager.Settings.ShowRow5;

        // Individual Row 5 element toggles. The row grid is gated by ShowWarnings; these
        // gate each element within it (the track-location element additionally hides itself
        // when the current track isn't in the catalog, via TrackLocationVM.LocationVisible).
        public bool ShowTrackLocationElement => _settingsManager.Settings.ShowTrackLocation;
        public bool ShowIncidentElement => _settingsManager.Settings.ShowIncidentCounter;
        public bool ShowTrackTempElement => _settingsManager.Settings.ShowTrackTemp;

        public double ScaleFactor => WindowScale.ForMainWindow(_settingsManager.Settings.WindowSize);

        public MainViewModel()
        {
            _classColorManager = new ClassColorManager();
            _settingsManager = SettingsManager.Instance;
            _positionCalculator = new PositionCalculator();

            FuelVM = new FuelViewModel();
            RelativeVM = new RelativeViewModel(_classColorManager, _positionCalculator);
            DeltaBarVM = new DeltaBarViewModel();
            WarningsVM = new WarningsViewModel();
            CountdownVM = new CountdownViewModel();
            TrackTempVM = new TrackTempViewModel();
            TrackLocationVM = new TrackLocationViewModel();
        }

        public void RefreshElementVisibility()
        {
            OnPropertyChanged(nameof(ShowGear));
            OnPropertyChanged(nameof(ShowPosition));
            OnPropertyChanged(nameof(ShowTimeRemaining));
            OnPropertyChanged(nameof(ShowFuelRemaining));
            OnPropertyChanged(nameof(ShowLapDelta));
            OnPropertyChanged(nameof(ShowLapTimes));
            OnPropertyChanged(nameof(ShowRelative));
            OnPropertyChanged(nameof(ShowWarnings));
            OnPropertyChanged(nameof(ShowTrackLocationElement));
            OnPropertyChanged(nameof(ShowIncidentElement));
            OnPropertyChanged(nameof(ShowTrackTempElement));
            OnPropertyChanged(nameof(ScaleFactor));
        }

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, SessionView? sessionDataProvider)
        {
            bool leftCoolDown = CheckSessionStateTransitions(snapshot);
            bool sessionChanged = CheckForSessionTransition(snapshot, sessionDataProvider);

            // Leaving cool-down for the next session also changes the session number, on the same
            // frame: one reset covers both.
            if (leftCoolDown || sessionChanged)
                ResetSessionUI();

            if (sessionChanged && sessionDataProvider != null && sessionDataProvider.IsDataReady)
                CountdownVM.OnSessionTransition(sessionDataProvider, snapshot.SessionNum);

            _positionCalculator.Update(snapshot.ToPositionFrame(), sessionDataProvider);

            FuelVM.Update(snapshot.FuelLevel, snapshot.Lap);
            RelativeVM.Update(snapshot, sessionDataProvider);
            DeltaBarVM.Update(snapshot);
            CountdownVM.Update(snapshot, sessionDataProvider);
            TrackTempVM.Update(snapshot.TrackTempCrew, snapshot.SessionTime);

            if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
            {
                var playerCarIdx = snapshot.PlayerCarIdx;
                if (playerCarIdx >= 0)
                {
                    var incidentCounts = sessionDataProvider.CurDriverIncidentCount;
                    if (incidentCounts != null && playerCarIdx < incidentCounts.Length)
                    {
                        WarningsVM.UpdateIncidentCount(incidentCounts[playerCarIdx], sessionDataProvider.IncidentLimit);
                    }
                }

                TrackLocationVM.Update(snapshot, sessionDataProvider);

                UpdatePlayerPosition(snapshot, sessionDataProvider);
            }
            else
            {
                ClassPositionNumber = "--";
                OnPropertyChanged(nameof(ClassPositionNumber));
            }

            float lastLap = snapshot.LapLastLapTime;

            // Log the player's pit-road entry/exit transitions.
            int playerIdx = snapshot.PlayerCarIdx;
            if (playerIdx >= 0)
            {
                CheckPlayerPitRoadTransition(snapshot.PlayerOnPitRoad, snapshot.Lap);
            }

            UpdateLapTimeDisplays(lastLap, snapshot.LapBestLapTime);

            UpdateGearDisplay(snapshot);
            UpdateShiftIndicator(snapshot, sessionDataProvider);
        }

        #region --- Player Position Calculation ---

        private void UpdatePlayerPosition(SVappsLABSnapshot snapshot, SessionView dataProvider)
        {
            string newPosition;
            bool useOverall = _settingsManager.Settings.PositionDisplayMode == PositionDisplayMode.Overall;

            if (dataProvider.ShouldUseFastestLapPositioning())
            {
                var playerCarIdx = snapshot.PlayerCarIdx;
                var fastestLapData = dataProvider.FastestLapPositions;
                var playerData = fastestLapData.FirstOrDefault(d => d.carIdx == playerCarIdx);
                int position = useOverall ? playerData.overallPosition : playerData.classPosition;
                newPosition = position > 0 ? position.ToString() : "--";
            }
            else
            {
                var playerCarIdx = snapshot.PlayerCarIdx;
                var carClassIDs = dataProvider.CarClassIDs;

                if (playerCarIdx >= 0 && playerCarIdx < carClassIDs.Length)
                {
                    int playerClassId = carClassIDs[playerCarIdx];
                    int position = useOverall
                        ? _positionCalculator.GetOverallPosition(playerCarIdx)
                        : _positionCalculator.GetClassPosition(playerCarIdx, playerClassId);
                    newPosition = position > 0 ? position.ToString() : "--";
                }
                else
                {
                    newPosition = "--";
                }
            }

            if (ClassPositionNumber != newPosition)
            {
                Log.Info($"Player position changed: '{ClassPositionNumber}' -> '{newPosition}'");
                ClassPositionNumber = newPosition;
                OnPropertyChanged(nameof(ClassPositionNumber));
            }
        }

        // Logs the player crossing onto or off pit road. The first observation only seeds the
        // state (no log) so we don't emit a spurious "entered" when a session begins with the
        // player already in the pits.
        private void CheckPlayerPitRoadTransition(bool onPitRoad, int lap)
        {
            if (_playerWasOnPitRoad == onPitRoad) return;

            if (_playerWasOnPitRoad != null)
            {
                Log.Info(onPitRoad
                    ? $"Player entered pit road (lap {lap})"
                    : $"Player exited pit road (lap {lap})");
            }
            _playerWasOnPitRoad = onPitRoad;
        }

        #endregion

        private void UpdateGearDisplay(SVappsLABSnapshot snapshot)
        {
            int gear = snapshot.Gear;
            string newGearDisplay = gear switch { -1 => "R", 0 => "N", _ => gear.ToString() };
            if (GearDisplay != newGearDisplay)
            {
                GearDisplay = newGearDisplay;
                OnPropertyChanged(nameof(GearDisplay));
            }
        }

        private void UpdateShiftIndicator(SVappsLABSnapshot snapshot, SessionView? sessionDataProvider)
        {
            var car = sessionDataProvider != null && sessionDataProvider.IsDataReady ? sessionDataProvider.PlayerCar : null;

            bool onPitRoad = snapshot.PlayerOnPitRoad;

            // Learning runs whether or not the indicator is shown, so the shift points are ready
            // if the driver turns it on later.
            _shiftPoints.Update(snapshot, car, onPitRoad);

            int gear = snapshot.Gear;
            bool active = car != null && _settingsManager.Settings.ShowShiftIndicator
                && snapshot.IsOnTrack && !onPitRoad && !snapshot.PitLimiterOn && gear >= 1;

            int shiftRpm = 0, approachRpm = 0;
            if (active)
            {
                shiftRpm = _shiftPoints.GetShiftRpm(gear, snapshot, car);
                approachRpm = ShiftPointProvider.GetApproachRpm(shiftRpm, snapshot, car!);
            }

            var newState = _shiftCue.Update(snapshot.SessionTime, gear, snapshot.RPM, active,
                snapshot.RevLimiterActive, car?.RedLine ?? 0f, shiftRpm, approachRpm);

            if (newState != ShiftState)
            {
                ShiftState = newState;
                OnPropertyChanged(nameof(ShiftState));
            }

            // Calibration dot: status of the current gear's shift point; hidden when the
            // indicator is off or no shift point applies.
            var newCalibration = _settingsManager.Settings.ShowShiftIndicator
                ? _shiftPoints.GetCalibration(gear, car)
                : ShiftCalibration.None;
            if (newCalibration != ShiftCalibration)
            {
                ShiftCalibration = newCalibration;
                OnPropertyChanged(nameof(ShiftCalibration));
            }
        }

        /// <summary>Saves any unsaved shift-point learning. Called on disconnect and app exit.</summary>
        public void FlushShiftPoints() => _shiftPoints.Flush();

        /// <summary>
        /// Logs session-state changes. True when the session has just left cool-down for the
        /// next one (CoolDown -> GetInCar), which calls for a reset.
        /// </summary>
        private bool CheckSessionStateTransitions(SVappsLABSnapshot snapshot)
        {
            int currentSessionState = snapshot.SessionState;
            if (currentSessionState == _lastSessionState)
                return false;

            Log.Info($"Session state transition: {SessionStates.Name(_lastSessionState)} -> {SessionStates.Name(currentSessionState)}");

            bool leftCoolDown = _lastSessionState == SessionStates.CoolDown && currentSessionState == SessionStates.GetInCar;
            _lastSessionState = currentSessionState;
            return leftCoolDown;
        }

        /// <summary>
        /// True when the session number or the subsession has changed since the last frame,
        /// which calls for a reset.
        /// </summary>
        private bool CheckForSessionTransition(SVappsLABSnapshot snapshot, SessionView? sessionDataProvider)
        {
            int currentSessionNum = snapshot.SessionNum;

            // SubSessionID uniquely identifies the iRacing session and changes on any move to a
            // genuinely new one — including open practice -> a race weekend's practice, which can keep
            // SessionNum == 0 and so slip past the SessionNum check. Only meaningful once data is ready;
            // otherwise carry the last value forward so we don't read a transient empty as a change.
            string currentSubSessionId = (sessionDataProvider != null && sessionDataProvider.IsDataReady)
                ? sessionDataProvider.SubSessionId
                : _lastSubSessionId;

            bool sessionNumChanged = currentSessionNum != _lastSessionNum && _lastSessionNum != -1;
            // First non-empty reading only seeds _lastSubSessionId (empty -> set), never a transition.
            bool subSessionChanged = !string.IsNullOrEmpty(currentSubSessionId)
                && !string.IsNullOrEmpty(_lastSubSessionId)
                && currentSubSessionId != _lastSubSessionId;

            if (subSessionChanged)
            {
                Log.Info($"New subsession detected ({_lastSubSessionId} -> {currentSubSessionId}); resetting session UI");
            }

            _lastSessionNum = currentSessionNum;
            if (!string.IsNullOrEmpty(currentSubSessionId))
            {
                _lastSubSessionId = currentSubSessionId;
            }

            return sessionNumChanged || subSessionChanged;
        }

        /// <summary>
        /// Clear everything that belongs to one session. Used on a session change and on a
        /// disconnect.
        /// </summary>
        private void ResetSessionUI()
        {
            LastLapTime = LapTimePlaceholder;
            BestLapTime = LapTimePlaceholder;
            FuelVM.Reset();
            DeltaBarVM.Reset();
            RelativeVM.Reset();
            CountdownVM.Reset();
            WarningsVM.Reset();
            TrackTempVM.Reset();
            TrackLocationVM.Reset();
            GearDisplay = "N";
            ShiftState = ShiftState.Normal;
            ShiftCalibration = ShiftCalibration.None;
            _shiftCue.Reset();
            ClassPositionNumber = "--";
            _playerWasOnPitRoad = null;

            _classColorManager.Reset();
            _positionCalculator.Reset();

            OnPropertyChanged(string.Empty);
        }

        public void Reset()
        {
            _shiftPoints.Flush();
            _lastSessionNum = -1;
            _lastSessionState = -999;
            _lastSubSessionId = string.Empty;
            ResetSessionUI();
        }

        public ClassColorManager ClassColorManager => _classColorManager;

        private void UpdateLapTimeDisplays(float lastLap, float bestLap)
        {
            // Latch the last non-zero value rather than mirroring telemetry: iRacing intermittently
            // drops last/best lap to zero mid-session, and the display shouldn't flicker to the
            // placeholder. The latch is cleared explicitly on a real session change (ResetSessionUI,
            // driven by the checks at the top of UpdateFromTelemetry).
            if (lastLap > 0)
            {
                string last = FormatLapTime(lastLap);
                if (LastLapTime != last)
                {
                    LastLapTime = last;
                    OnPropertyChanged(nameof(LastLapTime));
                }
            }

            if (bestLap > 0)
            {
                string best = FormatLapTime(bestLap);
                if (BestLapTime != best)
                {
                    BestLapTime = best;
                    OnPropertyChanged(nameof(BestLapTime));
                }
            }
        }

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}