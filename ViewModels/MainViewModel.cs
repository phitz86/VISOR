using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VISOR.Telemetry;

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

        // --- Position Properties ---
        public string ClassPosition => RelativeVM.LivePlayerClassPosition;
        public string ClassPositionNumber => RelativeVM.LivePlayerClassPositionNumber;

        // --- Other Data Properties ---
        private string _gearDisplay = "N";
        private string _lastLapTime = "-:--.---";
        private string _bestLapTime = "-:--.---";
        public string GearDisplay { get => _gearDisplay; set { _gearDisplay = value; OnPropertyChanged(); } }
        public string LastLapTime { get => _lastLapTime; set { _lastLapTime = value; OnPropertyChanged(); } }
        public string BestLapTime { get => _bestLapTime; set { _bestLapTime = value; OnPropertyChanged(); } }

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

        public void UpdateFromTelemetry(SVappsLABSnapshot snapshot, SessionDataParser sessionParser)
        {
            // This logic will now only run after the connection is established.
            FuelVM.Update(
                snapshot.GetValue<float>("FuelLevel"),
                snapshot.GetValue<int>("Lap")
            );
            RelativeVM.Update(snapshot, sessionParser);

            int gear = snapshot.GetValue<int>("Gear");
            GearDisplay = gear switch
            {
                -1 => "R",
                0 => "N",
                _ => gear.ToString()
            };

            float lastLap = snapshot.GetValue<float>("LapLastLapTime");
            if (lastLap > 0) LastLapTime = FormatLapTime(lastLap);

            float bestLap = snapshot.GetValue<float>("LapBestLapTime");
            if (bestLap > 0) BestLapTime = FormatLapTime(bestLap);
        }

        // THIS METHOD WAS MISSING
        public void Reset()
        {
            FuelVM.Reset();
            RelativeVM.Reset();
            GearDisplay = "N";
            LastLapTime = "-:--.---";
            BestLapTime = "-:--.---";
            IsTelemetryConnected = false;
        }

        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}
