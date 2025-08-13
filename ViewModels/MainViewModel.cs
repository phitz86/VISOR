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

        // Properties for UI visibility (to be controlled by ConfigWindow later)
        public bool ShowPosition { get; set; } = true;
        public bool ShowGear { get; set; } = true;
        public bool ShowFuelRemaining { get; set; } = true;
        public bool ShowTimeRemaining { get; set; } = true;
        public bool ShowLapDelta { get; set; } = true;
        public bool ShowLapTimes { get; set; } = true;
        public bool ShowRelative { get; set; } = true;
        public bool ShowWarnings { get; set; } = true;

        // --- Backing fields for telemetry data ---
        private string _classPosition = "P--";
        private string _gearDisplay = "N";
        private string _fuelDisplay = "--- L";
        private string _timeRemainingDisplay = "--:--";
        private string _lastLapTime = "-:--.---";
        private string _bestLapTime = "-:--.---";

        // --- Public properties bound by the UI ---
        public string ClassPosition { get => _classPosition; set { _classPosition = value; OnPropertyChanged(); } }
        public string GearDisplay { get => _gearDisplay; set { _gearDisplay = value; OnPropertyChanged(); } }
        public string FuelDisplay { get => _fuelDisplay; set { _fuelDisplay = value; OnPropertyChanged(); } }
        public string TimeRemainingDisplay { get => _timeRemainingDisplay; set { _timeRemainingDisplay = value; OnPropertyChanged(); } }
        public string LastLapTime { get => _lastLapTime; set { _lastLapTime = value; OnPropertyChanged(); } }
        public string BestLapTime { get => _bestLapTime; set { _bestLapTime = value; OnPropertyChanged(); } }

        /// <summary>
        /// Updates the ViewModel's properties from a new telemetry snapshot.
        /// This method is called frequently, so it should be efficient.
        /// </summary>
        public void UpdateFromTelemetry(SVappsLABSnapshot s)
        {
            // --- Update simple properties ---

            // Player Position in Class
            int playerCarIdx = s.GetValue<int>("PlayerCarIdx");
            if (playerCarIdx != -1)
            {
                var classPositions = s.GetValue<int[]>("CarIdxClassPosition");
                if (classPositions != null && playerCarIdx < classPositions.Length)
                {
                    ClassPosition = $"P{classPositions[playerCarIdx]}";
                }
            }

            // Gear
            int gear = s.GetValue<int>("Gear");
            GearDisplay = gear switch
            {
                -1 => "R",
                0 => "N",
                _ => gear.ToString()
            };

            // Last Lap Time
            float lastLap = s.GetValue<float>("LapLastLapTime");
            if (lastLap > 0)
            {
                LastLapTime = FormatLapTime(lastLap);
            }

            // Best Lap Time
            float bestLap = s.GetValue<float>("LapBestLapTime");
            if (bestLap > 0)
            {
                BestLapTime = FormatLapTime(bestLap);
            }

            // Note: Fuel, Time Remaining, and other complex fields will be handled
            // by their own dedicated ViewModels later. This is a starting point.
        }

        /// <summary>
        /// Helper method to format a lap time from seconds into a M:SS.mmm string.
        /// </summary>
        private string FormatLapTime(float timeInSeconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(timeInSeconds);
            return $"{time.Minutes}:{time.Seconds:D2}.{time.Milliseconds:D3}";
        }
    }
}
