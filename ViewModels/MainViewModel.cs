using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        // --- Child ViewModels ---
        public FuelViewModel FuelVM { get; private set; }
        public RelativeViewModel RelativeVM { get; private set; }
        public DeltaBarViewModel DeltaBarVM { get; private set; }
        public WarningsViewModel WarningsVM { get; private set; }

        // --- Shared Services ---
        private readonly ClassColorManager _classColorManager;
        private readonly SettingsManager _settingsManager;

        // --- Session Tracking ---
        private int _lastSessionNum = -1;
        private int _lastSessionState = -999;
        private float _topSpeedBaseline = 0f; // For car health monitor

        // --- Debug Tracking for Session Timer ---
        private int _lastSessionLapsRemainOld = -1;
        private int _lastSessionLapsRemainEx = -1;
        private string _lastTimeRemainingDisplay = string.Empty;

        // --- Public Properties ---
        public string ClassPosition => RelativeVM.LivePlayerClassPosition;
        public string ClassPositionNumber => RelativeVM.LivePlayerClassPositionNumber;
        public string GearDisplay { get; private set; } = "N";
        public string LastLapTime { get; private set; } = "-:--.---";
        public string BestLapTime { get; private set; } = "-:--.---";
        public string TimeRemainingDisplay { get; private set; } = "--:--";
        public string TimeRemainingSymbol { get; private set; } = "⏳";

        // --- Settings Bridge Properties (for XAML binding) ---
        public bool ShowGear => _settingsManager.Settings.ShowRow0;
        public bool ShowPosition => _settingsManager.Settings.ShowRow0;
        public bool ShowTimeRemaining => _settingsManager.Settings.ShowRow1;
        public bool ShowFuelRemaining => _settingsManager.Settings.ShowRow1;
        public bool ShowLapDelta => _settingsManager.Settings.ShowRow2;
        public bool ShowLapTimes => _settingsManager.Settings.ShowRow3;
        public bool ShowRelative => _settingsManager.Settings.ShowRow4;
        public bool ShowWarnings => _settingsManager.Settings.ShowRow5;

        // Scale factor for LayoutTransform
        public double ScaleFactor => _settingsManager.Settings.WindowSize switch
        {
            WindowSizePreset.Small => 0.6,
            WindowSizePreset.Medium => 0.8,
            WindowSizePreset.Large => 1.0,
            _ => 1.0
        };

        public MainViewModel()
        {
            // Create shared services first
            _classColorManager = new ClassColorManager();
            _settingsManager = SettingsManager.Instance;

            // Create child view models with shared services
            FuelVM = new FuelViewModel();
            RelativeVM = new RelativeViewModel(_classColorManager);
            DeltaBarVM = new DeltaBarViewModel();
            WarningsVM = new WarningsViewModel();

            RelativeVM.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(RelativeViewModel.LivePlayerClassPosition))
                {
                    OnPropertyChanged(nameof(ClassPosition));
                }
                if (args.PropertyName == nameof(RelativeViewModel.LivePlayerClassPositionNumber))
                {
                    OnPropertyChanged(nameof(ClassPositionNumber));
                }
            };
        }

        private bool _isTelemetryConnected = false;
        public bool IsTelemetryConnected
        {
            get => _isTelemetryConnected;
            set { _isTelemetryConnected = value; OnPropertyChanged(); }
        }

        // Method to refresh all visibility bindings when settings change
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
            CheckForSessionTransition(snapshot);

            // Update child view models
            FuelVM.Update(snapshot.GetValue<float>("FuelLevel"), snapshot.GetValue<int>("Lap"));
            RelativeVM.Update(snapshot, sessionDataProvider);
            DeltaBarVM.Update(snapshot);

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
            }

            // --- Car Health & Lap Time Logic ---
            float currentSpeed = snapshot.GetValue<float>("Speed");
            if (currentSpeed > _topSpeedBaseline)
            {
                _topSpeedBaseline = currentSpeed;
            }

            float lastLap = snapshot.GetValue<float>("LapLastLapTime");
            if (lastLap > 0)
            {
                LastLapTime = FormatLapTime(lastLap);
                OnPropertyChanged(nameof(LastLapTime));

                bool onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad")?[snapshot.GetValue<int>("PlayerCarIdx")] ?? false;
                int lapsRemaining = snapshot.GetValue<int>("SessionLapsRemainEx");

                WarningsVM.CheckPace(lastLap, currentSpeed, _topSpeedBaseline, lapsRemaining, onPitRoad);
            }

            float bestLap = snapshot.GetValue<float>("LapBestLapTime");
            if (bestLap > 0)
            {
                BestLapTime = FormatLapTime(bestLap);
                OnPropertyChanged(nameof(BestLapTime));
            }

            UpdateGearDisplay(snapshot);
            UpdateSessionTimer(snapshot);
        }

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

        private void UpdateSessionTimer(SVappsLABSnapshot snapshot)
        {
            int sessionLapsRemainOld = snapshot.GetValue<int>("SessionLapsRemain", 0);
            int sessionLapsRemainEx = snapshot.GetValue<int>("SessionLapsRemainEx", 0);
            double timeRemain = snapshot.GetValue<double>("SessionTimeRemain", 0.0);
            int sessionNum = snapshot.GetValue<int>("SessionNum", -1);

            // Debug logging only when values change
            if (sessionLapsRemainOld != _lastSessionLapsRemainOld ||
                sessionLapsRemainEx != _lastSessionLapsRemainEx)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionTimer] SessionNum: {sessionNum}, LapsRemainOld: {sessionLapsRemainOld}, LapsRemainEx: {sessionLapsRemainEx}, TimeRemain: {timeRemain:F1}s");
                _lastSessionLapsRemainOld = sessionLapsRemainOld;
                _lastSessionLapsRemainEx = sessionLapsRemainEx;
            }

            string newTimeRemainingDisplay;

            // Use the new SessionLapsRemainEx and remove qualifying exclusion
            if (sessionLapsRemainEx > 0 && sessionLapsRemainEx < 10000)
            {
                TimeRemainingSymbol = "🏁";
                newTimeRemainingDisplay = (sessionLapsRemainEx == 1) ? "Final Lap" : $"{sessionLapsRemainEx} Laps";
            }
            else if (timeRemain > 0)
            {
                TimeRemainingSymbol = "⏳";
                TimeSpan remaining = TimeSpan.FromSeconds(timeRemain);
                if (remaining.TotalHours >= 1.0)
                    newTimeRemainingDisplay = $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                else
                    newTimeRemainingDisplay = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
            }
            else
            {
                newTimeRemainingDisplay = "--:--";
            }

            // Only log display changes
            if (newTimeRemainingDisplay != _lastTimeRemainingDisplay)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionTimer] Display changed to: {newTimeRemainingDisplay}");
                _lastTimeRemainingDisplay = newTimeRemainingDisplay;
            }

            TimeRemainingDisplay = newTimeRemainingDisplay;
            OnPropertyChanged(nameof(TimeRemainingDisplay));
            OnPropertyChanged(nameof(TimeRemainingSymbol));
        }

        private void CheckSessionStateTransitions(SVappsLABSnapshot snapshot)
        {
            int currentSessionState = snapshot.GetValue<int>("SessionState", -1);
            if (currentSessionState != _lastSessionState)
            {
                if (_lastSessionState == 6 && currentSessionState == 1) // CoolDown -> GetInCar
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
            GearDisplay = "N";
            TimeRemainingDisplay = "--:--";
            TimeRemainingSymbol = "⏳";

            // Reset shared services
            _classColorManager.Reset();

            // Reset debug tracking
            _lastSessionLapsRemainOld = -1;
            _lastSessionLapsRemainEx = -1;
            _lastTimeRemainingDisplay = string.Empty;

            OnPropertyChanged(string.Empty); // Update all properties
        }

        private void CheckForSessionTransition(SVappsLABSnapshot snapshot)
        {
            int currentSessionNum = snapshot.GetValue<int>("SessionNum", -1);
            if (currentSessionNum != _lastSessionNum && _lastSessionNum != -1)
            {
                ResetSessionData();
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
            OnPropertyChanged(string.Empty); // Update all properties
        }

        public void Reset()
        {
            _lastSessionNum = -1;
            _lastSessionState = -999;
            ClearSessionUI();
            WarningsVM.Reset();
            // ClassColorManager reset is handled in ClearSessionUI()
        }

        /// <summary>
        /// Provides access to the shared ClassColorManager for other components.
        /// Used by RadarWindow to get the same color manager instance.
        /// </summary>
        public ClassColorManager ClassColorManager => _classColorManager;

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}