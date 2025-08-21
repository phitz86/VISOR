using System;
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

        // --- State Properties ---
        private bool _isTelemetryConnected = false;
        public bool IsTelemetryConnected
        {
            get => _isTelemetryConnected;
            set { _isTelemetryConnected = value; OnPropertyChanged(); }
        }

        // --- Visibility Toggle Properties (for UI sections) ---
        private bool _showPosition = true;
        private bool _showGear = true;
        private bool _showFuelRemaining = true;
        private bool _showTimeRemaining = true;
        private bool _showLapDelta = true;
        private bool _showLapTimes = true;
        private bool _showRelative = true;
        private bool _showWarnings = true;

        public bool ShowPosition { get => _showPosition; set { _showPosition = value; OnPropertyChanged(); } }
        public bool ShowGear { get => _showGear; set { _showGear = value; OnPropertyChanged(); } }
        public bool ShowFuelRemaining { get => _showFuelRemaining; set { _showFuelRemaining = value; OnPropertyChanged(); } }
        public bool ShowTimeRemaining { get => _showTimeRemaining; set { _showTimeRemaining = value; OnPropertyChanged(); } }
        public bool ShowLapDelta { get => _showLapDelta; set { _showLapDelta = value; OnPropertyChanged(); } }
        public bool ShowLapTimes { get => _showLapTimes; set { _showLapTimes = value; OnPropertyChanged(); } }
        public bool ShowRelative { get => _showRelative; set { _showRelative = value; OnPropertyChanged(); } }
        public bool ShowWarnings { get => _showWarnings; set { _showWarnings = value; OnPropertyChanged(); } }

        // --- Position Properties ---
        public string ClassPosition => RelativeVM.LivePlayerClassPosition;
        public string ClassPositionNumber => RelativeVM.LivePlayerClassPositionNumber;

        // --- Other Data Properties ---
        private string _gearDisplay = "N";
        private string _lastLapTime = "-:--.---";
        private string _bestLapTime = "-:--.---";
        private string _timeRemainingDisplay = "--:--";

        public string GearDisplay { get => _gearDisplay; set { _gearDisplay = value; OnPropertyChanged(); } }
        public string LastLapTime { get => _lastLapTime; set { _lastLapTime = value; OnPropertyChanged(); } }
        public string BestLapTime { get => _bestLapTime; set { _bestLapTime = value; OnPropertyChanged(); } }
        public string TimeRemainingDisplay { get => _timeRemainingDisplay; set { _timeRemainingDisplay = value; OnPropertyChanged(); } }

        public MainViewModel()
        {
            FuelVM = new FuelViewModel();
            RelativeVM = new RelativeViewModel();
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

        // Updated to accept either SessionDataParser or SessionDataWrapper
        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            // Update fuel calculation (doesn't need session data)
            FuelVM.Update(
                snapshot.GetValue<float>("FuelLevel"),
                snapshot.GetValue<int>("Lap")
            );

            // Update relative positioning (needs session data)
            RelativeVM.Update(snapshot, sessionDataProvider);

            // Update gear display
            int gear = snapshot.GetValue<int>("Gear");
            GearDisplay = gear switch
            {
                -1 => "R",
                0 => "N",
                _ => gear.ToString()
            };

            // Update lap times
            float lastLap = snapshot.GetValue<float>("LapLastLapTime");
            if (lastLap > 0) LastLapTime = FormatLapTime(lastLap);

            float bestLap = snapshot.GetValue<float>("LapBestLapTime");
            if (bestLap > 0) BestLapTime = FormatLapTime(bestLap);

            // Update time remaining
            double timeRemain = snapshot.GetValue<double>("SessionTimeRemain");
            if (timeRemain > 0)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(timeRemain);
                TimeRemainingDisplay = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
            }
            else
            {
                TimeRemainingDisplay = "--:--";
            }
        }

        public void Reset()
        {
            FuelVM.Reset();
            RelativeVM.Reset();
            GearDisplay = "N";
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            TimeRemainingDisplay = "--:--";
            IsTelemetryConnected = false;
        }

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }

    // Interface to allow both SessionDataParser and SessionDataWrapper to be used
    public interface ISessionDataProvider
    {
        bool IsDataReady { get; }
        string[] UserNames { get; }
        string[] CarNumbers { get; }
        int[] CarNumberRaw { get; }
        int[] CarClassIDs { get; }
        bool[] CarIsAI { get; }
        int[] CurDriverIncidentCount { get; }
    }
}