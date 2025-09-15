using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using VISOR.Telemetry;
using VISOR.Views;

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

        // --- Session Tracking ---
        private int _lastSessionNum = -1;
        private int _lastSessionState = -999;
        private float _topSpeedBaseline = 0f; // For car health monitor

        // --- Public Properties ---
        public string ClassPosition => RelativeVM.LivePlayerClassPosition;
        public string ClassPositionNumber => RelativeVM.LivePlayerClassPositionNumber;
        public string GearDisplay { get; private set; } = "N";
        public string LastLapTime { get; private set; } = "-:--.---";
        public string BestLapTime { get; private set; } = "-:--.---";
        public string TimeRemainingDisplay { get; private set; } = "--:--";
        public string TimeRemainingSymbol { get; private set; } = "⏳";

        public MainViewModel()
        {
            FuelVM = new FuelViewModel();
            RelativeVM = new RelativeViewModel();
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
                int lapsRemaining = snapshot.GetValue<int>("SessionLapsRemain");

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
            int sessionLapsRemain = snapshot.GetValue<int>("SessionLapsRemain", 0);
            double timeRemain = snapshot.GetValue<double>("SessionTimeRemain", 0.0);
            bool isQualifying = snapshot.GetValue<int>("SessionNum", -1) == 1;

            if (!isQualifying && sessionLapsRemain > 0 && sessionLapsRemain < 10000)
            {
                TimeRemainingSymbol = "🏁";
                TimeRemainingDisplay = (sessionLapsRemain == 1) ? "Final Lap" : $"{sessionLapsRemain} Laps";
            }
            else if (timeRemain > 0)
            {
                TimeRemainingSymbol = "⏳";
                TimeSpan remaining = TimeSpan.FromSeconds(timeRemain);
                if (remaining.TotalHours >= 1.0)
                    TimeRemainingDisplay = $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                else
                    TimeRemainingDisplay = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
            }
            else
            {
                TimeRemainingDisplay = "--:--";
            }
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
        }

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}