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

        private readonly ClassColorManager _classColorManager;
        private readonly SettingsManager _settingsManager;
        private readonly PositionCalculator _positionCalculator;

        private int _lastSessionNum = -1;
        private int _lastSessionState = -999;
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

                UpdatePlayerPosition(snapshot, sessionDataProvider);
            }
            else
            {
                ClassPositionNumber = "--";
                OnPropertyChanged(nameof(ClassPositionNumber));
            }

            float lastLap = snapshot.LapLastLapTime;

            // Vehicle-health warnings are fed every frame. WarningsViewModel gates its lap-level math
            // (baseline, PIT economics) to once per completed lap internally, and runs the responsive
            // sub-lap segment and contact logic on the per-frame stream.
            int playerIdx = snapshot.PlayerCarIdx;
            if (playerIdx >= 0)
            {
                var lapDistArr = snapshot.CarIdxLapDistPct;
                var pitRoadArr = snapshot.CarIdxOnPitRoad;
                float lapDistPct = playerIdx < lapDistArr.Length ? lapDistArr[playerIdx] : 0f;
                bool onPitRoad = playerIdx < pitRoadArr.Length && pitRoadArr[playerIdx];
                int lapsRemaining = snapshot.SessionLapsRemain;

                CheckPlayerPitRoadTransition(onPitRoad, snapshot.Lap);

                // Green racing only: SessionState 4 is Racing; the caution bits mark a full-course
                // yellow, during which lap times are pace-distorted and not a health signal.
                const int CAUTION_FLAGS = 0x4000 | 0x8000; // irsdk caution | cautionWaving
                bool isRacingGreen = snapshot.SessionState == 4 && (snapshot.SessionFlags & CAUTION_FLAGS) == 0;

                WarningsVM.Update(snapshot.Lap, lapDistPct, snapshot.LapCurrentLapTime, lastLap,
                    snapshot.Speed, lapsRemaining, snapshot.SessionTimeRemain, onPitRoad, isRacingGreen);
            }

            // Lap-time fields mirror telemetry directly (like the rest of the live UI) instead of
            // latching the last non-zero value. iRacing zeroes LapLastLapTime/LapBestLapTime at every
            // session boundary, so following them clears the display on any new session — including
            // transitions (e.g. open practice -> session practice) that don't change SessionNum and so
            // never hit the ResetSessionData path.
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
            if (currentSessionNum != _lastSessionNum && _lastSessionNum != -1)
            {
                ResetSessionData();

                if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
                {
                    CountdownVM.OnSessionTransition(sessionDataProvider, currentSessionNum);
                }
            }
            _lastSessionNum = currentSessionNum;
        }

        private void ResetSessionData()
        {
            LastLapTime = LapTimePlaceholder;
            BestLapTime = LapTimePlaceholder;
            DeltaBarVM.Reset();
            FuelVM.Reset();
            WarningsVM.Reset();
            CountdownVM.Reset();
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
            ClearSessionUI();
            WarningsVM.Reset();
        }

        public ClassColorManager ClassColorManager => _classColorManager;

        private void UpdateLapTimeDisplays(float lastLap, float bestLap)
        {
            string last = lastLap > 0 ? FormatLapTime(lastLap) : LapTimePlaceholder;
            if (LastLapTime != last)
            {
                LastLapTime = last;
                OnPropertyChanged(nameof(LastLapTime));
            }

            string best = bestLap > 0 ? FormatLapTime(bestLap) : LapTimePlaceholder;
            if (BestLapTime != best)
            {
                BestLapTime = best;
                OnPropertyChanged(nameof(BestLapTime));
            }
        }

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}