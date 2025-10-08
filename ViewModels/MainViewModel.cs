using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
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

        private int _lastSessionNum = -1;
        private int _lastSessionState = -999;
        private float _topSpeedBaseline = 0f;
        private float _currentLapTopSpeed = 0f;
        private float _lastLapTopSpeed = 0f;

        public string ClassPositionNumber { get; private set; } = "--";
        public string GearDisplay { get; private set; } = "N";
        public string LastLapTime { get; private set; } = "-:--.---";
        public string BestLapTime { get; private set; } = "-:--.---";

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
            FuelVM = new FuelViewModel();
            RelativeVM = new RelativeViewModel(_classColorManager);
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

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            CheckSessionStateTransitions(snapshot);
            CheckForSessionTransition(snapshot, sessionDataProvider);

            FuelVM.Update(snapshot.GetValue<float>("FuelLevel"), snapshot.GetValue<int>("Lap"));
            RelativeVM.Update(snapshot, sessionDataProvider);
            DeltaBarVM.Update(snapshot);
            CountdownVM.Update(snapshot, sessionDataProvider);

            if (sessionDataProvider != null && sessionDataProvider.IsDataReady)
            {
                var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx", -1);
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

            float currentSpeed = snapshot.GetValue<float>("Speed");
            if (currentSpeed > _topSpeedBaseline)
            {
                _topSpeedBaseline = currentSpeed;
            }
            if (currentSpeed > _currentLapTopSpeed)
            {
                _currentLapTopSpeed = currentSpeed;
            }

            float lastLap = snapshot.GetValue<float>("LapLastLapTime");
            if (lastLap > 0)
            {
                LastLapTime = FormatLapTime(lastLap);
                OnPropertyChanged(nameof(LastLapTime));

                _lastLapTopSpeed = _currentLapTopSpeed;
                _currentLapTopSpeed = 0f;

                bool onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad")?[snapshot.GetValue<int>("PlayerCarIdx")] ?? false;
                int lapsRemaining = snapshot.GetValue<int>("SessionLapsRemain");

                WarningsVM.CheckPace(lastLap, _lastLapTopSpeed, _topSpeedBaseline, lapsRemaining, onPitRoad);
            }

            float bestLap = snapshot.GetValue<float>("LapBestLapTime");
            if (bestLap > 0)
            {
                BestLapTime = FormatLapTime(bestLap);
                OnPropertyChanged(nameof(BestLapTime));
            }

            UpdateGearDisplay(snapshot);
        }

        #region --- Player Position Calculation ---

        private void UpdatePlayerPosition(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            // Position display logic depends on session type:
            // - Practice/Qualifying: Use fastest lap position from YAML
            // - Race: Use calculated real-time position from RelativeVM

            string newPosition;

            if (dataProvider.ShouldUseFastestLapPositioning())
            {
                // Practice/Qualifying - get position from YAML fastest lap data
                var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx", -1);
                var fastestLapData = dataProvider.GetFastestLapPositioning();
                var playerData = fastestLapData.FirstOrDefault(d => d.carIdx == playerCarIdx);
                newPosition = playerData.position > 0 ? playerData.position.ToString() : "--";
            }
            else
            {
                // Race - use calculated position from RelativeVM
                var playerRow = RelativeVM.RelativeRows.FirstOrDefault(r => r.IsPlayer);
                newPosition = playerRow?.ClassPos ?? "--";
            }

            if (ClassPositionNumber != newPosition)
            {
                ClassPositionNumber = newPosition;
                OnPropertyChanged(nameof(ClassPositionNumber));
            }
        }

        #endregion

        private void UpdateGearDisplay(SVappsLABSnapshot snapshot)
        {
            int gear = snapshot.GetValue<int>("Gear");
            string newGearDisplay = gear switch { -1 => "R", 0 => "N", _ => gear.ToString() };
            if (GearDisplay != newGearDisplay)
            {
                GearDisplay = newGearDisplay;
                OnPropertyChanged(nameof(GearDisplay));
            }
        }

        private void CheckSessionStateTransitions(SVappsLABSnapshot snapshot)
        {
            int currentSessionState = snapshot.GetValue<int>("SessionState", -1);
            if (currentSessionState != _lastSessionState)
            {
                if (_lastSessionState == 6 && currentSessionState == 1)
                {
                    ClearSessionUI();
                }
                _lastSessionState = currentSessionState;
            }
        }

        private void ClearSessionUI()
        {
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            _topSpeedBaseline = 0f;
            FuelVM.Reset();
            DeltaBarVM.Reset();
            RelativeVM.Reset();
            CountdownVM.Reset();
            GearDisplay = "N";
            ClassPositionNumber = "--";

            _classColorManager.Reset();

            OnPropertyChanged(string.Empty);
        }

        private void CheckForSessionTransition(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);
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
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            _topSpeedBaseline = 0f;
            DeltaBarVM.Reset();
            FuelVM.Reset();
            WarningsVM.Reset();
            CountdownVM.Reset();
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

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}