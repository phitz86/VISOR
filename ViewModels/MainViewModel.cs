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

        // --- Session Transition Tracking ---
        private int _lastSessionNum = -1;
        private string _lastSessionType = string.Empty;

        // --- SessionState Debug Tracking ---
        private int _lastSessionState = -999;
        private DateTime _lastSessionStateChange = DateTime.MinValue;
        private readonly Dictionary<int, string> _sessionStateNames = new Dictionary<int, string>
        {
            { 0, "Invalid" }, { 1, "GetInCar" }, { 2, "Warmup" }, { 3, "ParadeLaps" },
            { 4, "Racing" }, { 5, "Checkered" }, { 6, "CoolDown" }
        };

        // --- State Properties ---
        private bool _isTelemetryConnected = false;
        public bool IsTelemetryConnected
        {
            get => _isTelemetryConnected;
            set { _isTelemetryConnected = value; OnPropertyChanged(); }
        }

        // --- Visibility Toggle Properties (for UI sections) ---
        public bool ShowPosition { get; set; } = true;
        public bool ShowGear { get; set; } = true;
        public bool ShowFuelRemaining { get; set; } = true;
        public bool ShowTimeRemaining { get; set; } = true;
        public bool ShowLapDelta { get; set; } = true;
        public bool ShowLapTimes { get; set; } = true;
        public bool ShowRelative { get; set; } = true;
        public bool ShowWarnings { get; set; } = true;

        // --- Position Properties ---
        public string ClassPosition => RelativeVM.LivePlayerClassPosition;
        public string ClassPositionNumber => RelativeVM.LivePlayerClassPositionNumber;

        // --- Other Data Properties ---
        private string _gearDisplay = "N";
        private string _lastLapTime = "-:--.---";
        private string _bestLapTime = "-:--.---";
        private string _timeRemainingDisplay = "--:--";
        private string _timeRemainingSymbol = "⏳";

        public string GearDisplay { get => _gearDisplay; set { _gearDisplay = value; OnPropertyChanged(); } }
        public string LastLapTime { get => _lastLapTime; set { _lastLapTime = value; OnPropertyChanged(); } }
        public string BestLapTime { get => _bestLapTime; set { _bestLapTime = value; OnPropertyChanged(); } }
        public string TimeRemainingDisplay { get => _timeRemainingDisplay; set { _timeRemainingDisplay = value; OnPropertyChanged(); } }
        public string TimeRemainingSymbol { get => _timeRemainingSymbol; set { _timeRemainingSymbol = value; OnPropertyChanged(); } }

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

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            CheckSessionStateTransitions(snapshot);
            CheckForSessionTransition(snapshot);

            FuelVM.Update(snapshot.GetValue<float>("FuelLevel"), snapshot.GetValue<int>("Lap"));
            RelativeVM.Update(snapshot, sessionDataProvider);
            DeltaBarVM.Update(snapshot);

            // REMOVED: The call to WarningsVM.UpdateConnectionHealth() has been deleted.
            // The new performance timer in WarningsViewModel handles its own updates.

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

            int gear = snapshot.GetValue<int>("Gear");
            GearDisplay = gear switch { -1 => "R", 0 => "N", _ => gear.ToString() };

            float lastLap = snapshot.GetValue<float>("LapLastLapTime");
            if (lastLap > 0) LastLapTime = FormatLapTime(lastLap);

            float bestLap = snapshot.GetValue<float>("LapBestLapTime");
            if (bestLap > 0) BestLapTime = FormatLapTime(bestLap);

            UpdateSessionTimer(snapshot);
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
                if (remaining.TotalMinutes < 1.0)
                {
                    TimeRemainingDisplay = $"{remaining.Seconds:D2}s";
                }
                else if (remaining.TotalHours >= 1.0)
                {
                    TimeRemainingDisplay = $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                }
                else
                {
                    TimeRemainingDisplay = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
                }
            }
            else
            {
                TimeRemainingDisplay = "--:--";
            }
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
                _lastSessionStateChange = DateTime.Now;
            }
        }

        private void ClearSessionUI()
        {
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            FuelVM.Reset();
            DeltaBarVM.Reset();
            RelativeVM.Reset();
            GearDisplay = "N";
            TimeRemainingDisplay = "--:--";
            TimeRemainingSymbol = "⏳";
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
            DeltaBarVM.Reset();
            FuelVM.Reset();
            WarningsVM.Reset();
        }

        public void Reset()
        {
            FuelVM.Reset();
            RelativeVM.Reset();
            DeltaBarVM.Reset();
            WarningsVM.Reset();
            GearDisplay = "N";
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            TimeRemainingDisplay = "--:--";
            TimeRemainingSymbol = "⏳";
            IsTelemetryConnected = false;
            _lastSessionState = -999;
            _lastSessionStateChange = DateTime.MinValue;
        }

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}