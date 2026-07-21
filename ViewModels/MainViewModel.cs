using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using VISOR.Diagnostics;
using VISOR.Telemetry;
using VISOR.Views;
using VISOR.Settings;

namespace VISOR.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

        private int _lastSessionNum = -1;
        private int _lastSessionState = -999;
        private string _lastSubSessionId = string.Empty;
        private bool? _playerWasOnPitRoad = null;

        private const string LapTimePlaceholder = "-:--.---";

        public string ClassPositionNumber { get; private set; } = "--";
        public string GearDisplay { get; private set; } = "N";
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

        public double ScaleFactor => _settingsManager.Settings.WindowSize switch
        {
            WindowSizePreset.Small => 0.6,
            WindowSizePreset.Medium => 0.8,
            WindowSizePreset.Large => 1.0,
            _ => 1.0
        };

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

        private bool _isTelemetryConnected = false;
        public bool IsTelemetryConnected
        {
            get => _isTelemetryConnected;
            set { _isTelemetryConnected = value; OnPropertyChanged(); }
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

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, ISessionDataProvider? sessionDataProvider)
        {
            CheckSessionStateTransitions(snapshot);
            CheckForSessionTransition(snapshot, sessionDataProvider);

            _positionCalculator.Update(snapshot, sessionDataProvider);

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
                var pitRoadArr = snapshot.CarIdxOnPitRoad;
                bool onPitRoad = playerIdx < pitRoadArr.Length && pitRoadArr[playerIdx];
                CheckPlayerPitRoadTransition(onPitRoad, snapshot.Lap);
            }

            UpdateLapTimeDisplays(lastLap, snapshot.LapBestLapTime);

            UpdateGearDisplay(snapshot);
        }

        #region --- Player Position Calculation ---

        private void UpdatePlayerPosition(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            string newPosition;
            bool useOverall = _settingsManager.Settings.PositionDisplayMode == PositionDisplayMode.Overall;

            if (dataProvider.ShouldUseFastestLapPositioning())
            {
                var playerCarIdx = snapshot.PlayerCarIdx;
                var fastestLapData = dataProvider.GetFastestLapPositioning();
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

        private static string GetStateName(int state) => state switch
        {
            0 => "Invalid",
            1 => "GetInCar",
            2 => "Warmup",
            3 => "ParadeLaps",
            4 => "Racing",
            5 => "Checkered",
            6 => "CoolDown",
            _ => $"Unknown({state})"
        };

        private void CheckSessionStateTransitions(SVappsLABSnapshot snapshot)
        {
            int currentSessionState = snapshot.SessionState;
            if (currentSessionState != _lastSessionState)
            {
                Log.Info($"Session state transition: {GetStateName(_lastSessionState)} -> {GetStateName(currentSessionState)}");

                if (_lastSessionState == 6 && currentSessionState == 1)
                {
                    ClearSessionUI();
                }
                _lastSessionState = currentSessionState;
            }
        }

        private void ClearSessionUI()
        {
            LastLapTime = LapTimePlaceholder;
            BestLapTime = LapTimePlaceholder;
            FuelVM.Reset();
            DeltaBarVM.Reset();
            RelativeVM.Reset();
            CountdownVM.Reset();
            GearDisplay = "N";
            ClassPositionNumber = "--";
            _playerWasOnPitRoad = null;

            _classColorManager.Reset();
            _positionCalculator.Reset();

            OnPropertyChanged(string.Empty);
        }

        private void CheckForSessionTransition(SVappsLABSnapshot snapshot, ISessionDataProvider? sessionDataProvider)
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

            if (sessionNumChanged || subSessionChanged)
            {
                if (subSessionChanged)
                {
                    Log.Info($"New subsession detected ({_lastSubSessionId} -> {currentSubSessionId}); resetting session UI");
                }

                ResetSessionData();

                if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
                {
                    CountdownVM.OnSessionTransition(sessionDataProvider, currentSessionNum);
                }
            }

            _lastSessionNum = currentSessionNum;
            if (!string.IsNullOrEmpty(currentSubSessionId))
            {
                _lastSubSessionId = currentSubSessionId;
            }
        }

        private void ResetSessionData()
        {
            LastLapTime = LapTimePlaceholder;
            BestLapTime = LapTimePlaceholder;
            DeltaBarVM.Reset();
            FuelVM.Reset();
            WarningsVM.Reset();
            CountdownVM.Reset();
            TrackTempVM.Reset();
            TrackLocationVM.Reset();
            RelativeVM.Reset();
            _playerWasOnPitRoad = null;
            _classColorManager.Reset();
            _positionCalculator.Reset();
            OnPropertyChanged(string.Empty);
        }

        public void Reset()
        {
            _lastSessionNum = -1;
            _lastSessionState = -999;
            _lastSubSessionId = string.Empty;
            ClearSessionUI();
            WarningsVM.Reset();
            TrackTempVM.Reset();
            TrackLocationVM.Reset();
        }

        public ClassColorManager ClassColorManager => _classColorManager;

        private void UpdateLapTimeDisplays(float lastLap, float bestLap)
        {
            // Latch the last non-zero value rather than mirroring telemetry: iRacing intermittently
            // drops last/best lap to zero mid-session, and the display shouldn't flicker to the
            // placeholder. The latch is cleared explicitly on a real session change (ResetSessionData,
            // driven by SessionNum or SubSessionID in CheckForSessionTransition).
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